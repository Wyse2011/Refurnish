using System;
using System.Collections.Generic;
using System.Linq;
using Refurnish.Inventory;

namespace Refurnish.Automation;

/// <summary>
/// One-time setup per house (and again after a game patch). While active, you store or remove one
/// placed item using the game's own menu. Refurnish watches which slot emptied, where the item
/// went, and which command the game sent, and saves that as a <see cref="StoreProfile"/>.
/// If it can't match the command to the item with certainty, it refuses to learn anything.
/// </summary>
public sealed class StoreLearner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly Plugin plugin;
    private Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> baseline = new();
    private DateTime startedUtc;
    private ulong houseId;
    private (int Container, int Slot, uint ItemId)? pending;
    private Dictionary<uint, int> roomBefore = new();
    private Dictionary<uint, int> bagBefore = new();
    private bool storeroomLoaded;
    private DateTime pendingSinceUtc;

    public bool Active { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public bool LastSucceeded { get; private set; }
    public List<CapturedCommand> LastCapture { get; private set; } = new();

    public StoreLearner(Plugin plugin) => this.plugin = plugin;

    public bool Start()
    {
        if (plugin.Recorder.SetupError != null) { Fail(plugin.Recorder.SetupError); return false; }
        if (plugin.Executor.Running) { Fail("Wait for storing to finish first."); return false; }

        houseId = plugin.Scanner.CurrentHouseId;
        if (houseId == 0) { Fail("Go inside the house you want to set up (you need decorating permission)."); return false; }

        baseline = LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out var loaded);
        if (!loaded || baseline.Count == 0) { Fail("Couldn't read this house's placed furniture yet. Try again in a moment."); return false; }

        // Remember how many of each placed item are in the storeroom and bags right now, so
        // afterwards it's clear where the stored item went.
        storeroomLoaded = LiveInventory.FreeSlots(LiveInventory.InteriorStoreroom) >= 0;
        roomBefore = baseline.Values.Select(v => v.ItemId).Distinct()
            .ToDictionary(id => id, id => LiveInventory.CountItem(LiveInventory.InteriorStoreroom, id));
        bagBefore = baseline.Values.Select(v => v.ItemId).Distinct()
            .ToDictionary(id => id, id => LiveInventory.CountItem(LiveInventory.PlayerInventory, id));

        startedUtc = DateTime.UtcNow;
        pending = null;
        plugin.Recorder.Start();
        Active = true;
        LastSucceeded = false;
        Status = storeroomLoaded
            ? "Watching. Now store one furnishing using the game's own menu."
            : "Watching. Open the storeroom tab in the furnishings window first, then store one furnishing using the game's own menu.";
        return true;
    }

    public void Cancel()
    {
        if (!Active) return;
        plugin.Recorder.Stop();
        Active = false;
        Status = "Cancelled.";
    }

    /// <summary>Called every framework tick.</summary>
    public void Update()
    {
        if (!Active) return;

        if (plugin.Scanner.CurrentHouseId != houseId) { Fail("You left the house, so learning stopped."); return; }
        if (DateTime.UtcNow - startedUtc > Timeout) { Fail("Nothing was stored within 3 minutes, so learning stopped."); return; }

        if (pending == null)
        {
            var now = LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out var loaded);
            if (!loaded) return;

            var gone = baseline.Where(kv => !now.TryGetValue(kv.Key, out var v) || v.ItemId != kv.Value.ItemId).ToList();
            if (gone.Count == 0)
            {
                baseline = now; // pick up anything newly placed so it can be used too
                foreach (var id in now.Values.Select(v => v.ItemId))
                {
                    if (!roomBefore.ContainsKey(id)) roomBefore[id] = LiveInventory.CountItem(LiveInventory.InteriorStoreroom, id);
                    if (!bagBefore.ContainsKey(id)) bagBefore[id] = LiveInventory.CountItem(LiveInventory.PlayerInventory, id);
                }
                return;
            }
            if (gone.Count > 1) { Fail("More than one item moved at once. Store just one item and try again."); return; }

            var (key, value) = (gone[0].Key, gone[0].Value);
            pending = (key.Container, key.Slot, value.ItemId);
            pendingSinceUtc = DateTime.UtcNow;
        }

        // Give the game a moment to send the command and update the destination.
        if (DateTime.UtcNow - pendingSinceUtc < TimeSpan.FromSeconds(1.5)) return;

        Finish(pending.Value.Container, pending.Value.Slot, pending.Value.ItemId);
    }

    private void Finish(int container, int slot, uint itemId)
    {
        plugin.Recorder.Stop();
        Active = false;
        LastCapture = plugin.Recorder.Snapshot();

        var matches = new List<(CapturedCommand Call, int ContainerParam, int SlotParam)>();
        foreach (var call in LastCapture)
        {
            for (var c = 0; c < 4; c++)
            {
                if (call.Param(c) != container) continue;
                for (var s = 0; s < 4; s++)
                {
                    if (s == c) continue;
                    if ((call.Param(s) & 0xFFFF) == slot)
                        matches.Add((call, c, s));
                }
            }
        }

        var distinct = matches.Select(m => (m.Call.Command, m.Call.P1, m.Call.P2, m.Call.P3, m.Call.P4, m.ContainerParam, m.SlotParam)).Distinct().ToList();
        if (distinct.Count == 0)
        {
            Fail("The item was stored, but no matching game command was seen. Automatic storing can't be enabled safely. "
                 + "Copy the capture log below and send it over so the cause can be found.");
            return;
        }
        if (distinct.Count > 1)
        {
            Fail("The match was ambiguous, so Refurnish won't guess. This can happen with the first item in a list. "
                 + "Press Learn again and store a different item. If it keeps happening, copy the capture log below and send it over.");
            return;
        }

        var destination = DetectDestination(itemId);
        if (destination == null)
        {
            Fail("Couldn't tell whether the item went to the storeroom or your inventory. Open the storeroom tab and try again.");
            return;
        }

        var m0 = matches[0];
        var profile = new StoreProfile
        {
            Destination = destination.Value,
            HouseId = houseId,
            GameVersion = Plugin.GameVersion,
            LearnedUtc = DateTime.UtcNow,
            CommandId = m0.Call.Command,
            Observed = new[] { m0.Call.P1, m0.Call.P2, m0.Call.P3, m0.Call.P4 },
            ContainerParam = m0.ContainerParam,
            SlotParam = m0.SlotParam,
            SlotExtraBits = m0.Call.Param(m0.SlotParam) & unchecked((int)0xFFFF0000),
        };

        plugin.Configuration.StoreProfiles.RemoveAll(p => p.HouseId == houseId && p.Destination == profile.Destination);
        plugin.Configuration.StoreProfiles.Add(profile);
        plugin.Configuration.Save();

        LastSucceeded = true;
        Status = destination == StoreDestination.Storeroom
            ? "Learned. Refurnish can now store items to this house's storeroom."
            : "Learned. Refurnish can now move items from this house to your inventory.";
    }

    private StoreDestination? DetectDestination(uint itemId)
    {
        var bagNow = LiveInventory.CountItem(LiveInventory.PlayerInventory, itemId);
        if (bagNow > bagBefore.GetValueOrDefault(itemId)) return StoreDestination.Inventory;

        if (!storeroomLoaded) return null;
        var roomNow = LiveInventory.CountItem(LiveInventory.InteriorStoreroom, itemId);
        if (roomNow > roomBefore.GetValueOrDefault(itemId)) return StoreDestination.Storeroom;

        return null;
    }

    private void Fail(string message)
    {
        plugin.Recorder.Stop();
        if (Active) LastCapture = plugin.Recorder.Snapshot();
        Active = false;
        LastSucceeded = false;
        Status = message;
    }
}
