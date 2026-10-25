using System.Collections.Generic;
using System.Linq;
using Refurnish.Inventory;
using Refurnish.Layout;

namespace Refurnish.Planning;

public enum PlanAction
{
    /// <summary>Already placed in the target house. MakePlace will move it into position.</summary>
    Keep,

    /// <summary>Needs to be brought out and placed (from the storeroom, inventory, a retainer, etc.).</summary>
    Retrieve,

    /// <summary>Placed in the target house but not part of the new layout.</summary>
    Store,

    /// <summary>Not found anywhere Refurnish has seen.</summary>
    Missing,
}

public sealed record PlanEntry(
    PlanAction Action,
    uint ItemId,
    byte NeededStain,
    byte ActualStain,
    CachedContainer? Source)
{
    public bool DyeMismatch => Action is PlanAction.Keep or PlanAction.Retrieve && NeededStain != ActualStain;
}

public sealed class LayoutPlan
{
    public List<PlanEntry> Entries { get; } = new();

    public IEnumerable<PlanEntry> Of(PlanAction action) => Entries.Where(e => e.Action == action);
    public int Count(PlanAction action) => Entries.Count(e => e.Action == action);
    public int DyeMismatches => Entries.Count(e => e.DyeMismatch);

    /// <summary>Retrievals that need a trip somewhere (not already in the house storeroom or your bags).</summary>
    public int NeedsTrip => Of(PlanAction.Retrieve).Count(e => !IsAtHand(e.Source));

    public bool IsAtHand(CachedContainer? source) => source != null && (
        source.Kind == SourceKind.Inventory
        || (source.Kind == SourceKind.HouseStoreroom && source.HouseId == TargetHouseId));

    public ulong TargetHouseId { get; init; }
    public CachedContainer? TargetStoreroom { get; init; }
    public CachedContainer? PlayerInventory { get; init; }
}

/// <summary>
/// Works out, for a layout and a target house, what to keep, store, retrieve, and what's missing.
/// Pure logic over cached data, so it never touches the game.
/// </summary>
public static class LayoutPlanner
{
    private sealed class Instance
    {
        public required uint ItemId;
        public required byte Stain;
        public required CachedContainer Source;
        public required int Priority;
        public bool Used;
    }

    public static LayoutPlan Build(IReadOnlyList<Requirement> requirements, CharacterCache cache, ulong targetHouseId)
    {
        var placedKey = $"house:{targetHouseId}:placed";
        var storeKey = $"house:{targetHouseId}:storeroom";
        cache.Containers.TryGetValue(storeKey, out var targetStoreroom);
        cache.Containers.TryGetValue("inventory", out var inventory);

        var plan = new LayoutPlan
        {
            TargetHouseId = targetHouseId,
            TargetStoreroom = targetStoreroom,
            PlayerInventory = inventory,
        };

        var neededIds = requirements.Select(r => r.ItemId).ToHashSet();

        // Every copy of every relevant item we know about, cheapest-to-use first.
        var pools = new Dictionary<uint, List<Instance>>();
        foreach (var container in cache.Containers.Values)
        {
            var isTargetPlaced = container.Key == placedKey;
            var priority = Priority(container, targetHouseId);

            foreach (var item in container.Items)
            {
                // Placed items in the target house matter even when unneeded (they get stored).
                if (!isTargetPlaced && !neededIds.Contains(item.ItemId)) continue;

                if (!pools.TryGetValue(item.ItemId, out var pool))
                    pools[item.ItemId] = pool = new List<Instance>();

                for (var q = 0; q < item.Quantity; q++)
                    pool.Add(new Instance { ItemId = item.ItemId, Stain = item.Stain, Source = container, Priority = priority });
            }
        }
        foreach (var pool in pools.Values)
            pool.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        foreach (var group in requirements.GroupBy(r => r.ItemId))
        {
            pools.TryGetValue(group.Key, out var pool);
            pool ??= new List<Instance>();
            var unmatched = new List<Requirement>();

            // Pass 1: a copy with exactly the right dye.
            foreach (var need in group)
            {
                var hit = pool.FirstOrDefault(i => !i.Used && i.Stain == need.Stain);
                if (hit == null) { unmatched.Add(need); continue; }
                Use(plan, hit, need);
            }

            // Pass 2: any remaining copy, flagged so the user knows to dye it.
            foreach (var need in unmatched)
            {
                var hit = pool.FirstOrDefault(i => !i.Used);
                if (hit == null)
                {
                    plan.Entries.Add(new PlanEntry(PlanAction.Missing, need.ItemId, need.Stain, 0, null));
                    continue;
                }
                Use(plan, hit, need);
            }
        }

        // Anything still placed in the target house isn't in the new layout.
        foreach (var inst in pools.Values.SelectMany(p => p).Where(i => !i.Used && i.Source.Key == placedKey))
            plan.Entries.Add(new PlanEntry(PlanAction.Store, inst.ItemId, 0, inst.Stain, inst.Source));

        return plan;
    }

    private static void Use(LayoutPlan plan, Instance inst, Requirement need)
    {
        inst.Used = true;
        var action = inst.Priority == 0 ? PlanAction.Keep : PlanAction.Retrieve;
        plan.Entries.Add(new PlanEntry(action, need.ItemId, need.Stain, inst.Stain, inst.Source));
    }

    // Lower is better: use what's already placed, then what's closest at hand, and only take
    // furniture out of another house's layout as a last resort.
    private static int Priority(CachedContainer c, ulong target) => c.Kind switch
    {
        SourceKind.PlacedInHouse when c.HouseId == target => 0,
        SourceKind.HouseStoreroom when c.HouseId == target => 1,
        SourceKind.Inventory => 2,
        SourceKind.Saddlebag => 3,
        SourceKind.HouseStoreroom => 4,
        SourceKind.Retainer => 5,
        SourceKind.FreeCompanyChest => 6,
        SourceKind.PlacedInHouse => 7,
        _ => 8,
    };
}
