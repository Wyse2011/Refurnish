using System;
using System.Collections.Generic;
using System.Linq;
using Refurnish.Inventory;
using Refurnish.Layout;

namespace Refurnish.Planning;

/// <summary>One stop on a trip: who to be, where to go, what to do there, and which items.</summary>
public sealed class TripStop
{
    public required int Order { get; init; }
    public required string Character { get; init; }      // a character's name, or "Any character"
    public required string Where { get; init; }
    public required string What { get; init; }
    public required List<(uint ItemId, byte Stain, int Count)> Items { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public bool HandOff { get; init; }                     // part of passing items to another character via the FC chest
    public bool AlreadyThere { get; init; }                // the items are already where they need to be
}

public sealed class TripPlan
{
    public List<TripStop> Stops { get; } = new();
    public List<(uint ItemId, byte Stain, int Count)> Missing { get; } = new();
    public List<(uint ItemId, byte NeededStain, byte HaveStain, int Count)> WrongDye { get; } = new();
    public int AlreadyPlaced { get; init; }
    public int HandOffCount { get; init; }
    public int FcChestFreeSlots { get; init; } = -1;
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Plans where to fetch everything a layout needs, across all characters. Every character is assumed
/// to be in the same FC with access to the FC chest and the FC house, which act as the hub: items on
/// another character are withdrawn and deposited in the FC chest by that character, then collected by
/// whoever is building.
///
/// Sources are used in order of convenience: already in the room; the room's storeroom; your own bags;
/// the FC chest; other storerooms; your own retainers; then other characters' bags and retainers.
/// </summary>
public static class TripPlanner
{
    private const string Any = "Any character";

    public static TripPlan Build(LoadedLayout layout, List<CharacterCache> characters, ulong currentId, ulong targetHouse,
        Func<ulong, string> roomName)
    {
        // One combined view of every place, remembering who owns each private one.
        var merged = new CharacterCache { ContentId = currentId };
        var owner = new Dictionary<string, (ulong Id, string Name)>();
        foreach (var c in characters)
        {
            var name = string.IsNullOrEmpty(c.CharacterName) ? $"Character {c.ContentId:X}" : c.CharacterName;
            foreach (var k in c.Containers.Values)
            {
                if (k.Kind is SourceKind.FreeCompanyChest or SourceKind.HouseStoreroom or SourceKind.PlacedInHouse)
                {
                    if (!merged.Containers.TryGetValue(k.Key, out var seen) || k.LastSeenUtc > seen.LastSeenUtc)
                        merged.Containers[k.Key] = k;
                    continue;
                }
                // Private places: keep your own under their usual names, prefix other characters'.
                var key = c.ContentId == currentId ? k.Key : $"{c.ContentId:X}:{k.Key}";
                merged.Containers[key] = Copy(k, key);
                owner[key] = (c.ContentId, name);
            }
        }

        bool Mine(CachedContainer k) => !owner.TryGetValue(k.Key, out var o) || o.Id == currentId;
        int Rank(CachedContainer k) => k.Kind switch
        {
            SourceKind.PlacedInHouse when k.HouseId == targetHouse => 0,
            SourceKind.HouseStoreroom when k.HouseId == targetHouse => 1,
            SourceKind.Inventory when Mine(k) => 2,
            SourceKind.Saddlebag when Mine(k) => 3,
            SourceKind.FreeCompanyChest => 4,
            SourceKind.HouseStoreroom => 5,
            SourceKind.Retainer when Mine(k) => 6,
            SourceKind.Inventory => 7,
            SourceKind.Saddlebag => 8,
            SourceKind.Retainer => 9,
            SourceKind.PlacedInHouse => 10,
            _ => 11,
        };

        var plan = LayoutPlanner.Build(layout.Interior, merged, targetHouse, Rank);

        var fcFree = merged.Containers.Values.Where(k => k.Kind == SourceKind.FreeCompanyChest).Sum(k => k.FreeSlots);
        var hasFc = merged.Containers.Values.Any(k => k.Kind == SourceKind.FreeCompanyChest);
        var retrieve = plan.Of(PlanAction.Retrieve).Where(e => e.Source != null).ToList();
        var handOffs = retrieve.Count(e => !Mine(e.Source!) && e.Source!.Kind is SourceKind.Inventory or SourceKind.Saddlebag or SourceKind.Retainer);

        var trip = new TripPlan
        {
            AlreadyPlaced = plan.Count(PlanAction.Keep),
            HandOffCount = handOffs,
            FcChestFreeSlots = hasFc ? fcFree : -1,
        };

        foreach (var g in plan.Of(PlanAction.Missing).GroupBy(e => (e.ItemId, e.NeededStain)))
            trip.Missing.Add((g.Key.ItemId, g.Key.NeededStain, g.Count()));
        foreach (var g in plan.Entries.Where(e => e.DyeMismatch).GroupBy(e => (e.ItemId, e.NeededStain, e.ActualStain)))
            trip.WrongDye.Add((g.Key.ItemId, g.Key.NeededStain, g.Key.ActualStain, g.Count()));

        // ---- Turn sources into stops ----
        var order = 0;
        static List<(uint, byte, int)> Items(IEnumerable<PlanEntry> entries) =>
            entries.GroupBy(e => (e.ItemId, e.ActualStain)).Select(g => (g.Key.ItemId, g.Key.ActualStain, g.Count()))
                   .ToList();

        string Room(ulong id) => roomName(id);
        var bySource = retrieve.GroupBy(e => e.Source!.Key).Select(g => (Source: g.First().Source!, Entries: g.ToList())).ToList();

        // 1. Things you (the current character) can collect yourself: bags, FC chest, storerooms, your retainers.
        foreach (var (src, entries) in bySource.Where(s => Mine(s.Source)).OrderBy(s => Rank(s.Source)))
        {
            var (where, what, there) = src.Kind switch
            {
                SourceKind.HouseStoreroom when src.HouseId == targetHouse =>
                    (Room(targetHouse) + " (storeroom)", "Already in this room's storeroom. Place them from the Retrieve tab.", true),
                SourceKind.Inventory => ("Your bags", "Already in your bags. Place them by hand for now (placing from bags is coming).", true),
                SourceKind.Saddlebag => ("Your saddlebag", "Move them from the saddlebag into your bags.", false),
                SourceKind.FreeCompanyChest => ("FC chest: " + src.Label, "Withdraw these into your bags.", false),
                SourceKind.HouseStoreroom => (Room(src.HouseId) + " (storeroom)", "Go to that room and take these out of its storeroom into your bags.", false),
                SourceKind.Retainer => ("Summoning bell, " + src.Label, "Withdraw these from the retainer into your bags.", false),
                SourceKind.PlacedInHouse => (Room(src.HouseId), "These are placed in another room. Store them there, then collect them.", false),
                _ => (src.Label, "Collect these.", false),
            };
            trip.Stops.Add(new TripStop
            {
                Order = ++order,
                Character = src.Kind is SourceKind.Inventory or SourceKind.Saddlebag or SourceKind.Retainer ? "You" : Any,
                Where = where,
                What = what,
                Items = Items(entries),
                LastSeenUtc = src.LastSeenUtc,
                AlreadyThere = there,
            });
        }

        // 2. Other characters: withdraw into their bags, deposit into the FC chest.
        var others = bySource.Where(s => !Mine(s.Source)).GroupBy(s => owner[s.Source.Key].Name).OrderBy(g => g.Key);
        var handed = new List<PlanEntry>();
        foreach (var character in others)
        {
            foreach (var (src, entries) in character.OrderBy(s => Rank(s.Source)))
            {
                var where = src.Kind switch
                {
                    SourceKind.Retainer => "Summoning bell, " + src.Label,
                    SourceKind.Saddlebag => "Saddlebag",
                    _ => "Bags",
                };
                var what = src.Kind switch
                {
                    SourceKind.Retainer => "Withdraw these into your bags, then deposit them in the FC chest.",
                    SourceKind.Saddlebag => "Move these into your bags, then deposit them in the FC chest.",
                    _ => "Deposit these in the FC chest.",
                };
                trip.Stops.Add(new TripStop
                {
                    Order = ++order,
                    Character = character.Key,
                    Where = where,
                    What = what,
                    Items = Items(entries),
                    LastSeenUtc = src.LastSeenUtc,
                    HandOff = true,
                });
                handed.AddRange(entries);
            }
        }

        // 3. Collect everything handed off.
        if (handed.Count > 0)
        {
            trip.Stops.Add(new TripStop
            {
                Order = ++order,
                Character = "You",
                Where = "FC chest",
                What = "Withdraw the items your other characters deposited.",
                Items = Items(handed),
                LastSeenUtc = DateTime.UtcNow,
                HandOff = true,
            });
            if (hasFc && handOffs > fcFree)
                trip.Warnings.Add($"{handOffs} items need to pass through the FC chest, but it only has about {fcFree} free slots. "
                                  + "Do the hand-offs in smaller batches.");
            if (!hasFc)
                trip.Warnings.Add("The FC chest hasn't been opened yet, so its free space is unknown.");
        }

        return trip;
    }

