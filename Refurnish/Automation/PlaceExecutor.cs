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
/// Places furniture from the storeroom or your inventory, one item at a time.
///
/// Retry rules: if an item can't be placed, it's set aside and the run moves straight on to the
/// next one. After the whole list, it goes back to the top and tries the set-aside items again.
/// Problems with the situation rather than the item (you left the room, the room is full, the
/// network connection can't be found) stop everything at once, and so do ten failures in a row.
///
/// After every placement it reads where the game actually put the item. If that isn't where it
/// was sent, the learned message is wrong, and everything stops immediately.
///
/// Before every try it re-plans from a live read (is it still needed, and where does it go?) and
/// checks the slot still holds that exact item. After sending, it waits for the game to confirm
/// the item was really placed. If an earlier try succeeded late, the re-plan notices and skips.
/// </summary>
public sealed class PlaceExecutor
{
    public const int TriesPerPass = 1;
    public const int Passes = 2;
    private const int MaxFailedTriesInARow = 10;
    private const float PositionTolerance = 0.05f;
    private const float RotationTolerance = 0.05f;
    private const double MinPauseSeconds = 1.0;

    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ClientWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(2);

    private enum Outcome { Ok, Skip, Retry, GiveUpOnItem, StopAll }

    private readonly Plugin plugin;
    private readonly List<PlaceJob> queue = new();      // front = next
    private readonly List<PlaceJob> setAside = new();   // missed this pass, retried next pass
    private readonly Dictionary<(int, int), int> triesThisPass = new();
    private readonly Dictionary<(int, int), (JobState State, string? Note)> states = new();

    private PlaceJob? current;
    private int currentContainer;
    private int currentSlot;
    private int placedCountBefore;
    private Dictionary<int, LiveFurniture.Entry> furnitureBefore = new();
    private Vector3 sentPosition;
    private float sentRotation;
    private DateTime sentUtc;
    private DateTime nextAllowedUtc = DateTime.MinValue;
    private DateTime waitingForClientSinceUtc = DateTime.MinValue;
    private bool holdingRecorder;
    private int failedTriesInARow;

