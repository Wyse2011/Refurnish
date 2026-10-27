using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Refurnish.Automation;

/// <summary>
/// The same "place anywhere" switch the MakePlace plugin uses: three single-byte flags in the game's
/// code that, when set to 1, turn off the check that refuses floating, sunk or overlapping
/// furniture ("Unable to place item."). Refurnish only turns it on while it is placing with this
/// option ticked, and always puts the original values back afterwards.
///
/// Safety: all three locations must be found, and each must currently hold 0 or 1 (the only values
/// this switch ever has). Otherwise nothing is changed and the option reports why.
/// </summary>
public sealed class PlaceAnywhere : IDisposable
{
    // Patterns and offsets from the MakePlace plugin (Memory.cs). They may need updating after a game patch.
    private static readonly (string Name, string Pattern, int Offset)[] Spots =
    {
        ("place anywhere", "C6 ?? ?? ?? 00 00 00 8B FE 48 89", 6),
        ("wall anywhere", "48 85 C0 74 ?? C6 87 ?? ?? 00 00 00", 11),
        ("wall-mount anywhere", "c6 87 83 01 00 00 00 48 83 c4 ??", 6),
    };

    private readonly List<(string Name, nint Address, byte Original)> applied = new();
    private List<(string Name, nint Address)>? found;

    public bool Active => applied.Count > 0;
    public string? Error { get; private set; }

    /// <summary>Finds the three switches. Returns false (with Error set) if any can't be found safely.</summary>
    private bool Find()
    {
        if (found != null) return true;
        var list = new List<(string, nint)>();
        foreach (var (name, pattern, offset) in Spots)
        {
            if (!Plugin.SigScanner.TryScanText(pattern, out var addr) || addr == 0)
            {
                Error = $"Couldn't find the game's \"{name}\" switch (the game may have been patched). Place anywhere is unavailable.";
                return false;
            }
            list.Add((name, addr + offset));
        }
        found = list;
        return true;
    }

    /// <summary>Turns place anywhere on. Returns false (and changes nothing) if it can't be done safely.</summary>
    public bool Enable()
    {
        if (Active) return true;
        Error = null;
        if (!Find()) return false;

        // Check every location before changing any of them.
        foreach (var (name, address) in found!)
        {
            var current = Marshal.ReadByte(address);
            if (current is not (0 or 1))
            {
                Error = $"The game's \"{name}\" switch held an unexpected value ({current}), so it wasn't changed. "
                        + "The game may have been patched. Place anywhere is unavailable.";
                return false;
            }
        }

        foreach (var (name, address) in found!)
        {
            var original = Marshal.ReadByte(address);
            if (!Write(address, 1))
            {
                Disable(); // put back anything already changed
                Error = $"Couldn't switch on \"{name}\". Nothing was left changed.";
                return false;
            }
            applied.Add((name, address, original));
        }
        Plugin.Log.Information("Refurnish: place anywhere on.");
        return true;
    }

    /// <summary>Puts every switch back exactly as it was (so if MakePlace had it on, it stays on).</summary>
    public void Disable()
    {
        if (applied.Count == 0) return;
        foreach (var (name, address, original) in applied)
        {
            if (!Write(address, original))
                Plugin.Log.Error($"Refurnish: couldn't restore \"{name}\". Restarting the game clears it.");
        }
        applied.Clear();
        Plugin.Log.Information("Refurnish: place anywhere restored.");
    }

    private static bool Write(nint address, byte value)
    {
        if (!VirtualProtect(address, 1, PageExecuteReadWrite, out var old)) return false;
        try
        {
            Marshal.WriteByte(address, value);
        }
        finally
        {
            VirtualProtect(address, 1, old, out _);
        }
        return true;
    }

    public void Dispose() => Disable();

    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
}
