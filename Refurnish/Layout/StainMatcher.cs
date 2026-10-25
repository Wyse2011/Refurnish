using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace Refurnish.Layout;

/// <summary>
/// MakePlace stores dyes as a color ("RRGGBBAA") rather than a dye ID. This maps the color back to
/// the closest dye in the game's Stain sheet. Colors exported from the game match exactly.
/// </summary>
public sealed class StainMatcher
{
    private readonly List<(byte Id, int R, int G, int B)> stains = new();
    private readonly Dictionary<byte, string> names = new();

    public StainMatcher()
    {
        foreach (var s in Plugin.DataManager.GetExcelSheet<Stain>())
        {
            if (s.RowId == 0 || s.RowId > byte.MaxValue) continue;
            var name = s.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var c = s.Color;
            stains.Add(((byte)s.RowId, (int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF)));
            names[(byte)s.RowId] = name;
        }
    }

    public byte FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 6) return 0;
        try
        {
            var r = Convert.ToInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToInt32(hex.Substring(4, 2), 16);

            byte best = 0;
            var bestDist = int.MaxValue;
            foreach (var s in stains)
            {
                var d = (s.R - r) * (s.R - r) + (s.G - g) * (s.G - g) + (s.B - b) * (s.B - b);
                if (d < bestDist) { bestDist = d; best = s.Id; }
            }
            return best;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    public string Name(byte stain) => stain == 0 ? "Undyed" : names.GetValueOrDefault(stain, $"Dye #{stain}");
}
