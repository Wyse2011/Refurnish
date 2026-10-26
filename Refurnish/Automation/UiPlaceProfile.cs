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

    public CbValue[] SelectFor(int listIndex)
    {
        var v = SelectValues.Select(x => new CbValue(x.Type, x.Raw)).ToArray();
        v[SelectIndexParam] = new CbValue(v[SelectIndexParam].Type, listIndex);
        return v;
    }
}
