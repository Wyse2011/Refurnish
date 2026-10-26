using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Refurnish.Inventory;

/// <summary>
/// Direct, up-to-the-moment reads of game containers. Automation uses these instead of the cache
/// so it never acts on old information.
/// </summary>
public static unsafe class LiveInventory
{
    // Raw container numbers (FFXIVClientStructs InventoryType). Numbers rather than enum names so
    // that newer house pages are picked up even if the bundled ClientStructs is a bit older.
    public static readonly int[] PlayerInventory = { 0, 1, 2, 3 };
    public static readonly int[] Saddlebag = { 4000, 4001, 4100, 4101 };
    public static readonly int[] RetainerPages = Enumerable.Range(10000, 7).ToArray();
    public const int FreeCompanyFirstPage = 20000;
    public const int FreeCompanyPageCount = 5;
    public static readonly int[] InteriorPlaced = Enumerable.Range(25003, 12).ToArray();
    public static readonly int[] InteriorStoreroom = Enumerable.Range(27001, 11).ToArray();

    public static bool IsInteriorPlaced(int container) => container >= 25003 && container <= 25014;

    /// <summary>Reads one slot. False if the container isn't loaded or the slot is empty.</summary>
    public static bool TryGetSlot(int container, int slot, out uint itemId, out byte stain)
    {
        itemId = 0;
        stain = 0;
        var mgr = InventoryManager.Instance();
        if (mgr == null) return false;
        var c = mgr->GetInventoryContainer((InventoryType)container);
        if (c == null || !c->IsLoaded || slot < 0 || slot >= c->Size) return false;
        var item = c->GetInventorySlot(slot);
        if (item == null || item->IsEmpty()) return false;
        itemId = item->GetBaseItemId();
        stain = item->GetStain(0);
        return true;
    }

    /// <summary>Every occupied slot across the given containers.</summary>
    public static Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> ReadOccupied(int[] types, out bool anyLoaded)
    {
        var result = new Dictionary<(int, int), (uint, byte)>();
        anyLoaded = false;
        var mgr = InventoryManager.Instance();
        if (mgr == null) return result;

        foreach (var t in types)
        {
            var c = mgr->GetInventoryContainer((InventoryType)t);
            if (c == null || !c->IsLoaded) continue;
            anyLoaded = true;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->IsEmpty()) continue;
                result[(t, i)] = (item->GetBaseItemId(), item->GetStain(0));
            }
        }
        return result;
    }

    /// <summary>Free slots across the given containers, or -1 if none of them are loaded.</summary>
    public static int FreeSlots(int[] types)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return -1;
        var free = 0;
        var anyLoaded = false;
        foreach (var t in types)
        {
            var c = mgr->GetInventoryContainer((InventoryType)t);
            if (c == null || !c->IsLoaded) continue;
            anyLoaded = true;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->IsEmpty()) free++;
            }
        }
        return anyLoaded ? free : -1;
    }

    /// <summary>Total quantity of one item across the given containers.</summary>
    public static int CountItem(int[] types, uint itemId)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return 0;
        var total = 0;
        foreach (var t in types)
        {
            var c = mgr->GetInventoryContainer((InventoryType)t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var item = c->GetInventorySlot(i);
                if (item == null || item->IsEmpty() || item->GetBaseItemId() != itemId) continue;
                total += (int)System.Math.Max(1, item->GetQuantity());
            }
        }
        return total;
    }
}
