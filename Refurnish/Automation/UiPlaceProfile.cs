using System;
using System.Collections.Generic;
using System.Linq;

namespace Refurnish.Automation;

/// <summary>
/// The clicks the game's furnishing window uses to place an item from the storeroom, copied from
/// you doing it once: pick an item in the list (which opens a menu), choose "Place", or close the
/// menu without choosing. Only numbers are stored, never text or addresses. Tied to the game
/// version, and the menu must read exactly as it did when learned before "Place" is chosen.
/// </summary>
[Serializable]
public sealed class UiPlaceProfile
{
    public string GameVersion { get; set; } = string.Empty;
    public DateTime LearnedUtc { get; set; }

    public string ListAddon { get; set; } = "HousingGoods";
    public CbValue[] SelectValues { get; set; } = Array.Empty<CbValue>();
    public int SelectIndexParam { get; set; } = -1;
    public bool SelectClose { get; set; }

    public string MenuAddon { get; set; } = "ContextMenu";
    public CbValue[] PlaceValues { get; set; } = Array.Empty<CbValue>();
    public bool PlaceClose { get; set; }
    public CbValue[] CancelValues { get; set; } = Array.Empty<CbValue>();
    public bool CancelClose { get; set; }

    /// <summary>The menu's text when "Place" was chosen; must match before choosing again.</summary>
    public List<string> MenuStrings { get; set; } = new();

    public bool IsValid =>
        GameVersion == Plugin.GameVersion && SelectIndexParam >= 0 && SelectIndexParam < SelectValues.Length
        && PlaceValues.Length > 0 && CancelValues.Length > 0
        && SelectValues.All(v => v.Replayable) && PlaceValues.All(v => v.Replayable) && CancelValues.All(v => v.Replayable);

    /// <summary>Which menu entry "Place" was (the second value of the menu click).</summary>
    public int PlaceEntryIndex => PlaceValues.Length > 1 ? PlaceValues[1].AsInt : -1;

    /// <summary>The text of the entry that gets chosen, as it read when learned (e.g. "Place").</summary>
    public string PlaceEntryText => PlaceEntryIndex >= 0 && PlaceEntryIndex < MenuStrings.Count ? Clean(MenuStrings[PlaceEntryIndex]) : string.Empty;

    /// <summary>
    /// The game reuses one menu window and leaves entries from bigger, earlier menus behind, so only
    /// the entry about to be chosen is compared: it must read exactly as it did when learned.
    /// </summary>
    public bool MenuChoiceMatches(List<string> menu, out string found)
    {
        var i = PlaceEntryIndex;
        found = i >= 0 && i < menu.Count ? Clean(menu[i]) : "(missing)";
        return PlaceEntryText.Length > 0 && found == PlaceEntryText;
    }

    // Learned text from older versions may still contain formatting codes; keep only plain characters.
    private static string Clean(string s) => new string(s.Where(c => !char.IsControl(c) && c != '\uFFFD').ToArray()).Trim();

    public CbValue[] SelectFor(int listIndex)
    {
        var v = SelectValues.Select(x => new CbValue(x.Type, x.Raw)).ToArray();
        v[SelectIndexParam] = new CbValue(v[SelectIndexParam].Type, listIndex);
        return v;
    }
}
