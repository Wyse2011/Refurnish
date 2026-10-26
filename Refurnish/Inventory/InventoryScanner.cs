using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Refurnish.Inventory;

/// <summary>
/// Once a second, reads whichever item containers the game currently has loaded and records them
/// in the cache. This is read-only: it never moves anything.
///
/// The game only loads a retainer's items while that retainer is open at a summoning bell, an FC
/// chest compartment once you've viewed it, and a house's storeroom and placed furniture while
/// you're inside it. So each of those gets recorded when you visit it and remembered afterwards.
/// </summary>
public sealed unsafe class InventoryScanner : IDisposable
{
    private static int[] PlayerInventory => LiveInventory.PlayerInventory;
    private static int[] Saddlebag => LiveInventory.Saddlebag;
    private static int[] RetainerPages => LiveInventory.RetainerPages;
    private const int FreeCompanyFirstPage = LiveInventory.FreeCompanyFirstPage;
    private const int FreeCompanyPageCount = LiveInventory.FreeCompanyPageCount;
    private static int[] InteriorPlaced => LiveInventory.InteriorPlaced;
    private static int[] InteriorStoreroom => LiveInventory.InteriorStoreroom;

    private readonly CacheStore store;
    private DateTime nextScan = DateTime.MinValue;
    private bool loggedError;

    public InventoryScanner(CacheStore store)
    {
        this.store = store;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    /// <summary>The house the player is standing in right now (0 if none, or no permissions there).</summary>
    public ulong CurrentHouseId { get; private set; }

    /// <summary>True when inside a house or room you can't decorate (so it can't be read or changed).</summary>
    public bool InsideWithoutPermission { get; private set; }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextScan) return;
        nextScan = DateTime.UtcNow.AddSeconds(1);

        try
        {
            if (Plugin.PlayerState.IsLoaded && Plugin.PlayerState.ContentId != 0)
                Scan();
            else
                CurrentHouseId = 0;

            store.SaveIfDue();
        }
        catch (Exception ex)
        {
            if (!loggedError) Plugin.Log.Error(ex, "Refurnish inventory scan failed");
            loggedError = true;
        }
    }

    private void Scan()
    {
        var contentId = Plugin.PlayerState.ContentId;
        var cache = store.GetOrCreate(contentId, Plugin.PlayerState.CharacterName);

        Snapshot(cache, "inventory", "Inventory", SourceKind.Inventory, 0, PlayerInventory);
        Snapshot(cache, "saddlebag", "Saddlebag", SourceKind.Saddlebag, 0, Saddlebag);

        for (var p = 0; p < FreeCompanyPageCount; p++)
            Snapshot(cache, $"fc:{p + 1}", $"FC chest, compartment {p + 1}", SourceKind.FreeCompanyChest, 0,
                new[] { FreeCompanyFirstPage + p });

        ScanRetainers(cache);
        ScanHouse(cache);
    }

    private void ScanRetainers(CharacterCache cache)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return;

        // Remember the full retainer list so the window can point out ones not opened yet.
        var count = rm->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            var name = r->NameString;
            if (!cache.Retainers.TryGetValue(r->RetainerId, out var known) || known != name)
            {
                cache.Retainers[r->RetainerId] = name;
                store.MarkDirty(cache.ContentId);
            }
        }

        var active = rm->GetActiveRetainer();
        if (active == null || active->RetainerId == 0) return;

        Snapshot(cache, $"retainer:{active->RetainerId}", $"Retainer: {active->NameString}",
            SourceKind.Retainer, 0, RetainerPages);
    }

    private void ScanHouse(CharacterCache cache)
    {
        CurrentHouseId = 0;
        InsideWithoutPermission = false;

        var hm = HousingManager.Instance();
        if (hm == null || !hm->IsInside()) return;
        if (!hm->HasHousePermissions())
        {
            InsideWithoutPermission = true;
            return;
        }

        var house = hm->GetCurrentIndoorHouseId();
        if (house.Id == 0 || house.Id == ulong.MaxValue || house.IsWorkshop) return;

        CurrentHouseId = house.Id;

        // Each room (an FC house's main floor, each private chamber, each apartment) has its own ID,
        // furniture and permissions, so each is tracked separately.
        var label = DescribeHouse(house);
        if (!cache.Houses.TryGetValue(house.Id, out var known))
        {
            known = new KnownHouse { HouseId = house.Id, Label = label };
            cache.Houses[house.Id] = known;
            store.MarkDirty(cache.ContentId);
        }
        else if (known.Label != label)
        {
            known.Label = label;
            store.MarkDirty(cache.ContentId);
        }
        known.LastVisitedUtc = DateTime.UtcNow;

        Snapshot(cache, $"house:{house.Id}:placed", $"{known.Label}: placed", SourceKind.PlacedInHouse, house.Id, InteriorPlaced);
        Snapshot(cache, $"house:{house.Id}:storeroom", $"{known.Label}: storeroom", SourceKind.HouseStoreroom, house.Id, InteriorStoreroom);
    }

    private static string DescribeHouse(HouseId house)
    {
        var area = string.Empty;
        if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(house.TerritoryTypeId, out var terr))
            area = terr.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

        var ward = house.WardIndex + 1;
        var room = house.RoomNumber;
        string where;
        if (house.IsApartment)
            where = $"Ward {ward} apartment, room {room}";
        else if (room > 0)
            where = $"Ward {ward}, Plot {house.PlotIndex + 1}, private chamber {room}";
        else
            where = $"Ward {ward}, Plot {house.PlotIndex + 1}";

        return string.IsNullOrEmpty(area) ? where : $"{area}, {where}";
    }

    /// <summary>
    /// Reads every loaded container in <paramref name="types"/> and stores them together under
    /// <paramref name="key"/>. Does nothing if none are loaded, so the last known contents stay.
    /// </summary>
    private void Snapshot(CharacterCache cache, string key, string label, SourceKind kind, ulong houseId, int[] types)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return;

        var items = new List<CachedItem>();
        var totalSlots = 0;
        var anyLoaded = false;

        foreach (var t in types)
        {
            var type = (InventoryType)t;
            var container = mgr->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            anyLoaded = true;
            totalSlots += container->Size;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->IsEmpty()) continue;

                items.Add(new CachedItem
                {
                    ItemId = slot->GetBaseItemId(),
                    Stain = slot->GetStain(0),
                    Quantity = (int)Math.Max(1, slot->GetQuantity()),
                    Container = t,
                    Slot = i,
                });
            }
        }

        if (!anyLoaded) return;

        var now = DateTime.UtcNow;
        if (cache.Containers.TryGetValue(key, out var existing)
            && existing.Label == label
            && existing.TotalSlots == totalSlots
            && SameItems(existing.Items, items))
        {
            // Unchanged. Refresh the "last seen" time, but only bother saving it every few minutes.
            if (now - existing.LastSeenUtc > TimeSpan.FromMinutes(5))
            {
                existing.LastSeenUtc = now;
                store.MarkDirty(cache.ContentId);
            }
            else
            {
                existing.LastSeenUtc = now;
            }
            return;
        }

        cache.Containers[key] = new CachedContainer
        {
            Key = key,
            Label = label,
            Kind = kind,
            HouseId = houseId,
            LastSeenUtc = now,
            TotalSlots = totalSlots,
            Items = items,
        };
        store.MarkDirty(cache.ContentId);
    }

    private static bool SameItems(List<CachedItem> a, List<CachedItem> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.ItemId != y.ItemId || x.Stain != y.Stain || x.Quantity != y.Quantity
                || x.Container != y.Container || x.Slot != y.Slot)
                return false;
        }
        return true;
    }
}
