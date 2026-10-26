using System;
using System.Collections.Generic;
using System.Linq;
using Refurnish.Inventory;

namespace Refurnish.Automation;

/// <summary>
/// Learns the furnishing window's clicks. You do two things by hand while it watches:
///  1. In the storeroom tab, pick an item and choose "Place".
///  2. Pick another item and press Escape to close the menu without choosing.
/// It only saves when the "Place" click is followed by exactly the selected storeroom slot
/// emptying and a new item appearing in the room, and every value involved is a plain number.
/// </summary>
public sealed class UiPlaceLearner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly Plugin plugin;
    private readonly List<CbEvent> events = new();
    private readonly object gate = new();
    private Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> roomBase = new();
    private Dictionary<uint, int> placedCounts = new();
    private DateTime startedUtc;
    private ulong houseId;

    private (CbEvent Select, CbEvent Place)? placeFound;
    private CbEvent? cancelFound;

    public bool Active { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public bool LastSucceeded { get; private set; }
    public bool HasPlace => placeFound != null;
    public bool HasCancel => cancelFound != null;

    public UiPlaceLearner(Plugin plugin) => this.plugin = plugin;

    public bool Start()
    {
        if (plugin.Tap.SetupError != null) { Fail(plugin.Tap.SetupError); return false; }
        if (plugin.Executor.Running || plugin.UiPlacer.Running || plugin.Learner.Active)
        {
            Fail("Wait for other Refurnish actions to finish first.");
            return false;
        }
        houseId = plugin.Scanner.CurrentHouseId;
        if (houseId == 0) { Fail("Go inside the room first (you need decorating permission)."); return false; }

        roomBase = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out var loaded);
        if (!loaded) { Fail("Open the storeroom tab in the furnishing window first."); return false; }
        placedCounts = CountPlaced();

        lock (gate) events.Clear();
        placeFound = null;
        cancelFound = null;
        startedUtc = DateTime.UtcNow;
        plugin.Tap.Seen += OnCallback;
        plugin.Tap.Acquire();
        Active = true;
        LastSucceeded = false;
        UpdateStatus();
        return true;
    }

    public void Cancel()
    {
        if (!Active) return;
        Unhook();
        Active = false;
        Status = "Cancelled.";
    }

    private void Unhook()
    {
        plugin.Tap.Seen -= OnCallback;
        plugin.Tap.Release();
    }

    private void OnCallback(CbEvent e)
    {
        lock (gate)
        {
            events.Add(e);
            if (events.Count > 300) events.RemoveAt(0);
        }
    }

    private void UpdateStatus()
    {
        Status = (HasPlace, HasCancel) switch
        {
            (false, false) => "Step 1: in the storeroom tab, click an item and choose Place (anywhere).",
            (true, false) => "Step 2: click another item in the storeroom list, then press Escape to close the menu.",
            (false, true) => "Now click an item in the storeroom list and choose Place.",
            _ => "Saving...",
        };
    }

    public void Update()
    {
        if (!Active) return;
        if (plugin.Scanner.CurrentHouseId != houseId) { Fail("You left the room, so learning stopped."); return; }
        if (DateTime.UtcNow - startedUtc > Timeout) { Fail("Learning timed out after 5 minutes."); return; }

        List<CbEvent> snapshot;
        lock (gate) snapshot = new List<CbEvent>(events);

        // Cancel: the menu closed without a choice, right after an item was picked in the list.
        if (cancelFound == null)
        {
            for (var i = 0; i < snapshot.Count; i++)
            {
                var e = snapshot[i];
                if (e.Addon != "ContextMenu" || !IsChoice(e) || e.Values[1].AsInt != -1) continue;
                if (snapshot.Take(i).Any(IsListSelect)) { cancelFound = e; break; }
            }
        }

        // Place: a storeroom slot emptied and a new item appeared in the room.
        if (placeFound == null)
        {
            var room = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out var loaded);
            var placed = CountPlaced();

            // Ignore the blink while the furnishing window reloads its lists.
            if (loaded && room.Count > 0 && (placed.Count > 0 || placedCounts.Count == 0))
            {
                var lost = roomBase.Where(kv => !room.TryGetValue(kv.Key, out var v) || v.ItemId != kv.Value.ItemId)
                    .Where(kv => placed.GetValueOrDefault(kv.Value.ItemId) > placedCounts.GetValueOrDefault(kv.Value.ItemId))
                    .ToList();

                if (lost.Count > 1) { Fail("More than one item moved at once. Place just one and try again."); return; }
                if (lost.Count == 1)
                {
                    var slot = lost[0].Key;
                    var result = MatchPlace(snapshot, slot);
                    if (result.Error != null) { Fail(result.Error); return; }
                    placeFound = result.Found;
                }
                roomBase = room;
                placedCounts = placed;
            }
        }

        if (placeFound != null && cancelFound != null)
        {
            Save();
            return;
        }
        UpdateStatus();
    }

    private (string? Error, (CbEvent, CbEvent)? Found) MatchPlace(List<CbEvent> snapshot, (int Container, int Slot) slot)
    {
        // The last "choose an option" click in the menu before the item moved...
        var placeIdx = snapshot.FindLastIndex(e => e.Addon == "ContextMenu" && IsChoice(e) && e.Values[1].AsInt >= 0
                                                   && DateTime.UtcNow - e.Utc < TimeSpan.FromSeconds(5));
        if (placeIdx < 0)
            return ("The item was placed, but not through the storeroom list's menu. Use the furnishing window's storeroom tab and try again.", null);
        var place = snapshot[placeIdx];

        // ...must have been for the storeroom slot that emptied...
        if (place.SelContainer != slot.Container || place.SelSlot != slot.Slot)
            return ("The menu click didn't match the item that was placed, so nothing was learned. Try again.", null);

        // ...and must follow picking an item in the list.
        var selIdx = snapshot.FindLastIndex(placeIdx, e => IsListSelect(e));
        if (selIdx < 0)
            return ("Couldn't see the click on the storeroom list before Place. Try again.", null);

        var select = snapshot[selIdx];
        if (select.Values.Any(v => !v.Replayable) || place.Values.Any(v => !v.Replayable))
            return ("The clicks contained text or addresses, which can't be repeated safely. Send the Placement test log over.", null);
        if (place.MenuStrings.Count == 0)
            return ("Couldn't read the menu's entries when Place was chosen. Try again.", null);

        return (null, (select, place));
    }

    private void Save()
    {
        var (select, place) = placeFound!.Value;
        var cancel = cancelFound!;
        if (cancel.Values.Any(v => !v.Replayable))
        {
            Fail("The cancel click contained values that can't be repeated safely.");
            return;
        }

        plugin.Configuration.UiPlace = new UiPlaceProfile
        {
            GameVersion = Plugin.GameVersion,
            LearnedUtc = DateTime.UtcNow,
            ListAddon = select.Addon,
            SelectValues = select.Values,
            SelectIndexParam = select.Values.Length - 1, // [action, list index]: the index is the last value
            SelectClose = select.Close,
            MenuAddon = place.Addon,
            PlaceValues = place.Values,
            PlaceClose = place.Close,
            CancelValues = cancel.Values,
            CancelClose = cancel.Close,
            MenuStrings = place.MenuStrings,
        };
        plugin.Configuration.Save();

        Unhook();
        Active = false;
        LastSucceeded = true;
        Status = $"Learned. Refurnish will choose the menu entry \"{plugin.Configuration.UiPlace!.PlaceEntryText}\".";
    }

    /// <summary>Picking an item in the furnishing list: two numbers, the first is 1 ("select").</summary>
    private static bool IsListSelect(CbEvent e) =>
        e.Addon == "HousingGoods" && e.Values.Length == 2 && e.Values[0].BaseType == 3 && e.Values[0].AsInt == 1
        && e.Values[1].BaseType is 3 or 5;

    /// <summary>A menu choice: first value 0, second the chosen entry (or -1 for closed).</summary>
    private static bool IsChoice(CbEvent e) =>
        e.Values.Length >= 2 && e.Values[0].BaseType == 3 && e.Values[0].AsInt == 0 && e.Values[1].BaseType == 3;

    private static Dictionary<uint, int> CountPlaced() =>
        LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out _)
            .GroupBy(kv => kv.Value.ItemId).ToDictionary(g => g.Key, g => g.Count());

    private void Fail(string message)
    {
        if (Active) Unhook();
        Active = false;
        LastSucceeded = false;
        Status = message;
    }
}