    public bool Running => current != null || queue.Count > 0;
    public int Pass { get; private set; } = 1;
    public int DoneThisRun { get; private set; }
    public int TotalThisRun { get; private set; }
    public int SetAsideCount => setAside.Count;
    public int FinalFailures { get; private set; }
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
            states.Clear();
            setAside.Clear();
            triesThisPass.Clear();
            Pass = 1;
            DoneThisRun = 0;
            TotalThisRun = 0;
            FinalFailures = 0;
            failedTriesInARow = 0;
            StopReason = null;
        }

        foreach (var job in jobs)
        {
            if (queue.Any(q => q.SlotKey == job.SlotKey) || current?.SlotKey == job.SlotKey) continue;
            queue.Add(job);
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
        foreach (var job in queue.Concat(setAside))
            if (states.TryGetValue(job.SlotKey, out var s) && s.State != JobState.Failed)
                states.Remove(job.SlotKey);
        queue.Clear();
        setAside.Clear();
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
            if (!StartNextPassIfNeeded())
            {
                ReleaseRecorder();
                return;
            }
        }
        if (DateTime.UtcNow < nextAllowedUtc) return;

        if (!plugin.Packets.HasClient)
        {
            if (DateTime.UtcNow - waitingForClientSinceUtc > ClientWait)
                Stop("Couldn't find the game's network connection. Take a step or two with your character, then try again.");
            return;
        }

        var job = queue[0];
        queue.RemoveAt(0);

        var outcome = Preflight(job, out var message, out var profile, out var target, out var position, out var rotation);
        if (outcome != Outcome.Ok)
        {
            Handle(job, outcome, message!);
            return;
        }

        var furniture = LiveFurniture.Read();
        if (furniture == null)
        {
            Handle(job, Outcome.StopAll, "Couldn't read this room's furniture, so placement couldn't be checked.");
            return;
        }
        furnitureBefore = furniture;
        sentPosition = position;
        sentRotation = rotation;
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
            Handle(job, Outcome.StopAll, "Couldn't send the request through the game's connection.");
            return;
        }

        current = job;
        currentContainer = target.Container;
        currentSlot = target.Slot;
        sentUtc = DateTime.UtcNow;
        var tryNo = triesThisPass.GetValueOrDefault(job.SlotKey) + 1;
        states[job.SlotKey] = (JobState.Working, tryNo > 1 ? $"Placing (try {tryNo})..." : null);
    }

    /// <summary>When a pass ends with items set aside, go back to the top with just those.</summary>
    private bool StartNextPassIfNeeded()
    {
        if (setAside.Count == 0) return false;

        if (Pass >= Passes)
        {
            foreach (var job in setAside)
            {
                states[job.SlotKey] = (JobState.Failed, states.TryGetValue(job.SlotKey, out var s) && s.Note != null
                    ? $"Couldn't place after {Passes} passes. Last problem: {s.Note}"
                    : $"Couldn't place after {Passes} passes.");
                FinalFailures++;
            }
            setAside.Clear();
            return false;
        }

        Pass++;
        queue.AddRange(setAside);
        foreach (var job in setAside)
            states[job.SlotKey] = (JobState.Queued, states.TryGetValue(job.SlotKey, out var s) ? s.Note : null);
        setAside.Clear();
        triesThisPass.Clear();
        nextAllowedUtc = DateTime.UtcNow + PauseAfterFailure;
        return true;
    }

    private void Handle(PlaceJob job, Outcome outcome, string message)
    {
        switch (outcome)
        {
            case Outcome.Skip:
                states[job.SlotKey] = (JobState.Done, message);
                return;

            case Outcome.StopAll:
                states[job.SlotKey] = (JobState.Failed, message);
                Stop(message);
                return;

            case Outcome.GiveUpOnItem:
                // Retrying wouldn't change anything for this item; carry on with the rest.
                states[job.SlotKey] = (JobState.Failed, message);
                FinalFailures++;
                CountFailedTry();
                return;

            case Outcome.Retry:
                var tries = triesThisPass.GetValueOrDefault(job.SlotKey) + 1;
                triesThisPass[job.SlotKey] = tries;
                nextAllowedUtc = DateTime.UtcNow + PauseAfterFailure;
                if (!CountFailedTry()) return;

                if (tries < TriesPerPass)
                {
                    queue.Insert(0, job); // another try straight away (only if TriesPerPass > 1)
                    states[job.SlotKey] = (JobState.Queued, $"Retrying: {message}");
                }
                else
                {
                    setAside.Add(job);
                    states[job.SlotKey] = Pass < Passes
                        ? (JobState.Queued, $"Missed, will retry after the others: {message}")
                        : (JobState.Queued, message);
                }
                return;
        }
    }

    /// <summary>Counts a failed try; stops everything if too many fail in a row. Returns false if stopped.</summary>
    private bool CountFailedTry()
    {
        failedTriesInARow++;
        if (failedTriesInARow < MaxFailedTriesInARow) return true;
        Stop($"{MaxFailedTriesInARow} items in a row couldn't be placed, so something is wrong beyond a single item. Stopped.");
        return false;
    }

    private Outcome Preflight(PlaceJob job, out string? message, out PlaceProfile? profile, out (int Container, int Slot) target,
        out Vector3 position, out float rotation)
    {
        message = null;
        profile = null;
        target = (job.Container, job.Slot);
        position = default;
        rotation = 0;

        // Situation problems: no point continuing with any item.
        if (!Plugin.PlayerState.IsLoaded) { message = "You're not logged in."; return Outcome.StopAll; }
        if (plugin.Scanner.CurrentHouseId != job.HouseId) { message = "You're not inside the room this plan is for."; return Outcome.StopAll; }
        if (plugin.Learner.Active || plugin.PlaceLearner.Active) { message = "Learning is active. Finish or cancel it first."; return Outcome.StopAll; }
        if (plugin.Executor.Running) { message = "Storing is running. Wait for it to finish."; return Outcome.StopAll; }
        if (LiveInventory.FreeSlots(LiveInventory.InteriorPlaced) == 0) { message = "This room has no free furnishing slots."; return Outcome.StopAll; }

        var source = SourceOf(job.Container);
        profile = ProfileFor(job.HouseId, source);
        if (profile == null)
        {
            message = source == PlaceSource.Storeroom
                ? "Placing from the storeroom isn't set up for this room. Use Learn first."
                : "Placing from your inventory isn't set up for this room. Use Learn first.";
            return Outcome.GiveUpOnItem; // other items may come from the source that is set up
        }

        var cache = plugin.Cache.TryGet(Plugin.PlayerState.ContentId);
        var resolved = LiveStoreResolver.ResolvePlace(plugin.MainWindow.CurrentLayout, cache, job);
        if (resolved == null)
        {
            message = "Already placed, or no longer needed.";
            return Outcome.Skip;
        }

        target = (resolved.Value.Container, resolved.Value.Slot);
        if (!LiveInventory.TryGetSlot(target.Container, target.Slot, out var itemId, out var stain)
            || itemId != job.ItemId || stain != job.Stain)
        {
            message = "The item moved while checking.";
            return Outcome.Retry;
        }

        if (plugin.Configuration.PlaceAtLayoutPosition && resolved.Value.Need.HasPosition)
        {
            position = resolved.Value.Need.Position;
            rotation = resolved.Value.Need.Rotation;
        }
        else
        {
            var me = Plugin.ObjectTable.LocalPlayer;
            if (me == null) { message = "Couldn't find your character's position."; return Outcome.Retry; }
            position = me.Position;
            rotation = 0;
        }

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || Math.Abs(position.X) > 500 || Math.Abs(position.Y) > 500 || Math.Abs(position.Z) > 500)
        {
            message = "The position for this item looks wrong, so it wasn't sent.";
            return Outcome.GiveUpOnItem;
        }

        return Outcome.Ok;
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
            // Confirm it landed where it was sent. Wait for the furniture list to catch up.
            var now = LiveFurniture.Read();
            var added = now == null ? new List<LiveFurniture.Entry>() : LiveFurniture.New(furnitureBefore, now);
            if (added.Count == 0)
            {
                if (DateTime.UtcNow - sentUtc <= ConfirmTimeout) return;
                current = null;
                Handle(job, Outcome.StopAll, "The item was placed but its position couldn't be confirmed. Stopped to be safe.");
                return;
            }

            var hit = added.Any(a => Vector3.Distance(a.Position, sentPosition) < PositionTolerance
                                     && LiveFurniture.AngleDiff(a.Rotation, sentRotation) < RotationTolerance);
            current = null;
            if (!hit)
            {
                var a = added[0];
                Handle(job, Outcome.StopAll,
                    $"The item landed at ({a.Position.X:0.00}, {a.Position.Y:0.00}, {a.Position.Z:0.00}) instead of " +
                    $"({sentPosition.X:0.00}, {sentPosition.Y:0.00}, {sentPosition.Z:0.00}). The learned message isn't right, " +
                    "so everything stopped. Please send this message over.");
                return;
            }

            states[job.SlotKey] = (JobState.Done, "Placed");
            DoneThisRun++;
            failedTriesInARow = 0;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(MinPauseSeconds, plugin.Configuration.StoreDelaySeconds));
            return;
        }

        if (DateTime.UtcNow - sentUtc <= ConfirmTimeout) return;

        current = null;
        if (stillThere)
        {
            // The game simply didn't take it (often a spot it won't allow). Safe to try again.
            Handle(job, Outcome.Retry, "The game didn't place it.");
        }
        else
        {
            // It left its slot but never showed up: don't resend for something in an unknown state.
            Handle(job, Outcome.GiveUpOnItem, "It left its slot but didn't appear in the room. Check your storeroom and inventory.");
        }
    }

    private static int CountPlaced(uint itemId) => LiveInventory.CountItem(LiveInventory.InteriorPlaced, itemId);
}
