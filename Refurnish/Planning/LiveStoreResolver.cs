using System;
using System.Collections.Generic;
using System.Linq;
using Refurnish.Automation;
using Refurnish.Inventory;
using Refurnish.Layout;

namespace Refurnish.Planning;

/// <summary>
/// Right before each item is stored, re-runs the planner on a live read of the house so the
/// decision never rests on cached data. Returns the slot that currently holds an unneeded copy of
/// the job's item (preferring the original slot), or null if the layout needs every copy.
/// </summary>
public static class LiveStoreResolver
{
    public static (int Container, int Slot)? Resolve(LoadedLayout? layout, CharacterCache? cache, StoreJob job)
    {
        if (layout == null || cache == null) return null;

        var live = LiveCache(cache, job.HouseId);
        if (live == null) return null;

        var plan = LayoutPlanner.Build(layout.Interior, live, job.HouseId);
        var candidates = plan.Of(PlanAction.Store)
            .Where(e => e.Item != null && e.ItemId == job.ItemId && e.ActualStain == job.Stain)
            .Select(e => (e.Item!.Container, e.Item.Slot))
            .ToList();

        if (candidates.Count == 0) return null;
        return candidates.Contains((job.Container, job.Slot)) ? (job.Container, job.Slot) : candidates[0];
    }

    /// <summary>
    /// For placing: re-plans from a live read and returns the slot holding a copy of the job's item
    /// (same dye) that the layout still needs and that's at hand, plus where it should go.
    /// Null if the layout no longer needs it.
    /// </summary>
    public static (int Container, int Slot, Requirement Need)? ResolvePlace(LoadedLayout? layout, CharacterCache? cache, PlaceJob job)
    {
        if (layout == null || cache == null) return null;
        var live = LiveCache(cache, job.HouseId);
        if (live == null) return null;

        var plan = LayoutPlanner.Build(layout.Interior, live, job.HouseId);
        var candidates = plan.Of(PlanAction.Retrieve)
            .Where(e => e.Item != null && e.Need != null && e.ItemId == job.ItemId && e.ActualStain == job.Stain
                        && plan.IsAtHand(e.Source) && SameSource(e.Item.Container, job.Container))
            .ToList();
        if (candidates.Count == 0) return null;

        var pick = candidates.FirstOrDefault(e => e.Item!.Container == job.Container && e.Item.Slot == job.Slot) ?? candidates[0];
        return (pick.Item!.Container, pick.Item.Slot, pick.Need!.Value);
    }

    private static bool SameSource(int a, int b) =>
        LiveInventory.InteriorStoreroom.Contains(a) == LiveInventory.InteriorStoreroom.Contains(b);

    /// <summary>A copy of the cache with this room's placed items, storeroom and your bags read live.</summary>
    private static CharacterCache? LiveCache(CharacterCache cache, ulong houseId)
    {
        var placed = LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out var placedLoaded);
        if (!placedLoaded) return null;

        var live = new CharacterCache
        {
            ContentId = cache.ContentId,
            Containers = new Dictionary<string, CachedContainer>(cache.Containers),
            Houses = cache.Houses,
            Retainers = cache.Retainers,
        };
        live.Containers[$"house:{houseId}:placed"] = ToContainer($"house:{houseId}:placed", SourceKind.PlacedInHouse, houseId, placed);

        var room = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out var roomLoaded);
        if (roomLoaded)
            live.Containers[$"house:{houseId}:storeroom"] = ToContainer($"house:{houseId}:storeroom", SourceKind.HouseStoreroom, houseId, room);

        var bag = LiveInventory.ReadOccupied(LiveInventory.PlayerInventory, out var bagLoaded);
        if (bagLoaded)
            live.Containers["inventory"] = ToContainer("inventory", SourceKind.Inventory, 0, bag);

        return live;
    }

    private static CachedContainer ToContainer(string key, SourceKind kind, ulong houseId,
        Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> slots) => new()
    {
        Key = key,
        Label = key,
        Kind = kind,
        HouseId = houseId,
        LastSeenUtc = DateTime.UtcNow,
        Items = slots.Select(kv => new CachedItem
        {
            ItemId = kv.Value.ItemId,
            Stain = kv.Value.Stain,
            Quantity = 1,
            Container = kv.Key.Container,
            Slot = kv.Key.Slot,
        }).ToList(),
    };
}
