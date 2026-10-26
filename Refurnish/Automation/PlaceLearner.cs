using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Refurnish.Inventory;

namespace Refurnish.Automation;

/// <summary>
/// One-time setup per room (and after each patch). While active, you place one item from your
/// storeroom or inventory using the game's own menu, turned a little so it isn't facing straight
/// ahead. Refurnish then reads where the game actually put it (position and rotation) and looks
/// for the outgoing message containing that exact container, slot, position and rotation.
/// Nothing is assumed about where those sit in the message; anything ambiguous is refused.
/// </summary>
public sealed class PlaceLearner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private const float PositionTolerance = 0.01f;
    private const float RotationTolerance = 0.01f;
    private const float MinUsefulRotation = 0.1f;

    private readonly Plugin plugin;
    private Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> roomBase = new();
    private Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> bagBase = new();
    private Dictionary<uint, int> placedCounts = new();
    private Dictionary<int, LiveFurniture.Entry> furnitureBase = new();
    private DateTime startedUtc;
    private ulong houseId;
    private (int Container, int Slot, uint ItemId, PlaceSource Source)? pending;
    private DateTime pendingSinceUtc;

    public bool Active { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public bool LastSucceeded { get; private set; }
    public List<CapturedPacket> LastCapture { get; private set; } = new();

    public PlaceLearner(Plugin plugin) => this.plugin = plugin;

    public bool Start()
    {
        if (!PlaceExecutor.DirectPlacingEnabled) { Fail("Placing is switched off in this version."); return false; }
        if (plugin.Packets.SetupError != null) { Fail(plugin.Packets.SetupError); return false; }
        if (plugin.Executor.Running || plugin.Placer.Running || plugin.Learner.Active)
        {
            Fail("Wait for other Refurnish actions to finish first.");
            return false;
        }

        houseId = plugin.Scanner.CurrentHouseId;
        if (houseId == 0) { Fail("Go inside the room you want to set up (you need decorating permission)."); return false; }

        var furniture = LiveFurniture.Read();
        if (furniture == null) { Fail("Couldn't read this room's furniture. Try again in a moment."); return false; }

        roomBase = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out _);
        bagBase = LiveInventory.ReadOccupied(LiveInventory.PlayerInventory, out var bagLoaded);
        if (!bagLoaded) { Fail("Couldn't read your inventory. Try again in a moment."); return false; }
        placedCounts = CountPlaced();
        furnitureBase = furniture;

        startedUtc = DateTime.UtcNow;
        pending = null;
        plugin.Packets.Acquire();
        plugin.Packets.StartCapture();
        Active = true;
        LastSucceeded = false;
        Status = "Watching. Place one item from your storeroom or inventory with the game's menu. "
                 + "Turn it a little before setting it down (any angle except straight ahead).";
        return true;
    }

    public void Cancel()
    {
        if (!Active) return;
        plugin.Packets.StopCapture();
        plugin.Packets.Release();
        Active = false;
        Status = "Cancelled.";
    }

    public void Update()
    {
        if (!Active) return;
        if (plugin.Scanner.CurrentHouseId != houseId) { Fail("You left the room, so learning stopped."); return; }
        if (DateTime.UtcNow - startedUtc > Timeout) { Fail("Nothing was placed within 3 minutes, so learning stopped."); return; }

        if (pending == null)
        {
            var room = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out _);
            var bag = LiveInventory.ReadOccupied(LiveInventory.PlayerInventory, out _);
            var placed = CountPlaced();

            var lost = Lost(roomBase, room).Select(l => (l.Key, l.ItemId, Source: PlaceSource.Storeroom))
                .Concat(Lost(bagBase, bag).Select(l => (l.Key, l.ItemId, Source: PlaceSource.Inventory)))
                .Where(l => placed.GetValueOrDefault(l.ItemId) > placedCounts.GetValueOrDefault(l.ItemId))
                .ToList();

            if (lost.Count == 0)
            {
                // Unrelated changes (loot, moving items, rearranging furniture): take the new state.
                roomBase = room;
                bagBase = bag;
                placedCounts = placed;
                furnitureBase = LiveFurniture.Read() ?? furnitureBase;
                return;
            }
            if (lost.Count > 1) { Fail("More than one item was placed at once. Place just one and try again."); return; }

            pending = (lost[0].Key.Container, lost[0].Key.Slot, lost[0].ItemId, lost[0].Source);
            pendingSinceUtc = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - pendingSinceUtc < TimeSpan.FromSeconds(1.5)) return;
        Finish(pending.Value.Container, pending.Value.Slot, pending.Value.Source);
    }

    private void Finish(int container, int slot, PlaceSource source)
    {
        LastCapture = plugin.Packets.StopCapture();
        plugin.Packets.Release();
        Active = false;

        // Where did the game really put it?
        var now = LiveFurniture.Read();
        var added = now == null ? new List<LiveFurniture.Entry>() : LiveFurniture.New(furnitureBase, now);
        if (added.Count != 1)
        {
            Fail(added.Count == 0
                ? "The item was placed, but Refurnish couldn't find it in the room's furniture list. Placing can't be enabled safely."
                : "More than one new furnishing appeared, so Refurnish can't tell which one was placed. Try again.");
            return;
        }
        var actual = added[0];
        if (MathF.Abs(actual.Rotation) < MinUsefulRotation || LiveFurniture.AngleDiff(actual.Rotation, 0) < MinUsefulRotation)
        {
            Fail("The item was placed facing straight ahead, so its rotation can't be told apart from empty space in the message. "
                 + "Store it again, press Learn, and turn the item a little before setting it down.");
            return;
        }

        // Find messages holding all four values: container+slot, the exact position, the exact rotation.
        var matches = new List<(CapturedPacket Packet, int Container, int Position, int Rotation)>();
        foreach (var p in LastCapture)
        {
            var b = p.Bytes;
            var containerOffsets = new List<int>();
            for (var o = 0; o + 4 <= b.Length; o += 2)
                if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) == container
                    && BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 2)) == slot)
                    containerOffsets.Add(o);
            if (containerOffsets.Count == 0) continue;

            var positionOffsets = new List<int>();
            for (var o = 0; o + 12 <= b.Length; o += 4)
            {
                var v = new Vector3(Float(b, o), Float(b, o + 4), Float(b, o + 8));
                if (Vector3.Distance(v, actual.Position) < PositionTolerance) positionOffsets.Add(o);
            }

            var rotationOffsets = new List<int>();
            for (var o = 0; o + 4 <= b.Length; o += 4)
            {
                var r = Float(b, o);
                if (float.IsFinite(r) && LiveFurniture.AngleDiff(r, actual.Rotation) < RotationTolerance) rotationOffsets.Add(o);
            }

            foreach (var c in containerOffsets)
            foreach (var pos in positionOffsets)
            foreach (var rot in rotationOffsets)
            {
                if (Overlaps(c, 4, pos, 12) || Overlaps(c, 4, rot, 4) || Overlaps(pos, 12, rot, 4)) continue;
                matches.Add((p, c, pos, rot));
            }
        }

        var kinds = matches.Select(m => (m.Container, m.Position, m.Rotation, m.Packet.A3, m.Packet.A4, m.Packet.A5)).Distinct().ToList();
        if (kinds.Count == 0)
        {
            Fail("No message contained this item's slot together with where it actually landed. Placing can't be enabled safely. "
                 + "Copy the capture log below and send it over.");
            return;
        }
        if (kinds.Count > 1)
        {
            Fail("More than one message could have been the placement, so Refurnish won't guess. "
                 + "Try Learn again with a different item and angle; if it keeps happening, send the capture log.");
            return;
        }

        var m0 = matches[0];
        var profile = BuildProfile(m0.Packet, m0.Container, m0.Position, m0.Rotation, source);

        plugin.Configuration.PlaceProfiles.RemoveAll(pp => pp.HouseId == houseId && pp.Source == source);
        plugin.Configuration.PlaceProfiles.Add(profile);
        plugin.Configuration.Save();

        LastSucceeded = true;
        var from = source == PlaceSource.Storeroom ? "the storeroom" : "your inventory";
        Status = profile.SessionOnly
            ? $"Learned placing from {from}. (Only trusted until you restart the game.)"
            : $"Learned placing from {from}.";
    }

    private PlaceProfile BuildProfile(CapturedPacket packet, int containerOffset, int positionOffset, int rotationOffset, PlaceSource source)
    {
        var bytes = (byte[])packet.Bytes.Clone();
        bool Protected(int o, int len) =>
            Overlaps(o, len, containerOffset, 4) || Overlaps(o, len, positionOffset, 12) || Overlaps(o, len, rotationOffset, 4);

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestamps = new List<int>();
        for (var o = 0; o + 4 <= bytes.Length; o += 4)
        {
            if (Protected(o, 4)) continue;
            var v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o));
            if (Math.Abs((long)v - nowSec) < 300) timestamps.Add(o);
        }

        var module = Process.GetCurrentProcess().MainModule!;
        var baseAddr = (ulong)module.BaseAddress;
        var end = baseAddr + (ulong)module.ModuleMemorySize;
        var modulePtrs = new List<int>();
        var sessionOnly = false;
        for (var o = 0; o + 8 <= bytes.Length; o += 8)
        {
            if (Protected(o, 8)) continue;
            var v = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(o));
            if (v >= baseAddr && v < end)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(o), v - baseAddr);
                modulePtrs.Add(o);
            }
            else if ((v >> 48) == 0 && v >= 0x100_0000_0000UL && (v & 7) == 0)
            {
                sessionOnly = true;
            }
        }

        return new PlaceProfile
        {
            FormatVersion = PlaceProfile.CurrentFormat,
            Source = source,
            HouseId = houseId,
            GameVersion = Plugin.GameVersion,
            LearnedUtc = DateTime.UtcNow,
            Template = bytes,
            A3 = packet.A3,
            A4 = packet.A4,
            A5 = packet.A5,
            ContainerOffset = containerOffset,
            PositionOffset = positionOffset,
            RotationOffset = rotationOffset,
            TimestampOffsets = timestamps.ToArray(),
            ModulePointerOffsets = modulePtrs.ToArray(),
            SessionOnly = sessionOnly,
            SessionId = PlaceProfile.CurrentSessionId,
        };
    }

    private static float Float(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));

    private static bool Overlaps(int a, int aLen, int b, int bLen) => a < b + bLen && b < a + aLen;

    private static IEnumerable<((int Container, int Slot) Key, uint ItemId)> Lost(
        Dictionary<(int, int), (uint ItemId, byte Stain)> before, Dictionary<(int, int), (uint ItemId, byte Stain)> now) =>
        before.Where(kv => !now.TryGetValue(kv.Key, out var v) || v.ItemId != kv.Value.ItemId)
              .Select(kv => (kv.Key, kv.Value.ItemId));

    private static Dictionary<uint, int> CountPlaced() =>
        LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out _)
            .GroupBy(kv => kv.Value.ItemId).ToDictionary(g => g.Key, g => g.Count());

    private void Fail(string message)
    {
        if (Active)
        {
            LastCapture = plugin.Packets.StopCapture();
            plugin.Packets.Release();
        }
        Active = false;
        LastSucceeded = false;
        Status = message;
    }
}
