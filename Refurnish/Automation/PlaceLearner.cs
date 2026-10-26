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
/// storeroom or inventory using the game's own menu. Refurnish sees which slot it came from,
/// confirms it appeared among the placed furniture, and finds the outgoing message that names
/// exactly that container and slot followed by a sensible position. Anything ambiguous is refused.
/// </summary>
public sealed class PlaceLearner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly Plugin plugin;
    private Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> roomBase = new();
    private Dictionary<(int Container, int Slot), (uint ItemId, byte Stain)> bagBase = new();
    private Dictionary<uint, int> placedCounts = new();
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
        if (plugin.Packets.SetupError != null) { Fail(plugin.Packets.SetupError); return false; }
        if (plugin.Executor.Running || plugin.Placer.Running || plugin.Learner.Active)
        {
            Fail("Wait for other Refurnish actions to finish first.");
            return false;
        }

        houseId = plugin.Scanner.CurrentHouseId;
        if (houseId == 0) { Fail("Go inside the room you want to set up (you need decorating permission)."); return false; }

        roomBase = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out _);
        bagBase = LiveInventory.ReadOccupied(LiveInventory.PlayerInventory, out var bagLoaded);
        if (!bagLoaded) { Fail("Couldn't read your inventory. Try again in a moment."); return false; }
        placedCounts = CountPlaced();

        startedUtc = DateTime.UtcNow;
        pending = null;
        plugin.Packets.Acquire();
        plugin.Packets.StartCapture();
        Active = true;
        LastSucceeded = false;
        Status = "Watching. Now place one item from your storeroom or inventory using the game's own menu.";
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
                // Unrelated changes (picking up loot, moving items around): just take the new state.
                roomBase = room;
                bagBase = bag;
                placedCounts = placed;
                return;
            }
            if (lost.Count > 1) { Fail("More than one item was placed at once. Place just one and try again."); return; }

            pending = (lost[0].Key.Container, lost[0].Key.Slot, lost[0].ItemId, lost[0].Source);
            pendingSinceUtc = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - pendingSinceUtc < TimeSpan.FromSeconds(1)) return;
        Finish(pending.Value.Container, pending.Value.Slot, pending.Value.Source);
    }

    private void Finish(int container, int slot, PlaceSource source)
    {
        LastCapture = plugin.Packets.StopCapture();
        plugin.Packets.Release();
        Active = false;

        var player = Plugin.ObjectTable.LocalPlayer?.Position;
        var found = new List<(CapturedPacket Packet, int Offset)>();
        foreach (var p in LastCapture)
        {
            var b = p.Bytes;
            for (var o = 0; o + 20 <= b.Length; o += 2)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) != container) continue;
                if (BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 2)) != slot) continue;

                var pos = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 8)),
                    BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 12)));
                var rot = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 16));
                if (!Plausible(pos, rot, player)) continue;

                found.Add((p, o));
            }
        }

        // The same message may be seen more than once; what matters is that only one kind matched.
        var kinds = found.Select(f => (f.Offset, f.Packet.A3, f.Packet.A4, f.Packet.A5)).Distinct().ToList();
        if (kinds.Count == 0)
        {
            Fail("The item was placed, but no matching message was seen. Placing can't be enabled safely. "
                 + "Copy the capture log below and send it over.");
            return;
        }
        if (kinds.Count > 1)
        {
            Fail("More than one message could have been the placement, so Refurnish won't guess. "
                 + "Try Learn again with a different item; if it keeps happening, send the capture log.");
            return;
        }

        var (packet, offset) = found[0];
        var profile = BuildProfile(packet, offset, source);

        plugin.Configuration.PlaceProfiles.RemoveAll(pp => pp.HouseId == houseId && pp.Source == source);
        plugin.Configuration.PlaceProfiles.Add(profile);
        plugin.Configuration.Save();

        LastSucceeded = true;
        var from = source == PlaceSource.Storeroom ? "the storeroom" : "your inventory";
        Status = profile.SessionOnly
            ? $"Learned placing from {from}. (This copy is only trusted until you restart the game.)"
            : $"Learned placing from {from}.";
    }

    private PlaceProfile BuildProfile(CapturedPacket packet, int offset, PlaceSource source)
    {
        var bytes = (byte[])packet.Bytes.Clone();
        var protectedRange = (Start: offset, End: offset + 20);

        // Fields that look like the current time get refreshed on each send.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestamps = new List<int>();
        for (var o = 0; o + 4 <= bytes.Length; o += 4)
        {
            if (o + 4 > protectedRange.Start && o < protectedRange.End) continue;
            var v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o));
            if (Math.Abs((long)v - now) < 300) timestamps.Add(o);
        }

        // Addresses into the game's code get stored relative so they survive a restart;
        // any other address means the copy is only good for this session.
        var module = Process.GetCurrentProcess().MainModule!;
        var baseAddr = (ulong)module.BaseAddress;
        var end = baseAddr + (ulong)module.ModuleMemorySize;
        var modulePtrs = new List<int>();
        var sessionOnly = false;
        for (var o = 0; o + 8 <= bytes.Length; o += 8)
        {
            if (o + 8 > protectedRange.Start && o < protectedRange.End) continue;
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
            Source = source,
            HouseId = houseId,
            GameVersion = Plugin.GameVersion,
            LearnedUtc = DateTime.UtcNow,
            Template = bytes,
            A3 = packet.A3,
            A4 = packet.A4,
            A5 = packet.A5,
            ContainerOffset = offset,
            TimestampOffsets = timestamps.ToArray(),
            ModulePointerOffsets = modulePtrs.ToArray(),
            SessionOnly = sessionOnly,
            SessionId = PlaceProfile.CurrentSessionId,
        };
    }

    private static bool Plausible(Vector3 pos, float rot, Vector3? player)
    {
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z) || !float.IsFinite(rot)) return false;
        if (Math.Abs(pos.X) > 500 || Math.Abs(pos.Y) > 500 || Math.Abs(pos.Z) > 500 || Math.Abs(rot) > 10) return false;
        if (pos == Vector3.Zero) return false;
        return player == null || Vector3.Distance(pos, player.Value) < 80;
    }

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
