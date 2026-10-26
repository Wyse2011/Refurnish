using System;
using System.Collections.Generic;
using System.Linq;
using Refurnish.Inventory;
using Refurnish.Planning;

namespace Refurnish.Automation;

/// <param name="Everything">
/// Full rebuild: store this item even though the layout uses it (it'll be placed back at its exact
/// spot afterwards). The slot is still checked live before every item.
/// </param>
public readonly record struct StoreJob(ulong HouseId, int Container, int Slot, uint ItemId, byte Stain, bool Everything = false)
{
    public (int, int) SlotKey => (Container, Slot);
}

public enum JobState { Queued, Working, Done, Failed }

/// <summary>
/// Stores placed furniture one item at a time. Before every item it re-checks, live:
/// you're still in the right house, the slot still holds exactly the item that was planned, a
/// learned request exists for this house and game version, and the destination has room.
/// After sending, it waits until the game confirms the item actually moved. Any failure or
/// timeout stops the whole run; nothing is retried automatically.
/// </summary>
public sealed class StoreExecutor
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);

    private readonly Plugin plugin;
    private readonly Queue<StoreJob> queue = new();
    private readonly Dictionary<(int, int), (JobState State, string? Note)> states = new();

    private StoreJob? current;          // the job as queued (its slot is the row's key in the UI)
    private int currentContainer;       // where the item actually was when sent
    private int currentSlot;
    private StoreDestination currentDestination;
    private int destinationCountBefore;
    private DateTime sentUtc;
    private DateTime nextAllowedUtc = DateTime.MinValue;

    public bool Running => current != null || queue.Count > 0;
    public int Remaining => queue.Count + (current != null ? 1 : 0);
    public int DoneThisRun { get; private set; }
    public int TotalThisRun { get; private set; }
    public string? StopReason { get; private set; }

    public StoreExecutor(Plugin plugin) => this.plugin = plugin;

    public (JobState State, string? Note)? StateOf(int container, int slot) =>
        states.TryGetValue((container, slot), out var s) ? s : null;

    public void Enqueue(IEnumerable<StoreJob> jobs)
    {
        if (!Running)
        {
            ClearFinished();
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
    }

    public void Stop(string reason)
    {
        foreach (var job in queue) states.Remove(job.SlotKey);
        queue.Clear();
        if (current != null)
        {
            // A request already sent can't be recalled; leave it to finish or time out on the next check.
            states[current.Value.SlotKey] = (JobState.Failed, "Stopped while this item was being stored. Check it in game.");
            current = null;
        }
        StopReason = reason;
    }

    public void ClearFinished()
    {
        foreach (var key in states.Where(kv => kv.Value.State is JobState.Done or JobState.Failed).Select(kv => kv.Key).ToList())
            states.Remove(key);
    }

    /// <summary>Which profiles are usable right now, in the order they'll be tried.</summary>
    public StoreProfile? ProfileFor(ulong houseId, StoreDestination destination) =>
        plugin.Configuration.StoreProfiles.FirstOrDefault(p => p.Destination == destination && p.IsValidFor(houseId, Plugin.GameVersion));

    /// <summary>Called every framework tick.</summary>
    public void Update()
    {
        if (current != null)
        {
            CheckCurrent();
            return;
        }

        if (queue.Count == 0 || DateTime.UtcNow < nextAllowedUtc) return;

        var job = queue.Dequeue();
        var problem = Preflight(job, out var profile, out var destination, out var target, out var skipOnly);
        if (problem != null)
        {
            states[job.SlotKey] = (JobState.Failed, problem);
            if (!skipOnly) Stop(problem);
            return;
        }

        destinationCountBefore = CountAtDestination(destination, job.ItemId);
        var parameters = profile!.Build(target.Container, target.Slot);

        try
        {
            // The return value isn't relied on; whether the item actually moved is checked next.
            CommandRecorder.Send(profile.CommandId, parameters);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Sending store request failed");
            const string msg = "Sending the request failed. See /xllog.";
            states[job.SlotKey] = (JobState.Failed, msg);
            Stop(msg);
            return;
        }

        current = job;
        currentContainer = target.Container;
        currentSlot = target.Slot;
        currentDestination = destination;
        sentUtc = DateTime.UtcNow;
        states[job.SlotKey] = (JobState.Working, null);
    }

    private string? Preflight(StoreJob job, out StoreProfile? profile, out StoreDestination destination, out (int Container, int Slot) target, out bool skipOnly)
    {
        skipOnly = false;
        profile = null;
        destination = StoreDestination.Storeroom;
        target = (job.Container, job.Slot);

        if (!Plugin.PlayerState.IsLoaded) return "You're not logged in.";
        if (plugin.Scanner.CurrentHouseId != job.HouseId) return "You're not inside the room this plan is for.";
        if (plugin.Learner.Active || plugin.PlaceLearner.Active) return "Learning is active. Finish or cancel it first.";
        if (plugin.Placer.Running || plugin.UiPlacer.Running) return "Placing is running. Wait for it to finish.";
        if (plugin.UiLearner.Active) return "Learning is active. Finish or cancel it first.";
        if (!LiveInventory.IsInteriorPlaced(job.Container)) return "That isn't a placed-furniture slot.";

        if (job.Everything)
        {
            // Full rebuild: find this item (same dye) wherever it sits now; slots can shift as items leave.
            if (LiveInventory.TryGetSlot(job.Container, job.Slot, out var id0, out var st0) && id0 == job.ItemId && st0 == job.Stain)
            {
                target = (job.Container, job.Slot);
            }
            else
            {
                var elsewhere = LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out _)
                    .Where(kv => kv.Value.ItemId == job.ItemId && kv.Value.Stain == job.Stain)
                    .Select(kv => kv.Key).ToList();
                if (elsewhere.Count == 0)
                {
                    skipOnly = true;
                    return "Already stored.";
                }
                target = elsewhere[0];
            }
        }
        else
        {
            // Re-plan from a live read: is a copy of this item (same dye) still unneeded, and where is it now?
            var cache = plugin.Cache.TryGet(Plugin.PlayerState.ContentId);
            var resolved = LiveStoreResolver.Resolve(plugin.MainWindow.CurrentLayout, cache, job);
            if (resolved == null)
            {
                skipOnly = true; // harmless: nothing to do for this one, so carry on with the rest
                return "This item is no longer an extra (the layout uses every copy, or it was already moved), so it was skipped.";
            }
            target = resolved.Value;
        }

        // Final check on the exact slot about to be sent.
        if (!LiveInventory.TryGetSlot(target.Container, target.Slot, out var itemId, out var stain)
            || itemId != job.ItemId || stain != job.Stain)
            return "The item moved while checking. Refresh and try again.";

        var room = ProfileFor(job.HouseId, StoreDestination.Storeroom);
        if (room != null)
        {
            var free = LiveInventory.FreeSlots(LiveInventory.InteriorStoreroom);
            if (free < 0) return "The storeroom isn't loaded, so its space can't be checked. Open the storeroom tab in the furnishings window.";
            if (free > 0) { profile = room; destination = StoreDestination.Storeroom; return null; }
        }

        var bag = ProfileFor(job.HouseId, StoreDestination.Inventory);
        if (bag != null)
        {
            var free = LiveInventory.FreeSlots(LiveInventory.PlayerInventory);
            if (free > 0) { profile = bag; destination = StoreDestination.Inventory; return null; }
            return room != null ? "The storeroom and your inventory are both full." : "Your inventory is full.";
        }

        if (room != null) return "The storeroom is full. (Teach Refurnish \"Remove to inventory\" to overflow into your bags.)";
        return "Storing isn't set up for this room yet. Use \"Learn\" first.";
    }

    private void CheckCurrent()
    {
        var job = current!.Value;

        if (plugin.Scanner.CurrentHouseId != job.HouseId)
        {
            states[job.SlotKey] = (JobState.Failed, "You left the house while this item was being stored. Check it in game.");
            current = null;
            Stop("You left the house.");
            return;
        }

        var stillThere = LiveInventory.TryGetSlot(currentContainer, currentSlot, out var itemId, out var stain)
                         && itemId == job.ItemId && stain == job.Stain;
        var arrived = CountAtDestination(currentDestination, job.ItemId) > destinationCountBefore;

        if (!stillThere && arrived)
        {
            states[job.SlotKey] = (JobState.Done, currentDestination == StoreDestination.Storeroom ? "Stored" : "In inventory");
            current = null;
            DoneThisRun++;
            // Each item already waited for the game to confirm it moved, so this pause is extra.
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(Configuration.MinStoreDelaySeconds, plugin.Configuration.StoreDelaySeconds));
            if (queue.Count == 0) StopReason = null;
            return;
        }

        if (DateTime.UtcNow - sentUtc > ConfirmTimeout)
        {
            var msg = stillThere
                ? "The game didn't move the item. Stopped so nothing else is attempted."
                : "The item left its slot but didn't show up where expected. Stopped; check your storeroom and inventory.";
            states[job.SlotKey] = (JobState.Failed, msg);
            current = null;
            Stop(msg);
        }
    }

    private static int CountAtDestination(StoreDestination d, uint itemId) => d == StoreDestination.Storeroom
        ? LiveInventory.CountItem(LiveInventory.InteriorStoreroom, itemId)
        : LiveInventory.CountItem(LiveInventory.PlayerInventory, itemId);
}
