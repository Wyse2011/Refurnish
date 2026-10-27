using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace Refurnish.Automation;

/// <summary>
/// The game's housing editor: which layout mode is active, which item is selected, and the game's
/// own "select item" and "place item" functions, which are what clicking an item and setting it
/// down in Rotate mode use.
///
/// Facts used here, from the ReMakePlace plugin's research (https://github.com/RemakePlace/plugin):
/// the byte patterns that locate the two functions, and where the editor's state sits (0x40 into
/// LayoutWorld; mode at 0x0, selected item at 0x18). Everything else comes from FFXIVClientStructs.
/// They may need updating after a game patch; if they can't be found, nothing is called.
/// </summary>
public sealed unsafe class HousingEditor
{
    private const string SelectPattern = "48 85 D2 0F 84 ?? ?? ?? ?? 53 41 56 48 83 EC ?? 48 89 6C 24";
    private const string PlacePattern = "48 89 5C 24 10 48 89 74 24 18 57 48 83 EC 20 4c 8B 41 18 33 FF 0F B6 F2";
    private const int EditorStateOffset = 0x40;
    private const int ModeOffset = 0x0;
    private const int SelectedOffset = 0x18;
    private const int RotateMode = 2;

    private delegate* unmanaged<nint, nint, void> select;
    private delegate* unmanaged<nint, nint, void> place;
    private bool resolved;

    public string? Error { get; private set; }

    /// <summary>Finds the game's select and place functions. False (with Error set) if either can't be found.</summary>
    public bool Resolve()
    {
        if (resolved) return true;
        if (!Plugin.SigScanner.TryScanText(SelectPattern, out var s) || s == 0)
        {
            Error = "Couldn't find the game's \"select furniture\" function (the game may have been patched).";
            return false;
        }
        if (!Plugin.SigScanner.TryScanText(PlacePattern, out var p) || p == 0)
        {
            Error = "Couldn't find the game's \"place furniture\" function (the game may have been patched).";
            return false;
        }
        select = (delegate* unmanaged<nint, nint, void>)s;
        place = (delegate* unmanaged<nint, nint, void>)p;
        resolved = true;
        Error = null;
        return true;
    }

    /// <summary>The editor's state, or 0 if the housing editor isn't open.</summary>
    private static nint State
    {
        get
        {
            var world = LayoutWorld.Instance();
            return world == null ? 0 : *(nint*)((byte*)world + EditorStateOffset);
        }
    }

    public bool InRotateMode
    {
        get
        {
            var state = State;
            return state != 0 && *(int*)(state + ModeOffset) == RotateMode;
        }
    }

    public nint Selected
    {
        get
        {
            var state = State;
            return state == 0 ? 0 : *(nint*)(state + SelectedOffset);
        }
    }

    /// <summary>Selects an item, like clicking it in Rotate mode. Only call when Resolve() succeeded and in Rotate mode.</summary>
    public bool Select(nint item)
    {
        var state = State;
        if (!resolved || state == 0 || item == 0) return false;
        select(state, item);
        return true;
    }

    /// <summary>Sets the selected item down where its position now says. Same conditions as Select.</summary>
    public bool Place(nint item)
    {
        var state = State;
        if (!resolved || state == 0 || item == 0) return false;
        place(state, item);
        return true;
    }
}
