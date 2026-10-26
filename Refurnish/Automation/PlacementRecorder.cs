using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using Refurnish.Inventory;

namespace Refurnish.Automation;

/// <summary>
/// Watch-only test for rebuilding placement. While recording it notes:
///  - every callback a game window fires (window name + values), e.g. clicks in the furnishing list;
///  - changes to the housing state (holding an item, which storeroom slot it came from, moving
///    objects, the game's busy/status flags);
///  - items leaving the storeroom or inventory and appearing in the room.
/// It never sends anything or changes the game.
/// </summary>
public sealed unsafe class PlacementRecorder : IDisposable
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    private const int MaxLines = 600;

    private readonly CallbackTap tap;
    private readonly List<string> lines = new();
    private readonly object gate = new();
    private DateTime startedUtc;
    private string lastState = string.Empty;
    private int lastRoom, lastBag, lastPlaced;

    public bool Recording { get; private set; }
    public string? SetupError => tap.SetupError;
    public int LineCount { get { lock (gate) return lines.Count; } }

    public PlacementRecorder(CallbackTap tap)
    {
        this.tap = tap;
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
        tap.Seen += OnCallback;
        tap.Acquire();
        Plugin.ToastGui.ErrorToast += OnErrorToast;
    }

    public void Stop()
    {
        if (!Recording) return;
        tap.Seen -= OnCallback;
        tap.Release();
        Plugin.ToastGui.ErrorToast -= OnErrorToast;
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
            Add($"ITEMS storeroom {lastRoom}->{room}, inventory {lastBag}->{bag}, placed {lastPlaced}->{placed} | {Aim()}");
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
            sb.Append($" moving={Obj((nint)indoor->MovingHousingObject)}");
            sb.Append($" targeted={Obj((nint)indoor->TargetedHousingObject)}");
            sb.Append($" hovered={Obj((nint)indoor->HoveredHousingObject)}");
            // The preview object that follows the cursor while an item is held.
            sb.Append($" preview={Obj((nint)indoor->FurnitureManager.ObjectManager.ObjectArray.TempObject)}");
            sb.Append($" showStoreroomTab={indoor->ShowStoreroomTab}");
        }

        var flags = Plugin.Condition.AsReadOnlySet().Select(f => f.ToString()).OrderBy(f => f);
        sb.Append($" | flags: {string.Join(",", flags)}");
        return sb.ToString();
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (Recording) Add($"GAME MESSAGE \"{message.TextValue}\" | {Aim()}");
    }

    /// <summary>Position of a housing object, rounded so small cursor wobbles don't flood the log.</summary>
    private static string Obj(nint address)
    {
        if (address == 0) return "none";
        var o = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        static float R(float v) => MathF.Round(v * 2) / 2;
        return $"({R(o->Position.X):0.0},{R(o->Position.Y):0.0},{R(o->Position.Z):0.0})";
    }

    private static string Aim()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var you = me != null ? $"you ({me.Position.X:0.00},{me.Position.Y:0.00},{me.Position.Z:0.00})" : "you ?";
        return Plugin.GameGui.ScreenToWorld(ImGui.GetMousePos(), out var a)
            ? $"{you}, mouse aiming at ({a.X:0.00},{a.Y:0.00},{a.Z:0.00})"
            : $"{you}, mouse not over the room";
    }

    private void OnCallback(CbEvent e)
    {
        if (Recording) Add(e.Display + (e.MenuStrings.Count > 0 ? $" menu=[{string.Join(" | ", e.MenuStrings)}]" : "") + $" | {Aim()}");
    }

    private void Add(string line)
    {
        lock (gate)
        {
            lines.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (lines.Count > MaxLines) lines.RemoveAt(0);
        }
    }

    public void Dispose() => Stop();
}
