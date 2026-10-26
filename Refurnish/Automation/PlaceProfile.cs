using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;

namespace Refurnish.Automation;

public enum PlaceSource
{
    Storeroom,
    Inventory,
}

/// <summary>
/// A copy of the exact message the game sent when you placed one item by hand, with the item's
/// container, slot, position and rotation marked so they can be swapped. Only used in the room
/// and game version it was learned in (message IDs change every patch).
/// </summary>
[Serializable]
public sealed class PlaceProfile
{
    public PlaceSource Source { get; set; }
    public ulong HouseId { get; set; }
    public string GameVersion { get; set; } = string.Empty;
    public DateTime LearnedUtc { get; set; }

    public byte[] Template { get; set; } = Array.Empty<byte>();
    public uint A3 { get; set; }
    public uint A4 { get; set; }
    public bool A5 { get; set; }

    /// <summary>Offset of the 2-byte container number; the slot, position (3 floats) and rotation follow it.</summary>
    public int ContainerOffset { get; set; }

    /// <summary>4-byte fields that held the current time when learned; refreshed when sending.</summary>
    public int[] TimestampOffsets { get; set; } = Array.Empty<int>();

    /// <summary>8-byte fields that pointed into the game's code; stored relative and re-based.</summary>
    public int[] ModulePointerOffsets { get; set; } = Array.Empty<int>();

    /// <summary>If the copy held other memory addresses, it's only trusted until the game restarts.</summary>
    public bool SessionOnly { get; set; }
    public long SessionId { get; set; }

    public static long CurrentSessionId => Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    public bool IsValidFor(ulong houseId, string gameVersion) =>
        HouseId == houseId && GameVersion == gameVersion && Template.Length > ContainerOffset + 20
        && (!SessionOnly || SessionId == CurrentSessionId);

    public byte[] Build(int container, int slot, Vector3 position, float rotation)
    {
        var b = (byte[])Template.Clone();
        var o = ContainerOffset;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)container);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 2), (short)slot);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 4), position.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 8), position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 12), position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 16), rotation);

        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var t in TimestampOffsets)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(t), now);

        if (ModulePointerOffsets.Length > 0)
        {
            var baseAddr = (ulong)Process.GetCurrentProcess().MainModule!.BaseAddress;
            foreach (var p in ModulePointerOffsets)
            {
                var rel = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), baseAddr + rel);
            }
        }
        return b;
    }
}
