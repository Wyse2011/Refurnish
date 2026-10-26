using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Refurnish.Inventory;
using Refurnish.Planning;

namespace Refurnish.Automation;

public readonly record struct PlaceJob(ulong HouseId, int Container, int Slot, uint ItemId, byte Stain)
{
    public (int, int) SlotKey => (Container, Slot);
}

/// <summary>
/// Places furniture from the storeroom or your inventory, one item at a time. Before every item
/// it re-plans from a live read (is it still needed, and where does it go?), checks the slot
/// still holds that exact item, and checks a learned message exists for this room, source and game
/// version. After sending, it waits for the game to confirm the item really was placed.
/// Any failure or timeout stops the run; nothing is retried.
/// </summary>
public sealed class PlaceExecutor
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ClientWait = TimeSpan.FromSeconds(5);

    private readonly Plugin plugin;
    private readonly Queue<PlaceJob> queue = new();
    private readonly Dictionary<(int, int), (JobState State, string? Note)> states = new();

    private PlaceJob? current;
    private int currentContainer;
    private int currentSlot;
    private int placedCountBefore;
    private DateTime sentUtc;
    private DateTime nextAllowedUtc = DateTime.MinValue;
    private DateTime waitingForClientSinceUtc = DateTime.MinValue;
    private bool holdingRecorder;

    public bool Running => current != null || queue.Count > 0;
    public int DoneThisRun { get; private set; }
    public int TotalThisRun { get; private set; }
    public string? StopReason { get; private set; }

    public PlaceExecutor(Plugin plugin) => this.plugin = plugin;

    public (JobState State, string? Note)? StateOf(int container, int slot) =>
        states.TryGetValue((container, slot), out var s) ? s : null;

    public static PlaceSource SourceOf(int container) =>
        LiveInventory.InteriorStoreroom.Contains(container) ? PlaceSource.Storeroom : PlaceSource.Inventory;

    public PlaceProfile? ProfileFor(ulong houseId, PlaceSource source) =>
        plugin.Configuration.PlaceProfiles.FirstOrDefault(p => p.Source == source && p.IsValidFor(houseId, Plugin.GameVersion));

    public void Enqueue(IEnumerable<PlaceJob> jobs)
    {
        if (!Running)
        {
            foreach (var key in states.Where(kv => kv.Value.State is JobState.Done or JobState.Failed).Select(kv => kv.Key).ToList())
                states.Remove(key);
            DoneThisRun = 0;
            TotalThisRun = 0;
            StopReason = null;
        }

        foreach (var job in jobs)
        {
            if (states.TryGetValue(job.SlotKey, out var s) && s.State is JobState.Queued or JobState.Working) continue;
            queue.Enqueue(job);
            states[job.SlotKey] = (JobState.Queued, null);
            TotalThisRun++;
        }

        if (Running && !holdingRecorder)
        {
            plugin.Packets.Acquire(); // so the game's network client is known before the first send
            holdingRecorder = true;
            waitingForClientSinceUtc = DateTime.UtcNow;
        }
    }

    public void Stop(string reason)
    {
        foreach (var job in queue) states.Remove(job.SlotKey);
        queue.Clear();
        if (current != null)
        {
            states[current.Value.SlotKey] = (JobState.Failed, "Stopped while this item was being placed. Check it in game.");
            current = null;
        }
        StopReason = reason;
        ReleaseRecorder();
    }

    private void ReleaseRecorder()
    {
        if (!holdingRecorder) return;
        plugin.Packets.Release();
        holdingRecorder = false;
    }

    public void Update()
    {
        if (current != null)
        {
            CheckCurrent();
            return;
        }

        if (queue.Count == 0)
        {
            ReleaseRecorder();
            return;
        }
        if (DateTime.UtcNow < nextAllowedUtc) return;

        if (!plugin.Packets.HasClient)
        {
            if (DateTime.UtcNow - waitingForClientSinceUtc > ClientWait)
                Stop("Couldn't find the game's network connection.");
            return;
        }

        var job = queue.Dequeue();
        var problem = Preflight(job, out var profile, out var target, out var position, out var rotation, out var skipOnly);
        if (problem != null)
        {
            states[job.SlotKey] = (JobState.Failed, problem);
            if (!skipOnly) Stop(problem);
            return;
        }

        placedCountBefore = CountPlaced(job.ItemId);
        var data = profile!.Build(target.Container, target.Slot, position, rotation);

        bool sent;
        try
        {
            sent = plugin.Packets.Send(data, profile.A3, profile.A4, profile.A5);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Sending place request failed");
            sent = false;
        }
        if (!sent)
        {
            const string msg = "Couldn't send the request. Stopped.";
            states[job.SlotKey] = (JobState.Failed, msg);
            Stop(msg);
            return;
        }

        current = job;
        currentContainer = target.Container;
        currentSlot = target.Slot;
        sentUtc = DateTime.UtcNow;
        states[job.SlotKey] = (JobState.Working, null);
    }

    private string? Preflight(PlaceJob job, out PlaceProfile? profile, out (int Container, int Slot) target,
        out Vector3 position, out float rotation, out bool skipOnly)
    {
        profile = null;
        target = (job.Container, job.Slot);
        position = default;
        rotation = 0;
        skipOnly = false;

        if (!Plugin.PlayerState.IsLoaded) return "You're not logged in.";
        if (plugin.Scanner.CurrentHouseId != job.HouseId) return "You're not inside the room this plan is for.";
        if (plugin.Learner.Active || plugin.PlaceLearner.Active) return "Learning is active. Finish or cancel it first.";
        if (plugin.Executor.Running) return "Storing is running. Wait for it to finish.";

        var source = SourceOf(job.Container);
        profile = ProfileFor(job.HouseId, source);
        if (profile == null)
            return source == PlaceSource.Storeroom
                ? "Placing from the storeroom isn't set up for this room yet. Use Learn first."
                : "Placing from your inventory isn't set up for this room yet. Use Learn first.";

        var cache = plugin.Cache.TryGet(Plugin.PlayerState.ContentId);
        var resolved = LiveStoreResolver.ResolvePlace(plugin.MainWindow.CurrentLayout, cache, job);
        if (resolved == null)
        {
            skipOnly = true;
            return "The layout no longer needs this item (or it was already placed), so it was skipped.";
        }

        target = (resolved.Value.Container, resolved.Value.Slot);
        if (!LiveInventory.TryGetSlot(target.Container, target.Slot, out var itemId, out var stain)
            || itemId != job.ItemId || stain != job.Stain)
            return "The item moved while checking. Refresh and try again.";

        if (LiveInventory.FreeSlots(LiveInventory.InteriorPlaced) == 0)
            return "This room has no free furnishing slots.";

        if (plugin.Configuration.PlaceAtLayoutPosition && resolved.Value.Need.HasPosition)
        {
            position = resolved.Value.Need.Position;
            rotation = resolved.Value.Need.Rotation;
        }
        else
        {
            var me = Plugin.ObjectTable.LocalPlayer;
            if (me == null) return "Couldn't find your character's position.";
            position = me.Position;
            rotation = 0;
        }

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || Math.Abs(position.X) > 500 || Math.Abs(position.Y) > 500 || Math.Abs(position.Z) > 500)
            return "The position for this item looks wrong, so it wasn't sent.";

        return null;
    }

    private void CheckCurrent()
    {
        var job = current!.Value;

        if (plugin.Scanner.CurrentHouseId != job.HouseId)
        {
            states[job.SlotKey] = (JobState.Failed, "You left the room while this item was being placed. Check it in game.");
            current = null;
            Stop("You left the room.");
            return;
        }

        var stillThere = LiveInventory.TryGetSlot(currentContainer, currentSlot, out var itemId, out var stain)
                         && itemId == job.ItemId && stain == job.Stain;
        var placed = CountPlaced(job.ItemId) > placedCountBefore;

        if (!stillThere && placed)
        {
            states[job.SlotKey] = (JobState.Done, "Placed");
            current = null;
            DoneThisRun++;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0.5, plugin.Configuration.StoreDelaySeconds));
            return;
        }

        if (DateTime.UtcNow - sentUtc > ConfirmTimeout)
        {
            var msg = stillThere
                ? "The game didn't place the item (it may not fit there). Stopped so nothing else is attempted."
                : "The item left its slot but didn't appear in the room. Stopped; check your storeroom and inventory.";
            states[job.SlotKey] = (JobState.Failed, msg);
            current = null;
            Stop(msg);
        }
    }

    private static int CountPlaced(uint itemId) => LiveInventory.CountItem(LiveInventory.InteriorPlaced, itemId);
}
