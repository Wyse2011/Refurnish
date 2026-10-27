using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Lumina.Excel.Sheets;
using Refurnish.Inventory;
using Refurnish.Layout;

namespace Refurnish.Planning;

public enum CheckResult
{
    Correct,
    WrongRotation,
    WrongDye,
    OutOfPlace,
    Missing,
    Extra,
}

public sealed record CheckLine(CheckResult Result, uint ItemId, byte WantedStain, byte ActualStain,
    Vector3? Wanted, Vector3? Actual, float WantedRotation, float ActualRotation, float Distance);

/// <summary>
/// Compares the loaded layout with what's actually in the room right now, read live from the game:
/// every layout spot should have the right item, in the right dye, at the right position, facing
/// the right way. Also lists furniture in the room that the layout doesn't use.
/// </summary>
public static class LayoutCheck
{
    public const float PositionTolerance = 0.05f;
    public const float RotationTolerance = 0.02f; // radians, about 1 degree

    private static Dictionary<uint, uint>? furnitureToItem;

    public static List<CheckLine> Run(LoadedLayout layout, float heightShift)
    {
        var lines = new List<CheckLine>();
        var live = ReadLive();
        if (live == null) return lines;

        var shift = new Vector3(0, heightShift, 0);
        var wanted = layout.Interior.Where(r => r.HasPosition).ToList();
        var used = new bool[live.Count];

        // Pass 1: exact matches (right item at the right spot), so near-misses don't steal them.
        var unmatched = new List<Requirement>();
        foreach (var r in wanted)
        {
            var spot = r.Position + shift;
            var i = Best(live, used, r.ItemId, spot, PositionTolerance);
            if (i < 0) { unmatched.Add(r); continue; }
            used[i] = true;
            var f = live[i];
            var turn = LiveFurniture.AngleDiff(f.Rotation, r.Rotation);
            var result = turn > RotationTolerance ? CheckResult.WrongRotation
                : r.Stain != f.Stain ? CheckResult.WrongDye
                : CheckResult.Correct;
            lines.Add(new CheckLine(result, r.ItemId, r.Stain, f.Stain, spot, f.Position, r.Rotation, f.Rotation,
                Vector3.Distance(f.Position, spot)));
        }

        // Pass 2: the nearest remaining copy of the item, wherever it is; otherwise it's missing.
        foreach (var r in unmatched)
        {
            var spot = r.Position + shift;
            var i = Best(live, used, r.ItemId, spot, float.MaxValue);
            if (i < 0)
            {
                lines.Add(new CheckLine(CheckResult.Missing, r.ItemId, r.Stain, 0, spot, null, r.Rotation, 0, 0));
                continue;
            }
            used[i] = true;
            var f = live[i];
            lines.Add(new CheckLine(CheckResult.OutOfPlace, r.ItemId, r.Stain, f.Stain, spot, f.Position, r.Rotation, f.Rotation,
                Vector3.Distance(f.Position, spot)));
        }

        // Anything left in the room isn't part of the layout.
        for (var i = 0; i < live.Count; i++)
        {
            if (used[i]) continue;
            var f = live[i];
            lines.Add(new CheckLine(CheckResult.Extra, f.ItemId, 0, f.Stain, null, f.Position, 0, f.Rotation, 0));
        }
        return lines;
    }

    private static int Best(List<LiveItem> live, bool[] used, uint itemId, Vector3 spot, float within)
    {
        var best = -1;
        var bestDist = within;
        for (var i = 0; i < live.Count; i++)
        {
            if (used[i] || live[i].ItemId != itemId) continue;
            var d = Vector3.Distance(live[i].Position, spot);
            if (d <= bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    public readonly record struct LiveItem(uint ItemId, byte Stain, Vector3 Position, float Rotation);

    /// <summary>Every placed furnishing in the current room: which item, its dye, position and rotation.</summary>
    private static unsafe List<LiveItem>? ReadLive()
    {
        var hm = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return null;
        var list = new List<LiveItem>();
        foreach (var p in hm->IndoorTerritory->FurnitureManager.FurnitureVector.AsSpan())
        {
            var f = p.Value;
            if (f == null) continue;
            list.Add(new LiveItem(ItemFor(f->Id), f->Stain, new Vector3(f->Position.X, f->Position.Y, f->Position.Z), f->Rotation));
        }
        return list;
    }

    /// <summary>Maps a placed furnishing's furniture row to its item.</summary>
    public static uint ItemFor(uint furnitureId)
    {
        furnitureToItem ??= BuildMap();
        // Indoors the game stores the row without its high bits (FFXIVClientStructs: 0x20000 | Id).
        foreach (var candidate in new[] { 0x20000u | furnitureId, furnitureId, 0x30000u | furnitureId })
            if (furnitureToItem.TryGetValue(candidate, out var item)) return item;
        return 0;
    }

    private static Dictionary<uint, uint> BuildMap()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<HousingFurniture>())
        {
            var item = row.Item.RowId;
            if (item != 0) map[row.RowId] = item;
        }
        return map;
    }

    public static string Report(List<CheckLine> lines, Func<uint, string> name, Func<byte, string> dye)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Refurnish {Plugin.Version} layout check: {lines.Count(l => l.Result == CheckResult.Correct)} correct, "
                      + $"{lines.Count(l => l.Result is not (CheckResult.Correct or CheckResult.Extra))} need attention, "
                      + $"{lines.Count(l => l.Result == CheckResult.Extra)} extra.");
        foreach (var group in lines.Where(l => l.Result != CheckResult.Correct).GroupBy(l => l.Result))
        {
            sb.AppendLine();
            sb.AppendLine($"{group.Key} ({group.Count()}):");
            foreach (var l in group) sb.AppendLine("  " + Describe(l, name, dye));
        }
        return sb.ToString();
    }

    public static string Describe(CheckLine l, Func<uint, string> name, Func<byte, string> dye)
    {
        static string P(Vector3? v) => v is { } p ? $"({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})" : "-";
        static float Deg(float r) => r * 180f / MathF.PI;
        return l.Result switch
        {
            CheckResult.WrongRotation => $"{name(l.ItemId)} at {P(l.Actual)}: facing {Deg(l.ActualRotation):0}° instead of {Deg(l.WantedRotation):0}°",
            CheckResult.WrongDye => $"{name(l.ItemId)} at {P(l.Actual)}: {dye(l.ActualStain)} instead of {dye(l.WantedStain)}",
            CheckResult.OutOfPlace => $"{name(l.ItemId)}: at {P(l.Actual)}, should be at {P(l.Wanted)} ({l.Distance:0.00} away)",
            CheckResult.Missing => $"{name(l.ItemId)}: nothing at {P(l.Wanted)}",
            CheckResult.Extra => $"{name(l.ItemId)} at {P(l.Actual)}: not in the layout",
            _ => $"{name(l.ItemId)} at {P(l.Actual)}",
        };
    }
}
