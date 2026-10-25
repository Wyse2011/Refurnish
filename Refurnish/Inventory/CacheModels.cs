using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Refurnish.Inventory;

public enum SourceKind
{
    PlacedInHouse,
    HouseStoreroom,
    Inventory,
    Saddlebag,
    Retainer,
    FreeCompanyChest,
}

public sealed class CachedItem
{
    public uint ItemId { get; set; }
    public byte Stain { get; set; }
    public int Quantity { get; set; }
    public int Container { get; set; }
    public int Slot { get; set; }
}

/// <summary>
/// The last known contents of one place items can live: your inventory, one retainer, one FC
/// chest compartment, or one house's storeroom or placed furniture.
/// </summary>
public sealed class CachedContainer
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SourceKind Kind { get; set; }

    /// <summary>For house containers, which house. 0 otherwise.</summary>
    public ulong HouseId { get; set; }

    public DateTime LastSeenUtc { get; set; }
    public int TotalSlots { get; set; }
    public List<CachedItem> Items { get; set; } = new();

    [JsonIgnore]
    public int FreeSlots => Math.Max(0, TotalSlots - Items.Count);
}

public sealed class KnownHouse
{
    public ulong HouseId { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime LastVisitedUtc { get; set; }
}

/// <summary>Everything Refurnish has seen for one character. Saved per character.</summary>
public sealed class CharacterCache
{
    public ulong ContentId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public Dictionary<string, CachedContainer> Containers { get; set; } = new();
    public Dictionary<ulong, KnownHouse> Houses { get; set; } = new();

    /// <summary>All retainers on this character (ID to name), whether or not they've been opened yet.</summary>
    public Dictionary<ulong, string> Retainers { get; set; } = new();
}
