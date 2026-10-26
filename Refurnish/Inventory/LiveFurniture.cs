using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Refurnish.Inventory;

/// <summary>Where each placed furnishing in the current room actually is, read from the game.</summary>
public static unsafe class LiveFurniture
{
    public readonly record struct Entry(int Index, uint Id, Vector3 Position, float Rotation);

    /// <summary>Placed furniture keyed by the game's object index, or null if not inside a room.</summary>
    public static Dictionary<int, Entry>? Read()
    {
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return null;

        var result = new Dictionary<int, Entry>();
        foreach (var p in hm->IndoorTerritory->FurnitureManager.FurnitureVector.AsSpan())
        {
            var f = p.Value;
            if (f == null) continue;
            result[f->Index] = new Entry(f->Index, f->Id, new Vector3(f->Position.X, f->Position.Y, f->Position.Z), f->Rotation);
        }
        return result;
    }

    /// <summary>Entries present now that weren't before.</summary>
    public static List<Entry> New(Dictionary<int, Entry> before, Dictionary<int, Entry> now)
    {
        var list = new List<Entry>();
        foreach (var (index, entry) in now)
            if (!before.ContainsKey(index)) list.Add(entry);
        return list;
    }

    /// <summary>Difference between two angles in radians, ignoring full turns.</summary>
    public static float AngleDiff(float a, float b)
    {
        var d = (a - b) % (2 * System.MathF.PI);
        if (d > System.MathF.PI) d -= 2 * System.MathF.PI;
        if (d < -System.MathF.PI) d += 2 * System.MathF.PI;
        return System.MathF.Abs(d);
    }
}
