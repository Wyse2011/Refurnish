using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Refurnish.Layout;

// The subset of the MakePlace layout format that Refurnish needs. The MakePlace app and the
// MakePlace Dalamud plugin share this format. Unknown fields are ignored.
public sealed class MakePlaceFile
{
    public string? houseSize { get; set; }
    public float interiorScale { get; set; } = 1;
    public List<MakePlaceFurniture> interiorFurniture { get; set; } = new();
    public List<MakePlaceFurniture> exteriorFurniture { get; set; } = new();
}

public sealed class MakePlaceFurniture
{
    public string name { get; set; } = string.Empty;
    public uint itemId { get; set; }
    public MakePlaceTransform? transform { get; set; }
    public Dictionary<string, JsonElement>? properties { get; set; }
    public List<MakePlaceFurniture>? attachments { get; set; }

    /// <summary>Dye color as "RRGGBBAA", or null when undyed.</summary>
    public string? Color =>
        properties != null && properties.TryGetValue("color", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    /// <summary>Item placed into this one (a painting in a frame, a fish in an aquarium), if any.</summary>
    public uint MaterialItemId
    {
        get
        {
            if (properties == null || !properties.TryGetValue("material", out var m) || m.ValueKind != JsonValueKind.Object)
                return 0;
            return m.TryGetProperty("itemId", out var id) && id.TryGetUInt32(out var v) ? v : 0;
        }
    }
}

public sealed class MakePlaceTransform
{
    public List<float>? location { get; set; }
    public List<float>? rotation { get; set; }
}

/// <summary>
/// One furnishing the layout needs: an item, the dye it should have (0 = undyed), and where it
/// goes in game coordinates (position, and rotation in radians around the vertical axis).
/// </summary>
public readonly record struct Requirement(uint ItemId, byte Stain, Vector3 Position = default, float Rotation = 0, bool HasPosition = false);

public sealed class LoadedLayout
{
    public required string Path { get; init; }
    public required string? HouseSize { get; init; }
    public required List<Requirement> Interior { get; init; }
    public required List<uint> InteriorMaterials { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public static LoadedLayout Load(string path, StainMatcher stains)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<MakePlaceFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidDataException("The file is empty or isn't a MakePlace layout.");

        var reqs = new List<Requirement>();
        var materials = new List<uint>();
        var scale = file.interiorScale > 0 ? file.interiorScale : 1f;
        Flatten(file.interiorFurniture, reqs, materials, stains, scale);

        return new LoadedLayout
        {
            Path = path,
            HouseSize = file.houseSize,
            Interior = reqs,
            InteriorMaterials = materials,
        };
    }

    // Items sitting on tables or hung on walls are nested under "attachments", so walk them too.
    private static void Flatten(List<MakePlaceFurniture>? list, List<Requirement> reqs, List<uint> materials, StainMatcher stains, float scale)
    {
        if (list == null) return;
        foreach (var f in list)
        {
            if (f.itemId != 0)
            {
                var (pos, rot, has) = ToGame(f.transform, scale);
                reqs.Add(new Requirement(f.itemId, stains.FromHex(f.Color), pos, rot, has));
            }

            var mat = f.MaterialItemId;
            if (mat != 0) materials.Add(mat);

            Flatten(f.attachments, reqs, materials, stains, scale);
        }
    }

    // Same conversion the MakePlace plugin uses: the file stores X, Z(height last) and a quaternion;
    // the game uses X, Y(up), Z and a single yaw angle.
    private static (Vector3 Pos, float Rot, bool Has) ToGame(MakePlaceTransform? t, float scale)
    {
        if (t?.location is not { Count: >= 3 } l) return (default, 0, false);
        var pos = new Vector3(l[0], l[2], l[1]) / scale;

        float rot = 0;
        if (t.rotation is { Count: >= 4 } r)
        {
            var q = new Quaternion(r[0], r[1], r[2], r[3]);
            var sinyCosp = 2 * (q.W * q.Z + q.X * q.Y);
            var cosyCosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
            rot = -(float)Math.Atan2(sinyCosp, cosyCosp);
        }
        return (pos, rot, true);
    }
}
