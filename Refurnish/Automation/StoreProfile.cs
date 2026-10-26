using System;

namespace Refurnish.Automation;

public enum StoreDestination
{
    Storeroom,
    Inventory,
}

/// <summary>
/// The exact request the game sent when you stored one item by hand, with the container and slot
/// marked so they can be swapped for another item's. Refurnish only ever sends requests built
/// from one of these, and only in the house and game version it was learned in.
/// </summary>
[Serializable]
public sealed class StoreProfile
{
    public StoreDestination Destination { get; set; }
    public ulong HouseId { get; set; }
    public string GameVersion { get; set; } = string.Empty;
    public DateTime LearnedUtc { get; set; }

    public int CommandId { get; set; }

    /// <summary>The four parameters exactly as observed.</summary>
    public int[] Observed { get; set; } = new int[4];

    /// <summary>Which parameter held the container number.</summary>
    public int ContainerParam { get; set; } = -1;

    /// <summary>Which parameter held the slot (in its low 16 bits).</summary>
    public int SlotParam { get; set; } = -1;

    /// <summary>Whatever else was packed alongside the slot (such as the storeroom flag).</summary>
    public int SlotExtraBits { get; set; }

    public bool IsValidFor(ulong houseId, string gameVersion) =>
        HouseId == houseId && GameVersion == gameVersion && ContainerParam >= 0 && SlotParam >= 0;

    public int[] Build(int container, int slot)
    {
        var p = (int[])Observed.Clone();
        p[ContainerParam] = container;
        p[SlotParam] = SlotExtraBits | (slot & 0xFFFF);
        return p;
    }
}
