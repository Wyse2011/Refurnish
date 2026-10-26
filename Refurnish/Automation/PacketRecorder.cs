using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Application.Network;

namespace Refurnish.Automation;

public sealed record CapturedPacket(DateTime Utc, byte[] Bytes, uint A3, uint A4, bool A5)
{
    public override string ToString() =>
        $"{Utc:HH:mm:ss.fff}  a3={A3} a4={A4} a5={A5}  {BitConverter.ToString(Bytes, 0, Math.Min(Bytes.Length, 0x40)).Replace("-", " ")}";
}

/// <summary>
/// Watches the game's outgoing network messages. Every message is passed through untouched.
/// It's on from plugin load so it always knows the game's current connection (needed to send);
/// while learning it also keeps a copy of each message. Sending reuses the game's own send
/// function with a message built from a learned copy. The remembered connection is forgotten on
/// every zone change and logout, so a stale one is never used.
/// </summary>
public sealed unsafe class PacketRecorder : IDisposable
{
    public const int CaptureLength = 0x60;

    private delegate bool SendPacketDelegate(ZoneClient* self, nint packet, uint a3, uint a4, bool a5);

    private readonly Hook<SendPacketDelegate>? hook;
    private readonly object gate = new();
    private readonly List<CapturedPacket> captured = new();
    private readonly List<(nint Ptr, DateTime FreeAfterUtc)> buffers = new();
    private ZoneClient* lastClient;
    private DateTime lastClientUtc;
    private bool capturing;

    public string? SetupError { get; }

    public PacketRecorder()
    {
        try
        {
            hook = Plugin.GameInterop.HookFromAddress<SendPacketDelegate>((nint)ZoneClient.Addresses.SendPacket.Value, Detour);
        }
        catch (Exception ex)
        {
            SetupError = "Couldn't attach to the game's network send function, so placing is unavailable.";
            Plugin.Log.Error(ex, "PacketRecorder hook failed");
        }
    }

    /// <summary>True once the game has sent any message since the last zone change.</summary>
    public bool HasClient => lastClient != null;

    /// <summary>Turns on passive watching. Called once at plugin load.</summary>
    public void Enable()
    {
        hook?.Enable();
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.ClientState.Logout += OnLogout;
    }

    private void OnTerritoryChanged(uint territory) => lastClient = null;

    private void OnLogout(int type, int code) => lastClient = null;

    // Kept so existing callers don't need to change; watching is always on now.
    public void Acquire() { }

    public void Release() { }

    public void StartCapture()
    {
        lock (gate)
        {
            captured.Clear();
            capturing = true;
        }
    }

    public List<CapturedPacket> StopCapture()
    {
        lock (gate)
        {
            capturing = false;
            return new List<CapturedPacket>(captured);
        }
    }

    private bool Detour(ZoneClient* self, nint packet, uint a3, uint a4, bool a5)
    {
        try
        {
            lastClient = self;
            lastClientUtc = DateTime.UtcNow;
            if (capturing && packet != 0)
            {
                var bytes = new byte[CaptureLength];
                Marshal.Copy(packet, bytes, 0, CaptureLength);
                lock (gate)
                {
                    captured.Add(new CapturedPacket(DateTime.UtcNow, bytes, a3, a4, a5));
                    if (captured.Count > 400) captured.RemoveAt(0);
                }
            }
        }
        catch
        {
            // Never let observing break the game's own message.
        }
        return hook!.Original(self, packet, a3, a4, a5);
    }

    /// <summary>Sends a message through the game's own send function. Framework thread only.</summary>
    public bool Send(byte[] data, uint a3, uint a4, bool a5)
    {
        if (hook == null || !HasClient) return false;

        // The buffer is kept alive for a while in case the game reads it after returning.
        var size = Math.Max(data.Length, 0x100);
        var ptr = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)ptr, size).Clear();
        Marshal.Copy(data, 0, ptr, data.Length);
        buffers.Add((ptr, DateTime.UtcNow.AddSeconds(30)));

        return hook.Original(lastClient, ptr, a3, a4, a5);
    }

    /// <summary>Frees send buffers that are old enough. Called every framework tick.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        for (var i = buffers.Count - 1; i >= 0; i--)
        {
            if (buffers[i].FreeAfterUtc > now) continue;
            Marshal.FreeHGlobal(buffers[i].Ptr);
            buffers.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.ClientState.Logout -= OnLogout;
        hook?.Dispose();
        foreach (var b in buffers) Marshal.FreeHGlobal(b.Ptr);
        buffers.Clear();
    }
}
