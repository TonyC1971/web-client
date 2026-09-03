using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ClassicUO.Utility.Logging;
#if !BROWSER_WASM
using TcpSocket = System.Net.Sockets.Socket;
#endif
using static System.Buffers.ArrayPool<byte>;

namespace ClassicUO.Network.Socket;

/// <summary>
/// Handles websocket connections to shards that support it. `ws(s)://[hostname]` as the ip in settings.json.
/// For testing see `tools/ws/README.md` 
/// </summary>
sealed class WebSocketWrapper : SocketWrapper
{
    private const int MAX_RECEIVE_BUFFER_SIZE = 1024 * 1024; // 1MB
    private const int WS_KEEP_ALIVE_INTERVAL = 5;            // seconds

    private ClientWebSocket _webSocket;
#if !BROWSER_WASM
    // On wasm the browser's WebSocket API owns the underlying TCP
    // socket; we can't create a C# Socket at all (browser sandbox).
    // Any access to _rawSocket is gated on !BROWSER_WASM below.
    private TcpSocket _rawSocket;
#endif

#if BROWSER_WASM
    // ROOT-FIX 2026-04-22: cached connection flag. Under .NET 10 wasm
    // MT, reading `_webSocket.State` from the deputy thread crosses
    // into the browser's main thread via JSInterop (the WebSocket
    // object lives there). When main is busy — e.g. processing a
    // flood of DOM events (keystrokes, mouse-motion during walk),
    // Console.Error proxy writes, etc — the deputy blocks waiting
    // for the State read to round-trip, and Draw# stops advancing.
    // Symptom: caminar + escribir = freeze.
    //
    // We set this flag from the C# side — no JS read — and every
    // ProcessSend / IsConnected check becomes a pure bool load.
    // volatile to ensure visibility across threads without locks.
    private volatile bool _wasmIsConnected;
    public override bool IsConnected => _wasmIsConnected;
    public override EndPoint LocalEndPoint => null;
#else
    public override bool IsConnected => _webSocket?.State is WebSocketState.Connecting or WebSocketState.Open;
    public override EndPoint LocalEndPoint => _rawSocket?.LocalEndPoint;
#endif
    public bool IsCanceled => _tokenSource.IsCancellationRequested;

    private CancellationTokenSource _tokenSource = new();
    private CircularBuffer _receiveStream;

#if BROWSER_WASM
    // Mono-WASM is single-threaded; blocking the main thread with
    // `.Wait()` on a task whose continuations need that same thread
    // to run is a deadlock anywhere the scheduler doesn't pump the
    // browser event loop. Fire-and-forget is correct here — the
    // InvokeOnConnected event in ConnectAsync signals completion
    // to NetClient.Connected subscribers. Exceptions surface via
    // the Task's UnobservedTaskException path (we log them inside
    // ConnectAsync's catches anyway).
    //
    // Always allocate a FRESH CancellationTokenSource on wasm.
    // Disconnect (called on the 0x8C relay) cancels the old one,
    // and ConnectAsync's `tokenSource ?? new CTS()` would reuse
    // the already-cancelled token, aborting the game-phase connect
    // before it starts.
    public override void Connect(Uri uri)
    {
        _tokenSource = new CancellationTokenSource();
        _ = ConnectAsync(uri, _tokenSource);
    }
#else
    public override void Connect(Uri uri) => ConnectAsync(uri, _tokenSource).Wait();
#endif

    public override void Send(byte[] buffer, int offset, int count)
    {
#if BROWSER_WASM
        // Same motivation as StartReceiveAsync: swap to the
        // ArraySegment overload + skip ArrayPool. The send buffer
        // is small (UO packets under a few KiB) so one fresh
        // allocation per send is cheap and avoids any cross-call
        // aliasing with the in-flight ReceiveAsync's pinned buffer.
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        SendCopyAsync(copy, count);
#else
        var copy = Shared.Rent(count);
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        SendCopyAsync(copy, count);
#endif
    }

