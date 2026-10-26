using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Refurnish.Automation;

public readonly record struct CapturedCommand(DateTime Utc, int Command, int P1, int P2, int P3, int P4)
{
    public int Param(int i) => i switch { 0 => P1, 1 => P2, 2 => P3, _ => P4 };
    public override string ToString() => $"{Utc:HH:mm:ss.fff}  cmd {Command} (0x{Command:X})  {P1}, {P2}, {P3} (0x{P3:X}), {P4}";
}

/// <summary>
/// Watches the game's command function while learning is active, so Refurnish can see exactly
/// what the game sends when you store an item by hand. It only observes; every call is passed
/// through unchanged. Off unless learning.
/// </summary>
public sealed unsafe class CommandRecorder : IDisposable
{
    private delegate bool ExecuteCommandDelegate(int command, int p1, int p2, int p3, int p4);

    private readonly Hook<ExecuteCommandDelegate>? hook;
    private readonly List<CapturedCommand> captured = new();
    private readonly object gate = new();

    public string? SetupError { get; }

    public CommandRecorder()
    {
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<ExecuteCommandDelegate>(
                (nint)GameMain.Addresses.ExecuteCommand.Value, Detour);
        }
        catch (Exception ex)
        {
            SetupError = "Couldn't attach to the game's command function, so learning is unavailable.";
            Plugin.Log.Error(ex, "CommandRecorder hook failed");
        }
    }

    public bool Recording => hook?.IsEnabled ?? false;

    public void Start()
    {
        lock (gate) captured.Clear();
        hook?.Enable();
    }

    public void Stop() => hook?.Disable();

    public List<CapturedCommand> Snapshot()
    {
        lock (gate) return new List<CapturedCommand>(captured);
    }

    private bool Detour(int command, int p1, int p2, int p3, int p4)
    {
        try
        {
            lock (gate)
            {
                captured.Add(new CapturedCommand(DateTime.UtcNow, command, p1, p2, p3, p4));
                if (captured.Count > 200) captured.RemoveAt(0);
            }
        }
        catch
        {
            // Never let logging break the game's own call.
        }
        return hook!.Original(command, p1, p2, p3, p4);
    }

    /// <summary>Sends a command the same way the game does. Must be called on the framework thread.</summary>
    public static bool Send(int command, int[] p) => GameMain.ExecuteCommand(command, p[0], p[1], p[2], p[3]);

    public void Dispose() => hook?.Dispose();
}
