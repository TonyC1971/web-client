using System;
using System.Net;
using System.Net.NetworkInformation;
using ClassicUO.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network;

public class ServerListEntry
{
    private IPAddress _ipAddress;
    private IPAddress _ipAddressLittleEndian;
#if BROWSER_WASM
    // v0.7.9 iter 43 (port from CUO LoginScene.cs:1058-1070): the
    // browser sandbox does not expose ICMP, so `new Ping()` throws
    // PlatformNotSupportedException. The field initializer fires at
    // `new ServerListEntry()` inside `LoginHandshake.ServerListReceived`'s
    // `Servers[i] = ServerListEntry.Create(ref p)` loop. Under .NET 10
    // AOT WASM the throw propagates through the JS-interop boundary as
    // a native `[object WebAssembly.Exception]` from a Mercury MT
    // worker, which manifests as the post-2nd-ReceiveAsync trap that
    // bisect iterations 32-42 chased. Iter 42 with Send() gated still
    // trapped right after `[send-debug] S-block ENGAGED` — the next
    // call after TryDequeuePacket returned the 46-byte 0xA8 ServerList
    // is the PacketHandler dispatch into ServerListReceived, which
    // allocates `new ServerListEntry()` and hits this field init.
    // CUO carried the same bug for weeks until the same #if gate
    // landed on 2026-04-something. Mirror the CUO architecture: drop
    // the Ping field on wasm, replace with an unused object so the
    // calling sites that reference `_pinger` still compile, and gate
    // every Ping-touching code path below.
    private object _pinger;
#else
    private Ping _pinger = new Ping();
#endif
    private bool _sending;
    private readonly bool[] _last10Results = new bool[10];
    private int _resultIndex;

    private ServerListEntry()
    {
    }

    public static ServerListEntry Create(ref StackDataReader p)
    {
        var entry = new ServerListEntry()
        {
            Index = p.ReadUInt16BE(),
            Name = p.ReadASCII(32, true),
            PercentFull = p.ReadUInt8(),
            Timezone = p.ReadUInt8(),
            Address = p.ReadUInt32BE()
        };

        // some server sends invalid ip.
        try
        {
            entry._ipAddress = new IPAddress
            (
                new byte[]
                {
                    (byte) ((entry.Address >> 24) & 0xFF),
                    (byte) ((entry.Address >> 16) & 0xFF),
                    (byte) ((entry.Address >> 8) & 0xFF),
                    (byte) (entry.Address & 0xFF)
                }
            );

            // IP address in little-endian format, required for server ping
            entry._ipAddressLittleEndian = new IPAddress
            (
                new byte[]
                {
                    (byte) (entry.Address & 0xFF),
                    (byte) ((entry.Address >> 8) & 0xFF),
                    (byte) ((entry.Address >> 16) & 0xFF),
                    (byte) ((entry.Address >> 24) & 0xFF)
                }
            );

        }
        catch (Exception e)
        {
            Log.Error(e.ToString());
        }

#if !BROWSER_WASM
        entry._pinger.PingCompleted += entry.PingerOnPingCompleted;
#endif

        return entry;
    }


    public uint Address;
    public ushort Index;
    public string Name;
    public byte PercentFull;
    public byte Timezone;
    public int Ping = -1;
    public int PacketLoss;
    public IPStatus PingStatus;
    public long Sent;

    private static byte[] _buffData = new byte[32];
#if !BROWSER_WASM
    // Same WASM AOT trap as `new Ping()` — this static field initializer
    // runs on type load and the constructor throws
    // PlatformNotSupportedException because ICMP is unavailable in the
    // browser sandbox. Gating the field out prevents the type-loader
    // crash that would otherwise abort the first `Servers[i] =
    // ServerListEntry.Create(...)` invocation.
    private static PingOptions _pingOptions = new PingOptions(64, true);
#endif

    public void DoPing()
    {
#if BROWSER_WASM
        // No ICMP in the browser sandbox. The UI shows Ping == -1
        // which the renderer handles gracefully (same convention CUO
        // uses; see LoginScene.cs:1150-1153).
        return;
#else
        if (_ipAddress != null && !_sending && _pinger != null)
        {
            if (_resultIndex >= _last10Results.Length)
            {
                _resultIndex = 0;
            }

            try
            {
                _pinger.SendAsync
                (
                    _ipAddressLittleEndian,
                    1000,
                    _buffData,
                    _pingOptions,
                    _resultIndex++
                );
                Sent = Time.Ticks;

                _sending = true;
            }
            catch
            {
                _ipAddress = null;
                Dispose();
            }
        }
#endif
    }

#if !BROWSER_WASM
    private void PingerOnPingCompleted(object sender, PingCompletedEventArgs e)
    {
        int index = (int)e.UserState;

        if (e.Reply != null)
        {
            Ping = (int)e.Reply.RoundtripTime;
            PingStatus = e.Reply.Status;

            _last10Results[index] = e.Reply.Status == IPStatus.Success;
        }

        if (index >= _last10Results.Length - 1)
        {
            PacketLoss = 0;

            for (int i = 0; i < _resultIndex; i++)
            {
                if (!_last10Results[i])
                {
                    ++PacketLoss;
                }
            }

            if (_resultIndex > 0)
            {
                PacketLoss = (int)Math.Round((PacketLoss * 100.0) / _resultIndex, MidpointRounding.AwayFromZero);
            }
            else
            {
                PacketLoss = 0;
            }

            _resultIndex = 0;
        }

        if (Ping == -1)
        {
            Ping = (int)(Time.Ticks - Sent);
        }

        _sending = false;
    }
#endif

    public void Dispose()
    {
#if !BROWSER_WASM
        if (_pinger != null)
        {
            _pinger.PingCompleted -= PingerOnPingCompleted;

            if (_sending)
            {
                try
                {
                    _pinger.SendAsyncCancel();
                }
                catch { }

            }

            _pinger.Dispose();
            _pinger = null;
        }
#endif
    }
}