    private async void SendCopyAsync(byte[] copy, int count)
    {
        try
        {
#if BROWSER_WASM
            await _webSocket.SendAsync(new ArraySegment<byte>(copy, 0, count), WebSocketMessageType.Binary, true, _tokenSource.Token);
#else
            await _webSocket.SendAsync(copy.AsMemory().Slice(0, count), WebSocketMessageType.Binary, true, _tokenSource.Token);
#endif
        }
#if BROWSER_WASM
        catch (Exception ex)
        {
            Log.Error($"WS SendAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
#endif
        finally
        {
#if !BROWSER_WASM
            Shared.Return(copy);
#endif
        }
    }

    public override int Read(byte[] buffer)
    {
        lock (_receiveStream)
        {
            return _receiveStream.Dequeue(buffer, 0, buffer.Length);
        }
    }

    public async Task ConnectAsync(Uri uri, CancellationTokenSource tokenSource = null)
    {
        if (IsConnected)
            return;

        _tokenSource = tokenSource ?? new CancellationTokenSource();
        _receiveStream = new CircularBuffer();

        try
        {
            await ConnectWebSocketAsyncCore(uri);

            if (IsConnected)
                InvokeOnConnected();
            else
                InvokeOnError(SocketError.NotConnected);
        }
        catch (WebSocketException ex)
        {
            SocketError error = ex.InnerException?.InnerException switch
            {
                SocketException socketException => socketException.SocketErrorCode,
                _ => SocketError.SocketError
            };

            Log.Error($"Error {ex.GetType().Name} {error} while connecting to {uri} {ex}");
            InvokeOnError(error);
        }
        catch (Exception ex)
        {
            Log.Error($"Unknown Error {ex.GetType().Name} while connecting to {uri} {ex}");
            InvokeOnError(SocketError.SocketError);
        }
    }


    private async Task ConnectWebSocketAsyncCore(Uri uri)
    {
#if BROWSER_WASM
        // On wasm the browser handles TCP; ClientWebSocket routes
        // to the browser's native `new WebSocket(url)` via the
        // WASM-specific System.Net.WebSockets.Client transport.
        // No custom HttpClient / ConnectCallback (that path insists
        // on a raw C# Socket, which the browser sandbox blocks).
        // ClientWebSocketOptions.KeepAliveInterval throws
        // PlatformNotSupportedException under the Browser transport
        // — the ping/pong cadence is fixed by the browser runtime.
        _webSocket = new ClientWebSocket();

        await _webSocket.ConnectAsync(uri, _tokenSource.Token);

        // ROOT-FIX 2026-04-22: flip cached flag AFTER successful
        // connect so IsConnected returns true without any JS cross.
        _wasmIsConnected = true;

        Log.Trace($"Connected WebSocket (wasm): {uri}");

        StartReceiveAsync().ConfigureAwait(false);
#else
        // Take control of creating the raw socket, turn off Nagle, also lets us peek at `Available` bytes.
        _rawSocket = new TcpSocket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        _webSocket = new ClientWebSocket();
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(WS_KEEP_ALIVE_INTERVAL); // ping/pong

        using var httpClient = new HttpClient
        (
            new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    try
                    {
                        await _rawSocket.ConnectAsync(context.DnsEndPoint, token);

                        return new NetworkStream(_rawSocket, ownsSocket: true);
                    }
                    catch
                    {
                        _rawSocket?.Dispose();
                        _rawSocket = null;
                        _webSocket?.Dispose();
                        _webSocket = null;

                        throw;
                    }
                }
            }
        );


        await _webSocket.ConnectAsync(uri, httpClient, _tokenSource.Token);

        Log.Trace($"Connected WebSocket: {uri}");

        // Kicks off the async receiving loop
        StartReceiveAsync().ConfigureAwait(false);
#endif
    }

