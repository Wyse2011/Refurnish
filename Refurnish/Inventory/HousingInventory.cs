using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Refurnish.Inventory;

/// <summary>One furnishing (item + dye) in one place, with how many there are.</summary>
public sealed record InventoryRow(string Character, string Location, string Type, uint ItemId, byte Stain, int Quantity,
    DateTime LastSeenUtc, bool Shared);

/// <summary>
/// Every housing furnishing Refurnish has seen, across all characters. Places only one character can
/// open (bags, saddlebag, retainers) are listed under that character. Shared places (the FC chest and
/// house storerooms and placed furniture) appear in every character's saved data, so each is counted
/// once, using whichever character saw it most recently.
/// </summary>
public static class HousingInventory
{
    public static List<InventoryRow> Build(IEnumerable<CharacterCache> characters)
    {
        var rows = new List<InventoryRow>();
        var shared = new Dictionary<string, CachedContainer>();

        foreach (var character in characters)
        {
            var name = string.IsNullOrEmpty(character.CharacterName) ? $"Character {character.ContentId:X}" : character.CharacterName;
            foreach (var container in character.Containers.Values)
            {
                if (IsShared(container))
                {
                    if (!shared.TryGetValue(container.Key, out var seen) || container.LastSeenUtc > seen.LastSeenUtc)
                        shared[container.Key] = container;
                    continue;
                }
                Add(rows, name, container, shared: false);
            }
        }

        foreach (var container in shared.Values)
            Add(rows, container.Kind == SourceKind.FreeCompanyChest ? "FC (shared)" : "House (shared)", container, shared: true);

        return rows;
    }

    private static bool IsShared(CachedContainer c) =>
        c.Kind is SourceKind.FreeCompanyChest or SourceKind.HouseStoreroom or SourceKind.PlacedInHouse;

    private static void Add(List<InventoryRow> rows, string character, CachedContainer container, bool shared)
    {
        foreach (var g in container.Items.Where(i => FurnishingCatalog.IsFurnishing(i.ItemId)).GroupBy(i => (i.ItemId, i.Stain)))
            rows.Add(new InventoryRow(character, container.Label, TypeName(container.Kind), g.Key.ItemId, g.Key.Stain,
                g.Sum(i => i.Quantity), container.LastSeenUtc, shared));
    }

    public static string TypeName(SourceKind kind) => kind switch
    {
        SourceKind.Inventory => "Bags",
        SourceKind.Saddlebag => "Saddlebag",
        SourceKind.Retainer => "Retainer",
        SourceKind.FreeCompanyChest => "FC chest",
        SourceKind.HouseStoreroom => "Storeroom",
        SourceKind.PlacedInHouse => "Placed",
        _ => kind.ToString(),
    };

    /// <summary>The rows as CSV (opens in Excel or Google Sheets).</summary>
    public static string ToCsv(IEnumerable<InventoryRow> rows, Func<uint, string> itemName, Func<byte, string> dyeName)
    {
        static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine("Item,Dye,Quantity,Character,Location,Type,Indoor/Outdoor,Last checked");
        foreach (var r in rows.OrderBy(r => itemName(r.ItemId)).ThenBy(r => r.Character).ThenBy(r => r.Location))
        {
            var where = FurnishingCatalog.IsIndoor(r.ItemId) ? "Indoor" : "Outdoor";
            sb.AppendLine(string.Join(",",
                Q(itemName(r.ItemId)), Q(dyeName(r.Stain)), r.Quantity.ToString(), Q(r.Character), Q(r.Location), Q(r.Type), where,
                r.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }
        return sb.ToString();
    }
}
