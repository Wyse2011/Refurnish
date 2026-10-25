using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Refurnish.Layout;

// The subset of the MakePlace layout format that Refurnish needs. The MakePlace app and the
// MakePlace Dalamud plugin share this format. Unknown fields are ignored.
public sealed class MakePlaceFile
{
    public string? houseSize { get; set; }
    public List<MakePlaceFurniture> interiorFurniture { get; set; } = new();
    public List<MakePlaceFurniture> exteriorFurniture { get; set; } = new();
}

public sealed class MakePlaceFurniture
{
    public string name { get; set; } = string.Empty;
    public uint itemId { get; set; }
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

/// <summary>One furnishing the layout needs: an item, and the dye it should have (0 = undyed).</summary>
public readonly record struct Requirement(uint ItemId, byte Stain);

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
        Flatten(file.interiorFurniture, reqs, materials, stains);

        return new LoadedLayout
        {
            Path = path,
            HouseSize = file.houseSize,
            Interior = reqs,
            InteriorMaterials = materials,
        };
    }

    // Items sitting on tables or hung on walls are nested under "attachments", so walk them too.
    private static void Flatten(List<MakePlaceFurniture>? list, List<Requirement> reqs, List<uint> materials, StainMatcher stains)
    {
        if (list == null) return;
        foreach (var f in list)
        {
            if (f.itemId != 0)
                reqs.Add(new Requirement(f.itemId, stains.FromHex(f.Color)));

            var mat = f.MaterialItemId;
            if (mat != 0) materials.Add(mat);

            Flatten(f.attachments, reqs, materials, stains);
        }
    }
}