    private async Task StartReceiveAsync()
    {
#if BROWSER_WASM
        // Under Mono's Browser transport the Memory<byte> overload
        // of ReceiveAsync rides through a JS interop path that
        // has tripped "memory access out of bounds" during P5a
        // bring-up. The ArraySegment<byte> overload is the older
        // API and avoids the Memory<>/Span<> layer entirely.
        // Also skip ArrayPool — pin a single managed buffer for
        // the lifetime of the wrapper so no other pool user can
        // steal or resize it out from under an in-flight receive.
        var buffer = new byte[65536];
        var position = 0;

        // O42 root-fix 2026-04-23: snapshot _webSocket + _tokenSource
        // into locals at loop entry. The relay flow
        // (LoginScene.HandleRelayServerPacket) calls Disconnect()
        // then Connect() on the SAME SocketWrapper; Connect
        // reassigns both fields in place. If this (OLD) loop reads
        // the fields after the swap — in its while-guard, its
        // ReceiveAsync call, or the error fall-through at the
        // bottom — it will either (a) keep receiving on the fresh
        // game-phase socket, corrupting the new handshake, or
        // (b) fire InvokeOnError(ConnectionReset) against the NEW
        // socket because the new CTS isn't cancelled and the new
        // WS's CloseStatus isn't NormalClosure, tearing down the
        // game-phase connection mid-setup. Reading only the locals
        // keeps each loop instance bound to the socket it started
        // on.
        var localSocket = _webSocket;
        var localCts = _tokenSource;

        try
        {
            while (!localCts.IsCancellationRequested)
            {
                var seg = new ArraySegment<byte>(buffer, position, buffer.Length - position);
                var receiveResult = await localSocket.ReceiveAsync(seg, localCts.Token);
#else
        var buffer = Shared.Rent(4096);
        var memory = buffer.AsMemory();
        var position = 0;

        try
        {
            while (IsConnected)
            {
                GrowReceiveBufferIfNeeded(ref buffer, ref memory);

                var receiveResult = await _webSocket.ReceiveAsync(memory.Slice(position), _tokenSource.Token);
#endif

                // Ignoring message types:
                // 1. WebSocketMessageType.Text: shouldn't be sent by the server, though might be useful for multiplexing commands
                // 2. WebSocketMessageType.Close: will be handled by IsConnected
                if (receiveResult.MessageType == WebSocketMessageType.Binary)
                    position += receiveResult.Count;

                if (!receiveResult.EndOfMessage)
                    continue;

                lock (_receiveStream)
                {
                    _receiveStream.Enqueue(buffer, 0, position);
                }

                position = 0;
            }
        }
        catch (OperationCanceledException)
        {
#if BROWSER_WASM
            Log.Trace("WebSocket OperationCanceledException on websocket " + (localCts.IsCancellationRequested ? "(was requested)" : "(remote cancelled)"));
#else
            Log.Trace("WebSocket OperationCanceledException on websocket " + (IsCanceled ? "(was requested)" : "(remote cancelled)"));
#endif
        }
        catch (Exception e)
        {
            Log.Trace($"WebSocket error in StartReceiveAsync {e}");
#if BROWSER_WASM
            // O42: guard against reading the field `_webSocket`.
            // See snapshot comment at loop entry — a fresh Connect
            // can have replaced _webSocket by the time this catch
            // runs, so check the LOCAL socket for clean-close state.
            var cleanCloseInFlight =
                localSocket?.CloseStatus == WebSocketCloseStatus.NormalClosure ||
                localSocket?.State == WebSocketState.CloseReceived ||
                localSocket?.State == WebSocketState.CloseSent ||
                localSocket?.State == WebSocketState.Closed;

            // Bug 2026-04-30 — first-login race that hung at "Verifying
            // account": if the server packs the 0x8C GameServerRelay
            // payload into the same TCP segment as the WebSocket Close
            // frame, the next ReceiveAsync after the 0x8C frame throws
            // `WebSocketException(net_WebSockets_InvalidState,
            // CloseReceived, Open, CloseSent)` mid-loop. The bytes
            // already pulled into `buffer[0..position]` are still
            // valid but never reach `_receiveStream` because the loop
            // only enqueues on EndOfMessage. Result: the 0x8C is
            // dropped, HandleRelayServerPacket never fires, no
            // game-phase reconnect, client hangs at "Verifying
            // account" forever. Reload of the browser usually wins
            // the race because the second auth attempt arrives with
            // the 0x8C and the Close in separate frames.
            //
            // Fix: when the close was clean and we have buffered
            // bytes from the in-flight frame, flush them to the
            // receive stream so the upstream parser sees the 0x8C.
            //
            // Diagnostic log: this hypothesis was deduced from the
            // exception signature alone, not reproduced locally
            // (the race only happens over Cloudflare HTTPS).
            // The line below tells us, on the next prod deploy,
            // whether `position > 0` actually fires when the hang
            // happens. If we see `position=0` consistently with
            // hangs, the hypothesis is wrong and we look elsewhere.
            // TODO: remove the log once we confirm one way or the
            // other.
            System.Console.WriteLine(
                $"[ws-catch] cleanClose={cleanCloseInFlight} position={position} state={localSocket?.State} closeStatus={localSocket?.CloseStatus}");
            if (cleanCloseInFlight && position > 0)
            {
                lock (_receiveStream)
                {
                    _receiveStream.Enqueue(buffer, 0, position);
                }
                position = 0;
            }
            if (!cleanCloseInFlight && !localCts.IsCancellationRequested)
                InvokeOnError(SocketError.SocketError);
#else
            InvokeOnError(SocketError.SocketError);
#endif
        }
        finally
        {
#if !BROWSER_WASM
            Shared.Return(buffer);
#endif
        }

#if BROWSER_WASM
        // A clean server-initiated close (WS CloseStatus == NormalClosure)
        // is how ModernUO tears down the login-phase TCP after 0x8C. On
        // desktop the raw TCP FIN is handled by the SocketWrapper
        // abstraction before CUO sees "Connection Lost"; on wasm the
        // server's WS Close arrives at the same time as the 0x8C payload,
        // and if StartReceiveAsync sees the Close *before* the scene
        // code calls Disconnect() we'd fire ConnectionReset and pop up
        // "Connection Lost" mid-handoff. Suppress ONLY on clean closes
        // (CloseStatus == NormalClosure) so genuine errors still surface.
        //
        // O42: localSocket + localCts snapshot so the fall-through
        // binds to THIS loop's socket, not a post-relay replacement.
        var cleanClose = localSocket?.CloseStatus == WebSocketCloseStatus.NormalClosure;
        if (!localCts.IsCancellationRequested && !cleanClose)
            InvokeOnError(SocketError.ConnectionReset);
#else
        if (!IsCanceled)
            InvokeOnError(SocketError.ConnectionReset);
#endif
    }

    // This is probably unnecessary, but WebSocket frames can be up to 2^63 bytes so we put some cap on it, yet to see packets larger than 4KB come through.
    // We peek the raw tcp socket available bytes, grow if the frame is bigger, we're naively assuming no compression.
    private void GrowReceiveBufferIfNeeded(ref byte[] buffer, ref Memory<byte> memory)
    {
#if BROWSER_WASM
        // No raw TCP peek on wasm — ClientWebSocket exposes each
        // fragment via ReceiveResult.Count, and we concatenate with
        // EndOfMessage. Grow when the current write position is
        // within one KiB of the end of the buffer; cap at the same
        // MAX_RECEIVE_BUFFER_SIZE the desktop path uses.
        // `buffer` / `memory` are maintained by StartReceiveAsync.
        // We only need to preempt the rare big-frame case.
        // (Typical UO packets are <4 KiB.)
        if (buffer.Length >= MAX_RECEIVE_BUFFER_SIZE)
            return;
#else
        if (_rawSocket.Available <= buffer.Length)
            return;

        if (_rawSocket.Available > MAX_RECEIVE_BUFFER_SIZE)
            throw new SocketException((int)SocketError.MessageSize, $"WebSocket message frame too large: {_rawSocket.Available} > {MAX_RECEIVE_BUFFER_SIZE}");

        Log.Trace($"WebSocket growing receive buffer {buffer.Length} bytes to {_rawSocket.Available} bytes");

        Shared.Return(buffer);
        buffer = Shared.Rent(_rawSocket.Available);
        memory = buffer.AsMemory();
#endif
    }

    public override void Disconnect()
    {
        if (!IsConnected)
            return;

#if BROWSER_WASM
        // ROOT-FIX 2026-04-22: flip cached flag FIRST so any concurrent
        // deputy-thread caller sees the socket as disconnected and
        // stops trying to Send / read. CloseAsync below is fire-and-
        // forget (ContinueWith cancels the token when it lands).
        _wasmIsConnected = false;
#endif

        try
        {
            _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None)
                .ContinueWith(_ => _tokenSource?.Cancel());
        }
        catch
        {
            _tokenSource?.Cancel();
        }
    }

    public override void Dispose()
    {
    }
}