    private static CachedContainer Copy(CachedContainer k, string key) => new()
    {
        Key = key,
        Label = k.Label,
        Kind = k.Kind,
        HouseId = k.HouseId,
        LastSeenUtc = k.LastSeenUtc,
        TotalSlots = k.TotalSlots,
        Items = k.Items,
    };

    /// <summary>The trip as plain text, for pasting or printing.</summary>
    public static string ToText(TripPlan trip, string layoutName, Func<uint, string> itemName, Func<byte, string> dyeName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Refurnish trip list for {layoutName}");
        sb.AppendLine($"{trip.AlreadyPlaced} items are already in place.");
        foreach (var w in trip.Warnings) sb.AppendLine("Note: " + w);
        foreach (var s in trip.Stops)
        {
            sb.AppendLine();
            sb.AppendLine($"{s.Order}. {s.Character} - {s.Where}");
            sb.AppendLine($"   {s.What}");
            foreach (var (id, stain, count) in s.Items)
                sb.AppendLine($"   {count} x {itemName(id)}{(stain == 0 ? "" : $" ({dyeName(stain)})")}");
        }
        if (trip.Missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not owned by any character (buy or craft):");
            foreach (var (id, stain, count) in trip.Missing)
                sb.AppendLine($"   {count} x {itemName(id)}{(stain == 0 ? "" : $" ({dyeName(stain)})")}");
        }
        if (trip.WrongDye.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Owned, but dyed differently from the layout:");
            foreach (var (id, need, have, count) in trip.WrongDye)
                sb.AppendLine($"   {count} x {itemName(id)}: is {dyeName(have)}, layout wants {dyeName(need)}");
        }
        return sb.ToString();
    }
}
