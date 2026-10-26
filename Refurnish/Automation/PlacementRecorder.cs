using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Refurnish.Inventory;

namespace Refurnish.Automation;

/// <summary>
/// Watch-only test for rebuilding placement. While recording it notes:
///  - every callback a game window fires (window name + values), e.g. clicks in the furnishing list;
///  - changes to the housing state (holding an item, which storeroom slot it came from, moving
///    objects, the game's busy/status flags);
///  - items leaving the storeroom or inventory and appearing in the room.
/// It never sends anything or changes the game. The window hook passes every call through untouched
/// and is only on while recording.
/// </summary>
public sealed unsafe class PlacementRecorder : IDisposable
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    private const int MaxLines = 600;

    private delegate bool FireCallbackDelegate(AtkUnitBase* self, uint valueCount, AtkValue* values, bool close);

    private readonly Hook<FireCallbackDelegate>? hook;
    private readonly List<string> lines = new();
    private readonly object gate = new();
    private DateTime startedUtc;
    private string lastState = string.Empty;
    private int lastRoom, lastBag, lastPlaced;

    public bool Recording { get; private set; }
    public string? SetupError { get; }
    public int LineCount { get { lock (gate) return lines.Count; } }

    public PlacementRecorder()
    {
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<FireCallbackDelegate>((nint)AtkUnitBase.Addresses.FireCallback.Value, Detour);
        }
        catch (Exception ex)
        {
            SetupError = "Couldn't attach to the game's window callbacks, so recording is unavailable.";
            Plugin.Log.Error(ex, "PlacementRecorder hook failed");
        }
    }

    public void Start()
    {
        lock (gate) lines.Clear();
        lastState = string.Empty;
        (lastRoom, lastBag, lastPlaced) = Counts();
        startedUtc = DateTime.UtcNow;
        Recording = true;
        Add($"Recording started. Game version {Plugin.GameVersion}, Refurnish {Plugin.Version}. "
            + $"Storeroom items {lastRoom}, inventory items {lastBag}, placed {lastPlaced}.");
        hook?.Enable();
    }

    public void Stop()
    {
        if (!Recording) return;
        hook?.Disable();
        Recording = false;
        Add("Recording stopped.");
    }

    public string Text()
    {
        lock (gate) return string.Join("\n", lines);
    }

    public List<string> Tail(int n)
    {
        lock (gate) return lines.Skip(Math.Max(0, lines.Count - n)).ToList();
    }

    /// <summary>Called every framework tick while recording.</summary>
    public void Update()
    {
        if (!Recording) return;
        if (DateTime.UtcNow - startedUtc > MaxDuration) { Add("Stopped automatically after 5 minutes."); Stop(); return; }

        var state = DescribeState();
        if (state != lastState)
        {
            Add("STATE " + state);
            lastState = state;
        }

        var (room, bag, placed) = Counts();
        if (room != lastRoom || bag != lastBag || placed != lastPlaced)
        {
            Add($"ITEMS storeroom {lastRoom}->{room}, inventory {lastBag}->{bag}, placed {lastPlaced}->{placed}");
            (lastRoom, lastBag, lastPlaced) = (room, bag, placed);
        }
    }

    private static (int Room, int Bag, int Placed) Counts() => (
        LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out _).Count,
        LiveInventory.ReadOccupied(LiveInventory.PlayerInventory, out _).Count,
        LiveInventory.ReadOccupied(LiveInventory.InteriorPlaced, out _).Count);

    private static string DescribeState()
    {
        var sb = new StringBuilder();
        var hm = HousingManager.Instance();
        var indoor = hm != null ? hm->IndoorTerritory : null;
        if (indoor == null)
        {
            sb.Append("not indoors");
        }
        else
        {
            var temp = indoor->TemporaryObject;
            var held = temp.InventoryItem;
            sb.Append($"holding={(held != null ? $"item {held->GetBaseItemId()} from container {(int)held->GetInventoryType()} slot {held->GetSlot()}" : "nothing")}");
            sb.Append($" tempKind={temp.Data.Kind} pad={temp.Data.IsPadModeEnabled}");
            sb.Append($" storeroomSel={(int)indoor->StoreroomItemInventoryType}/{indoor->StoreroomItemInventorySlot}");
            var p = indoor->StoreroomItemPlacePosition;
            sb.Append($" storeroomPos=({p.X:0.00},{p.Y:0.00},{p.Z:0.00}) rot={indoor->StoreroomItemPlaceRotation:0.00}");
            sb.Append($" moving={(indoor->MovingHousingObject != null ? "yes" : "no")}");
            sb.Append($" targeted={(indoor->TargetedHousingObject != null ? "yes" : "no")}");
            sb.Append($" showStoreroomTab={indoor->ShowStoreroomTab}");
        }

        var flags = Plugin.Condition.AsReadOnlySet().Select(f => f.ToString()).OrderBy(f => f);
        sb.Append($" | flags: {string.Join(",", flags)}");
        return sb.ToString();
    }

    private bool Detour(AtkUnitBase* self, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            if (Recording && self != null)
            {
                var sb = new StringBuilder();
                sb.Append($"CALLBACK {self->NameString} close={close} values=[");
                for (var i = 0; i < valueCount && i < 16; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Format(values[i]));
                }
                if (valueCount > 16) sb.Append(", ...");
                sb.Append(']');
                Add(sb.ToString());
            }
        }
        catch
        {
            // Never let recording break the game's own call.
        }
        return hook!.Original(self, valueCount, values, close);
    }

    private static string Format(AtkValue v)
    {
        var type = (AtkValueType)((int)v.Type & 0xF);
        return type switch
        {
            AtkValueType.Bool => $"bool:{v.Byte != 0}",
            AtkValueType.Int => $"int:{v.Int}",
            AtkValueType.UInt => $"uint:{v.UInt}",
            AtkValueType.Int64 => $"long:{v.Int64}",
            AtkValueType.UInt64 => $"ulong:{v.UInt64}",
            AtkValueType.Float => $"float:{v.Float:0.###}",
            AtkValueType.String or AtkValueType.ConstString =>
                v.String.Value != null ? $"str:\"{Marshal.PtrToStringUTF8((nint)v.String.Value)}\"" : "str:null",
            _ => $"type{(int)v.Type}",
        };
    }

    private void Add(string line)
    {
        lock (gate)
        {
            lines.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (lines.Count > MaxLines) lines.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        Stop();
        hook?.Dispose();
    }
}
