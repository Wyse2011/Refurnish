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

        var placed = LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out var placedLoaded);
        if (!placedLoaded) return null;

        // A copy of the cache with this house's containers replaced by what's there right now.
        var live = new CharacterCache
        {
            ContentId = cache.ContentId,
            Containers = new Dictionary<string, CachedContainer>(cache.Containers),
            Houses = cache.Houses,
            Retainers = cache.Retainers,
        };
        live.Containers[$"house:{job.HouseId}:placed"] = ToContainer($"house:{job.HouseId}:placed", SourceKind.PlacedInHouse, job.HouseId, placed);

        var room = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out var roomLoaded);
        if (roomLoaded)
            live.Containers[$"house:{job.HouseId}:storeroom"] = ToContainer($"house:{job.HouseId}:storeroom", SourceKind.HouseStoreroom, job.HouseId, room);

        var plan = LayoutPlanner.Build(layout.Interior, live, job.HouseId);
        var candidates = plan.Of(PlanAction.Store)
            .Where(e => e.Item != null && e.ItemId == job.ItemId && e.ActualStain == job.Stain)
            .Select(e => (e.Item!.Container, e.Item.Slot))
            .ToList();

        if (candidates.Count == 0) return null;
        return candidates.Contains((job.Container, job.Slot)) ? (job.Container, job.Slot) : candidates[0];
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
