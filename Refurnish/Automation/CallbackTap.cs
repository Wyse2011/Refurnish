using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Refurnish.Automation;

/// <summary>One value a game window passed along when something was clicked. Numbers only.</summary>
[Serializable]
public sealed class CbValue
{
    public int Type { get; set; }
    public long Raw { get; set; }

    public CbValue() { }
    public CbValue(int type, long raw) { Type = type; Raw = raw; }

    public int BaseType => Type & 0xF;
    public int AsInt => (int)Raw;

    /// <summary>Only plain numbers, booleans and empty values can be repeated safely (no text, no addresses).</summary>
    public bool Replayable => (Type & 0x20) == 0 && BaseType is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7;

    public override string ToString() => BaseType switch
    {
        2 => $"bool:{Raw != 0}",
        3 => $"int:{AsInt}",
        5 => $"uint:{(uint)Raw}",
        0 => "empty",
        _ => $"type{Type}",
    };
}

public sealed record CbEvent(DateTime Utc, string Addon, CbValue[] Values, bool Close, List<string> MenuStrings,
    int SelContainer, int SelSlot, string Display);

/// <summary>
/// Watches the callbacks game windows fire when you click in them, and can fire the same kind of
/// callback itself, which is exactly what clicking the button does. Every call passes through
/// untouched; the watcher is only on while something needs it.
/// </summary>
public sealed unsafe class CallbackTap : IDisposable
{
    private delegate bool FireCallbackDelegate(AtkUnitBase* self, uint valueCount, AtkValue* values, bool close);

    private readonly Hook<FireCallbackDelegate>? hook;
    private int users;

    /// <summary>True while Refurnish itself is clicking, so recordings can tell its clicks from yours.</summary>
    public static bool RefurnishClicking { get; private set; }

    public event Action<CbEvent>? Seen;
    public string? SetupError { get; }

    public CallbackTap()
    {
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<FireCallbackDelegate>((nint)AtkUnitBase.Addresses.FireCallback.Value, Detour);
        }
        catch (Exception ex)
        {
            SetupError = "Couldn't attach to the game's window callbacks.";
            Plugin.Log.Error(ex, "CallbackTap hook failed");
        }
    }

    public void Acquire() { if (users++ == 0) hook?.Enable(); }
    public void Release() { if (users > 0 && --users == 0) hook?.Disable(); }

    private bool Detour(AtkUnitBase* self, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            if (self != null && Seen != null)
            {
                var vals = new CbValue[valueCount];
                for (var i = 0; i < valueCount; i++)
                    vals[i] = new CbValue((int)values[i].Type, values[i].Int64);

                var name = self->NameString;
                var menu = name == "ContextMenu" ? ReadStrings(self) : new List<string>();
                var (c, s) = StoreroomSelection();
                var who = RefurnishClicking ? "[Refurnish] " : "[you] ";
                var display = $"{who}CALLBACK {name} close={close} values=[{string.Join(", ", vals.Select((v, i) => Describe(values[i], v)))}]";
                Seen.Invoke(new CbEvent(DateTime.UtcNow, name, vals, close, menu, c, s, display));
            }
        }
        catch
        {
            // Never let watching break the game's own call.
        }
        return hook!.Original(self, valueCount, values, close);
    }

    private static string Describe(AtkValue raw, CbValue v)
    {
        if (v.BaseType is 8 or 10 && raw.String.Value != null)
            return $"str:\"{Marshal.PtrToStringUTF8((nint)raw.String.Value)}\"";
        return v.ToString();
    }

    // ---- Reading game windows -----------------------------------------------------------------

    public static AtkUnitBase* Addon(string name)
    {
        var a = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name);
        return a != null && a->IsVisible ? a : null;
    }

    public static bool IsVisible(string name) => Addon(name) != null;

    /// <summary>All text values a window currently holds, in order (for a menu, its entries).</summary>
    public static List<string> ReadStrings(AtkUnitBase* a)
    {
        var list = new List<string>();
        if (a == null || a->AtkValues == null) return list;
        for (var i = 0; i < a->AtkValuesCount; i++)
        {
            var v = a->AtkValues[i];
            var t = (int)v.Type & 0xF;
            if (t is 8 or 10 && v.String.Value != null)
                list.Add(Text(v.String.Value));
        }
        return list;
    }

    /// <summary>Game text without its formatting codes (icons, colours), so it reads as plain words.</summary>
    private static string Text(byte* ptr)
    {
        try
        {
            return Dalamud.Game.Text.SeStringHandling.SeString.Parse(ptr).TextValue.Trim();
        }
        catch
        {
            return Marshal.PtrToStringUTF8((nint)ptr) ?? string.Empty;
        }
    }

    public static List<string>? OpenMenuStrings()
    {
        var a = Addon("ContextMenu");
        return a == null ? null : ReadStrings(a);
    }

    /// <summary>Which storeroom slot the furnishing window last selected.</summary>
    public static (int Container, int Slot) StoreroomSelection()
    {
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return (0, -1);
        return ((int)hm->IndoorTerritory->StoreroomItemInventoryType, hm->IndoorTerritory->StoreroomItemInventorySlot);
    }

    public static bool StoreroomTabShown()
    {
        var hm = HousingManager.Instance();
        return hm != null && hm->IndoorTerritory != null && hm->IndoorTerritory->ShowStoreroomTab;
    }

    // ---- Clicking ------------------------------------------------------------------------------

    /// <summary>
    /// Fires a callback on a visible window, the same thing clicking in it does. Only plain numeric
    /// values are allowed. Framework thread only. False if the window isn't open.
    /// </summary>
    public static bool Fire(string addonName, CbValue[] values, bool close)
    {
        if (values.Any(v => !v.Replayable)) return false;
        var a = Addon(addonName);
        if (a == null) return false;

        var size = sizeof(AtkValue) * Math.Max(1, values.Length);
        var ptr = (AtkValue*)Marshal.AllocHGlobal(size);
        try
        {
            new Span<byte>(ptr, size).Clear();
            for (var i = 0; i < values.Length; i++)
            {
                ptr[i].Type = (AtkValueType)values[i].Type;
                ptr[i].Int64 = values[i].Raw;
            }
            RefurnishClicking = true;
            try
            {
                a->FireCallback((uint)values.Length, ptr, close);
            }
            finally
            {
                RefurnishClicking = false;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)ptr);
        }
    }

    public void Dispose() => hook?.Dispose();
}
