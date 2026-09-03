using ClassicUO.Network.Encryption;
using ClassicUO.Utility.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Buffers;
using ClassicUO.Utility;
using SDL3;

namespace ClassicUO.Network
{
    sealed class AsyncSocketWrapper(AsyncNetClient client) : IDisposable
    {
        private AsyncNetClient _client = client;
#if BROWSER_WASM
        // v0.7.9 iter 23: WASM port routes the server connection through
        // a browser WebSocket. Raw TCP sockets are blocked by the browser
        // sandbox (TcpClient → PlatformNotSupportedException). The shard
        // YAML supplies a `wss://uonexus.com/ws?server=<slug>` URL which
        // the proxy upgrades to a TCP connection to ModernUO server-side.
        // Mirrors CUO's source/cuo/.../Network/Socket/WebSocketWrapper.cs
        // (no abstract base because TUO has only one shape — sealed
        // AsyncSocketWrapper).
        private ClientWebSocket _ws;
        // Cached connection flag — reading `_ws.State` from the deputy
        // thread crosses into the browser main thread via JSInterop and
        // can deadlock under load (see CUO ROOT-FIX 2026-04-22 in
        // WebSocketWrapper.cs). Pure bool load instead.
        private volatile bool _wasmIsConnected;
        public bool IsConnected => _wasmIsConnected;
        public EndPoint LocalEndPoint => null;
#else
        private TcpClient _socket;
        private NetworkStream _stream;
        public bool IsConnected => _socket?.Client?.Connected ?? false;
        public EndPoint LocalEndPoint => _socket?.Client?.LocalEndPoint;
#endif
        private CancellationTokenSource _cancellationTokenSource;
        private Task _receiveTask;

        public event EventHandler OnConnected, OnDisconnected;
        public event EventHandler<SocketError> OnError;

        public async Task<bool> ConnectAsync(string ip, int port, CancellationToken cancellationToken = default, int timeoutS = 2)
        {
            if (IsConnected)
                return true;

#if BROWSER_WASM
            // The shard config in WASM passes `wss://host/ws?server=slug`
            // as the `ip` string and 443 as the port. Build the URI from
            // the host. If `ip` already looks like a ws(s) URL, use it
            // verbatim; otherwise synthesise wss://<ip>:<port>/ — covers
            // both the proxy-wired path and any future shard that hands
            // back a raw hostname.
            try
            {
                Uri uri;
                if (ip.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                    ip.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    uri = new Uri(ip);
                }
                else
                {
                    var scheme = port == 80 ? "ws" : "wss";
                    uri = new Uri($"{scheme}://{ip}:{port}/");
                }

                _ws = new ClientWebSocket();
                // ClientWebSocketOptions.KeepAliveInterval throws
                // PlatformNotSupportedException under the Browser
                // transport — the ping/pong cadence is fixed by the
                // browser runtime. Mirrors CUO WebSocketWrapper.cs L176-180.
                _cancellationTokenSource = new CancellationTokenSource();

                // Race against a timeout so a wedged connect doesn't
                // hang VerifyingAccount forever.
                var connectTask = _ws.ConnectAsync(uri, _cancellationTokenSource.Token);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutS), _cancellationTokenSource.Token);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    try { _ws.Abort(); } catch { }
                    Log.Warn($"WebSocket connect timed out after {timeoutS}s to {uri}");
                    return false;
                }

                // Surface ConnectAsync exceptions caught by WhenAny
                await connectTask;

                _wasmIsConnected = true;
                Log.Trace($"Connected WebSocket (wasm): {uri}");

                // v0.7.9 iter 27 (root cause measured in iter 26):
                //
                //   Mercury MT's WebSocket implementation traps natively
                //   on the SECOND ReceiveAsync call when the receive
                //   loop is dispatched to the ThreadPool (Task.Run). The
                //   first call succeeds, OnDataReceived fires, we loop
                //   back to `await localWs.ReceiveAsync(...)` — and the
                //   worker emits `Uncaught [object WebAssembly.Exception]`
                //   before the await returns. The trap is BEFORE any
                //   managed catch block runs, so neither the try/catch
                //   inside ReceiveLoopAsync nor `TaskScheduler.
                //   UnobservedTaskException` ever sees it. The browser's
                //   WebSocket API requires the *same JS thread* between
                //   construction and subsequent reads; Task.Run gave us
                //   the first receive on whatever thread JSImport
                //   marshalled to, then the second receive on a fresh
                //   ThreadPool thread — and the JS-side WebSocket
                //   handle's deputy-side proxy refused to talk.
                //
                //   CUO's WebSocketWrapper avoids this by firing the
                //   receive loop as `StartReceiveAsync().ConfigureAwait
                //   (false)` (bare async, NO Task.Run). The ConfigureAwait
                //   detaches from the SyncContext so awaits hop to the
                //   ThreadPool when convenient — but every subsequent
                //   receive runs on whatever thread serviced the first
                //   one, keeping the JS-side handle stable.
                //
                //   Mirroring CUO here. NetworkLoopAsync at the outer
                //   AsyncNetClient.Connect level KEEPS Task.Run (its
                //   send loop has its own _ws references and doesn't
                //   re-enter ReceiveAsync) — only the inner receive
                //   needs the CUO pattern.
                _receiveTask = ReceiveLoopAsync(_cancellationTokenSource.Token);
                OnConnected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (WebSocketException wsEx)
            {
                Log.Error($"WebSocket connect failed: {wsEx.GetType().Name} {wsEx.Message}");
                OnError?.Invoke(this, SocketError.SocketError);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"WebSocket connect unknown error: {ex}");
                OnError?.Invoke(this, SocketError.SocketError);
                return false;
            }
#else
            try
            {
                _socket = new TcpClient();
                _socket.NoDelay = true;
                _cancellationTokenSource = new CancellationTokenSource();

                Task connectTask = _socket.ConnectAsync(ip, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutS), _cancellationTokenSource.Token); // set your timeout here

                Task completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _socket.Close(); // optional: cleanup

                    return false;
                }

                if (!IsConnected)
                {
                    OnError?.Invoke(this, SocketError.NotConnected);

                    return false;
                }

                _stream = _socket.GetStream();

                // Start background receive task
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                OnConnected?.Invoke(this, EventArgs.Empty);

                return true;
            }
            catch (SocketException socketEx)
            {
                Log.Error($"Error while connecting {socketEx}");
                OnError?.Invoke(this, socketEx.SocketErrorCode);

                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"Error while connecting {ex}");
                OnError?.Invoke(this, SocketError.SocketError);

                return false;
            }
#endif
        }

        public async Task SendAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
#if BROWSER_WASM
            if (!_wasmIsConnected || _ws == null)
                return;

            try
            {
                // ArraySegment overload (older API) avoids the Memory<>/Span<>
                // JS-interop layer that has tripped OOB traps under Mono's
                // Browser transport. Mirrors CUO WebSocketWrapper.cs:104-105.
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                await _ws.SendAsync(new ArraySegment<byte>(copy, 0, count),
                                    WebSocketMessageType.Binary, true, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error($"WS SendAsync failed: {ex.GetType().Name}: {ex.Message}");
                OnError?.Invoke(this, SocketError.SocketError);
            }
#else
            if (!IsConnected || _stream == null)
                return;

            try
            {
                await _stream.WriteAsync(buffer, offset, count, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error($"Error while sending {ex}");
                OnError?.Invoke(this, SocketError.SocketError);
            }
#endif
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
#if BROWSER_WASM
            // WebSocket receive loop. ArraySegment overload to avoid the
            // Memory<>/Span<> JS-interop layer. Snapshot the WS + CTS so
            // a fresh Connect (e.g. after the 0x8C game-phase relay)
            // doesn't yank fields out from under us mid-await. Mirrors
            // CUO WebSocketWrapper.cs O42 root-fix 2026-04-23.
            Log.Trace("[recv-debug] R0: ReceiveLoopAsync enter (wasm)");
            var localWs = _ws;
            var localCts = _cancellationTokenSource;
            // v0.7.9 iter 39: explicit pin of the receive buffer for the
            // lifetime of the WS connection. Hypothesis: Mercury MT's GC
            // moves the buffer between consecutive ReceiveAsync calls,
            // invalidating the WS JS-side pin metadata recorded during
            // the first receive. Holding a managed pin via
            // GC.AllocateUninitializedArray<byte>(..., pinned: true)
            // permanently anchors the array in the pinned heap so the
            // GC can never relocate it. CUO's pattern uses a fresh
            // local `new byte[65536]` which lands in the regular gen-0
            // heap; if my hypothesis is wrong this won't help, but the
            // change is cheap to revert.
            var buffer = GC.AllocateUninitializedArray<byte>(65536, pinned: true);
            var position = 0;
            int iter = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested && _wasmIsConnected)
                {
                    iter++;
                    var seg = new ArraySegment<byte>(buffer, position, buffer.Length - position);
                    if (CUOEnviroment.Debug) Log.Trace($"[recv-debug] R1.{iter}: pre-ReceiveAsync pos={position}");
                    var receiveResult = await localWs.ReceiveAsync(seg, localCts.Token).ConfigureAwait(false);
                    if (CUOEnviroment.Debug) Log.Trace($"[recv-debug] R2.{iter}: post-ReceiveAsync type={receiveResult.MessageType} count={receiveResult.Count} EOM={receiveResult.EndOfMessage}");

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        _wasmIsConnected = false;
                        OnDisconnected?.Invoke(this, EventArgs.Empty);
                        Disconnect();
                        break;
                    }

                    if (receiveResult.MessageType == WebSocketMessageType.Binary && receiveResult.Count > 0)
                    {
                        position += receiveResult.Count;
                    }

                    // Wait for the end of the WS message, then flush as
                    // a single packet to mirror the desktop semantics
                    // (one read → one OnDataReceived).
                    if (!receiveResult.EndOfMessage)
                        continue;

                    if (position > 0)
                    {
                        // v0.7.9 iter 37 — REVERT to iter 32 baseline (proven
                        // path). Iter 33-36 bisect attempts (Span.CopyTo,
                        // lock, _scratch intermediate) all either trapped
                        // or were blocked by server-side rate limit. Memento
                        // started silently rejecting our probe connections
                        // mid-bisect, so iter 36's outcome is unknown.
                        // Revert to the path that DOES at least get real
                        // users to Verifying Account before the trap, so
                        // Memento isn't shipped a possibly-worse build.
                        // Bisect resumes once rate limit clears (or we
                        // switch to a non-rate-limited test target).
                        if (CUOEnviroment.Debug) Log.Trace($"[recv-debug] R3.{iter}: enqueue {position} raw bytes (iter32 baseline)");
                        byte[] raw = new byte[position];
                        Array.Copy(buffer, raw, position);
                        _client.EnqueueRawBytes(raw);
                        if (CUOEnviroment.Debug) Log.Trace($"[recv-debug] R4.{iter}: post-enqueue");
                        position = 0;
                    }
                }
                Log.Trace("[recv-debug] R5: ReceiveLoopAsync exit-while (clean)");
            }
            catch (OperationCanceledException)
            {
                Log.Trace("[recv-debug] R6: OperationCanceledException");
                Disconnect();
                OnError?.Invoke(this, SocketError.Success);
            }
            catch (WebSocketException wsEx)
            {
                // O42 from CUO: a clean server-initiated close
                // (NormalClosure) is how the server tears down the
                // login-phase TCP after 0x8C relay. Suppress the
                // ConnectionReset surface ONLY on clean close so the
                // game-phase reconnect can run without "Connection
                // Lost" flashing up.
                var cleanClose =
                    localWs?.CloseStatus == WebSocketCloseStatus.NormalClosure ||
                    localWs?.State == WebSocketState.CloseReceived ||
                    localWs?.State == WebSocketState.CloseSent ||
                    localWs?.State == WebSocketState.Closed;

                if (cleanClose && position > 0)
                {
                    byte[] data = new byte[position];
                    Array.Copy(buffer, data, position);
                    _client.OnDataReceived(data);
                }

                Disconnect();
                if (cleanClose)
                {
                    Log.Trace($"[recv-debug] R7: clean WS close caught: {wsEx.GetType().Name} {wsEx.Message}");
                    OnError?.Invoke(this, SocketError.Success);
                }
                else
                {
                    Log.Error($"[recv-debug] R7-err: WebSocket receive error: {wsEx.GetType().FullName} {wsEx.Message}\n{wsEx}");
                    OnError?.Invoke(this, SocketError.SocketError);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[recv-debug] R8: WebSocket receive unknown error: {ex.GetType().FullName}: {ex.Message}\n{ex}");
                Disconnect();
                OnError?.Invoke(this, SocketError.SocketError);
            }
#else
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    // Read directly instead of gating on Available. A blocking ReadAsync returns 0
                    // when the remote closes the connection (FIN); gating on Available > 0 meant a
                    // graceful/half-open close was never detected, leaving the socket "connected"
                    // forever. ReadAsync also yields naturally, so no busy-wait delay is needed.
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead == 0)
                    {
                        OnDisconnected?.Invoke(this, EventArgs.Empty);
                        Disconnect();

                        break;
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        byte[] data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        _client.OnDataReceived(data);
                        //OnDataReceived?.Invoke(this, data);
                    }
                }
            }
            catch (IOException ioEx) when (ioEx.InnerException is SocketException socketEx)
            {
                Disconnect();

                switch (socketEx.SocketErrorCode)
                {
                    case SocketError.OperationAborted: OnError?.Invoke(this, SocketError.Success); break;
                    default:
                        Log.Error($"Socket error in receive loop: {socketEx.SocketErrorCode} - {socketEx.Message}");
                        OnError?.Invoke(this, socketEx.SocketErrorCode); break;
                }

            }
            catch (OperationCanceledException)
            {
                Disconnect();
                OnError?.Invoke(this, SocketError.Success);
            }
            catch (Exception ex)
            {
                Log.Error($"Error in receive loop {ex}");
                Disconnect();
                OnError?.Invoke(this, SocketError.SocketError);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
#endif
        }

        private bool _isDisconnecting;
        public void Disconnect()
        {
            if (_isDisconnecting)
                return;

            _isDisconnecting = true;

#if BROWSER_WASM
            // Flip the cached flag FIRST so concurrent deputy-thread
            // callers see the socket as disconnected and stop trying
            // to Send. CloseAsync is fire-and-forget.
            _wasmIsConnected = false;
            try
            {
                _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None)
                   .ContinueWith(_ => _cancellationTokenSource?.Cancel());
            }
            catch
            {
                _cancellationTokenSource?.Cancel();
            }
            // Don't .Wait() on the receive task on wasm — single-threaded
            // Mono can deadlock on it.
#else
            _cancellationTokenSource?.Cancel();
            _receiveTask?.Wait(5000);
            _stream?.Close();
            _socket?.Close();
#endif
        }

        public void Dispose()
        {
#if BROWSER_WASM
            _cancellationTokenSource?.Cancel();
            try { _ws?.Dispose(); } catch { }
            _cancellationTokenSource?.Dispose();
#else
            _cancellationTokenSource?.Cancel();
            _receiveTask?.Wait(5000);
            _stream?.Dispose();
            _socket?.Dispose();
            _cancellationTokenSource?.Dispose();
#endif
        }
    }

    public sealed class AsyncNetClient : IDisposable
    {
        private const int BUFF_SIZE = 0x10000;

        private readonly byte[] _compressedBuffer = new byte[4096];
        private readonly byte[] _uncompressedBuffer = new byte[BUFF_SIZE];
        private readonly Huffman _huffman = new Huffman();
        private bool _isCompressionEnabled;
        private readonly AsyncSocketWrapper _socket;
        private uint? _localIP;
        private readonly CircularBuffer _sendStream;
        private readonly ConcurrentQueue<byte[]> _incomingMessages = new();
        private Task _networkTask;
        private CancellationTokenSource _cancellationTokenSource = new();
        public static PacketsTable PacketsTable { get; private set; }
#nullable enable
        public static EncryptionHelper? Encryption { get; private set; }
#nullable disable
        public static AsyncNetClient Socket { get; set; } = new AsyncNetClient();
        public bool IsConnected => _socket != null && _socket.IsConnected;
        public NetStatistics Statistics { get; }

        public AsyncNetClient()
        {
            Statistics = new NetStatistics(this);
            _sendStream = new CircularBuffer();

            _socket = new AsyncSocketWrapper(this);

            _socket.OnConnected += (o, e) =>
            {
                Statistics.Reset();
                Connected?.Invoke(this, EventArgs.Empty);
            };

            _socket.OnDisconnected += (o, e) => Disconnected?.Invoke(this, SocketError.Success);
            _socket.OnError += (o, e) => Disconnected?.Invoke(this, e);
            //_socket.OnDataReceived += OnDataReceived;
        }

        public static EncryptionType Load(ClientVersion clientVersion, EncryptionType encryption)
        {
            PacketsTable = new PacketsTable(clientVersion);

            if (encryption != 0)
            {
                Encryption = new EncryptionHelper(clientVersion);
                Log.Trace("Calculating encryption by client version...");
                Log.Trace($"encryption: {Encryption.EncryptionType}");

                if (Encryption.EncryptionType != encryption)
                {
                    Log.Warn($"Encryption found: {Encryption.EncryptionType}");
                    encryption = Encryption.EncryptionType;
                }
            }

            return encryption;
        }

        public uint LocalIP
        {
            get
            {
                if (!_localIP.HasValue)
                {
                    try
                    {
                        byte[] addressBytes = (_socket?.LocalEndPoint as IPEndPoint)?.Address.MapToIPv4().GetAddressBytes();

                        if (addressBytes != null && addressBytes.Length != 0)
                        {
                            _localIP = (uint)(addressBytes[0] | (addressBytes[1] << 8) | (addressBytes[2] << 16) | (addressBytes[3] << 24));
                        }

                        if (!_localIP.HasValue || _localIP == 0)
                        {
                            _localIP = 0x100007f;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"error while retrieving local endpoint address: \n{ex}");
                        _localIP = 0x100007f;
                    }
                }

                return _localIP.Value;
            }
        }

        public event EventHandler Connected;
        public event EventHandler<SocketError> Disconnected;

        public async Task<bool> Connect(string ip, ushort port, CancellationToken cancellationToken = new ())
        {
            _sendStream.Clear();
            _huffman.Reset();
            // Relogin-hang fix (ported from CUO NetClient.Connect's
            // #if BROWSER_WASM block): pin compression OFF at the START of
            // every connect. Disconnect() resets it, but the WebSocket close
            // is fire-and-forget — a fast relogin (logout→relogin, or a
            // different account) can begin a new Connect before the
            // close-complete event fires, leaking _isCompressionEnabled=true
            // from the previous in-game session. A leaked-true flag makes the
            // login-phase Seed/FirstLogin go out Huffman-compressed, which the
            // login server cannot parse, so it closes the connection with no
            // response → the client hangs forever on "Verifying Account"
            // (operator-reported: gm-account always, test3 after prior sessions).
            // The login phase is always un-compressed per protocol, so pinning
            // false here is correct no matter what leaked from last session.
            _isCompressionEnabled = false;
            Statistics.Reset();

            bool success = await _socket.ConnectAsync(ip, port, cancellationToken);

            if (success)
            {
                _cancellationTokenSource = new CancellationTokenSource();
#if BROWSER_WASM
                // v0.7.9 iter 55 ROOT-FIX: do NOT start NetworkLoopAsync on
                // browser-wasm. That loop ran on a Task.Run ThreadPool
                // worker, spinning `ProcessSendAsync + Statistics.Update +
                // await Task.Delay(1)` forever. On .NET 10 Mercury MT,
                // that perpetually-busy worker cannot reliably reach a GC
                // safepoint — so when the main thread allocates during the
                // first Chunk.Load (Land.Create × 64 tiles), the GC's
                // stop-the-world cannot suspend it:
                //     [MONO] mono-threads.c:336 <disabled>
                //     WAITING for 1 threads, got 0 suspended
                // → fatal worker error surfacing as "memory access out of
                // bounds" / "WebAssembly.Exception" at whatever managed
                // allocation runs next (AddGameObject on the first tile).
                // This is the exact trap that iter 50 (conditional alloc)
                // and iter 51 (GetTileZ cache) only PARTIALLY mitigated by
                // reducing allocation count — they made the GC fire later,
                // not the suspend succeed.
                //
                // Since iter 53 made Send() fire-and-forget (bypassing
                // _sendStream), NetworkLoopAsync no longer pumps anything:
                // ProcessSendAsync finds an empty _sendStream every tick.
                // The only other thing it did was Statistics.Update(),
                // which is cosmetic and can ride the main Update tick.
                // CUO has NO equivalent send-pump thread — its Send is
                // fire-and-forget and the receive continuation rides the
                // first servicing thread. Mirror that: single managed
                // worker (the receive continuation) + main thread only.
                _networkTask = null;
#else
                // Keep Task.Run on desktop — real OS threads suspend fine
                // and the send-pump batches CircularBuffer drains.
                _networkTask = Task.Run(() => NetworkLoopAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
#endif
            }
            else
            {
                Disconnected?.Invoke(this, SocketError.NotConnected);
            }

            return success;
        }

        private bool _isDisconnecting;
        public async Task Disconnect()
        {
            if (_isDisconnecting)
                return;

            _isDisconnecting = true;

            SDL.SDL_CaptureMouse(false);
            _isCompressionEnabled = false;
            Statistics.Reset();

            _cancellationTokenSource?.Cancel();

            if(_networkTask != null)
            {
                try
                {
                    await Task.WhenAny(_networkTask,Task.Delay(5000));
                }
                catch { }
            }

            ClearIncomingMessages();

            _socket.Disconnect();
            _huffman.Reset();
            _sendStream.Clear();
        }

        public void EnableCompression()
        {
            _isCompressionEnabled = true;
            _huffman.Reset();
            _sendStream.Clear();
        }

        private async Task NetworkLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                try
                {
                    // Process outgoing data
                    await ProcessSendAsync(cancellationToken);

                    // Update statistics
                    Statistics.Update();

                    // Small delay to prevent excessive CPU usage
                    await Task.Delay(1, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await Disconnect();
                    Disconnected?.Invoke(this, SocketError.Success);
                    break;
                }
                catch (Exception ex)
                {
                    await Disconnect();
                    Log.Error($"Network loop error: {ex}");
                    Disconnected?.Invoke(this, SocketError.SocketError);
                    break;
                }
            }
        }

#if BROWSER_WASM
        // v0.7.9 iter 29: BG-receive-loop fast-path. Just enqueues raw
        // bytes; main thread does the heavy decode in TryDequeuePacket.
        // Pure ConcurrentQueue.Enqueue is the only operation on the
        // deputy worker — keeps the WS JSObject handle stable for the
        // next ReceiveAsync.
        public void EnqueueRawBytes(byte[] data) => _incomingMessages.Enqueue(data);

        // v0.7.9 iter 35: explicit lock object the receive loop holds
        // while reading from the WS-pinned receive buffer + enqueuing
        // the copy. Mirrors CUO WebSocketWrapper's `lock (_receiveStream)`
        // around its CircularBuffer.Enqueue. The lock acts as a full
        // memory fence on Mercury MT and may unstick the WS JS-side
        // handle's pin/unpin transaction so the next ReceiveAsync
        // doesn't trap natively.
        public readonly object SyncLock = new object();

        // v0.7.9 iter 36: long-lived (gen-2 promoted) scratch buffer for
        // the BG receive loop. Pre-allocated so no per-receive `new byte[]`
        // happens; the copy from the WS-pinned receive buffer lands in
        // this stable address instead of a fresh allocation. Tests
        // whether fresh-alloc-as-copy-destination is the trap trigger.
        public readonly byte[] _scratch = new byte[65536];
#endif

        public void OnDataReceived(byte[] data)
        {
            try
            {
                Statistics.TotalBytesReceived += (uint)data.Length;

                Span<byte> span = data.AsSpan();
                ProcessEncryption(span);
                Span<byte> decompressed = DecompressBuffer(span);

                if (!decompressed.IsEmpty)
                {
                    byte[] message = decompressed.ToArray();
                    _incomingMessages.Enqueue(message);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error processing received data: {ex}");
            }
        }

        public bool TryDequeuePacket(out byte[] packet)
        {
            return _incomingMessages.TryDequeue(out packet);
        }

        public void ClearIncomingMessages()
        {
            while (_incomingMessages.TryDequeue(out _))
            {
            }
        }

#if BROWSER_WASM
        // v0.7.9 login-batch (parity with CUO): when set, Send() appends the
        // (already-encrypted) packet bytes to this buffer instead of firing an
        // immediate per-packet SendAsync. BeginBatch()/EndBatchAndFlush() wrap
        // the login handshake's Send_Seed + Send_FirstLogin so they leave as ONE
        // WS message = the 83-byte 0xEF+0x80 bundle CUO sends. The proxy's
        // pattern-b-retry (recovers ModernUO's silent-FIN io_uring quirk) only
        // buffers+replays that batched form; TUO's previous per-Send immediate
        // SendAsync put the 21-byte seed ALONE on the wire (proxy retryBufSet=
        // false) → a silent FIN killed login with no possible retry → the client
        // sat on "Verifying Account" forever. Touched only from
        // OnNetClientConnected (main thread), so it preserves the iter53
        // single-writer-to-_ws invariant.
        private System.IO.MemoryStream _wasmLoginBatch;

        public void BeginBatch()
        {
            _wasmLoginBatch = new System.IO.MemoryStream(128);
        }

        public void EndBatchAndFlush()
        {
            System.IO.MemoryStream batch = _wasmLoginBatch;
            _wasmLoginBatch = null;
            if (batch == null || batch.Length == 0 || !IsConnected)
            {
                return;
            }
            byte[] arr = batch.ToArray();
            _ = _socket.SendAsync(arr, 0, arr.Length);
        }
#endif

        public void Send(Span<byte> message, bool ignorePlugin = false, bool skipEncryption = false)
        {
            if (!IsConnected || message is [])
            {
                return;
            }

            if (!ignorePlugin && Plugin.Enabled && !Plugin.ProcessSendPacket(ref message))
            {
                return;
            }

            if (message.IsEmpty)
                return;

            PacketLogger.Default?.Log(message, true);

            if (!skipEncryption)
            {
                EncryptionHelper.Instance?.Encrypt(!_isCompressionEnabled, message, message, message.Length);
            }

#if BROWSER_WASM
            // v0.7.9 iter 53: bypass _sendStream + NetworkLoopAsync's
            // BG ProcessSendAsync entirely. EnterWorld queues 6+ Sends
            // (Send_GameWindowSize, Send_Language, Send_ClientVersion,
            // SingleClick, Send_SkillsRequest, Send_ShowPublicHouseContent,
            // Send_ToPlugins_AllSkills, Send_ToPlugins_AllSpells) in
            // quick succession on the same Update tick. With the
            // BG-loop drain path, those 6+ packets land on a Mercury
            // MT thread different from main, racing with the BG
            // ReceiveLoopAsync's _ws.ReceiveAsync await — the JS
            // WebSocket handle is touched from THREE threads
            // simultaneously, which appears to be the trigger of the
            // "Uncaught [object WebAssembly.Exception]" trap that
            // iter 51's GetTileZ cache did NOT fix (the trap still
            // fires after the first 0x1B EnterWorld dispatch and the
            // first chunk's first tile insert).
            //
            // Mirror CUO's WebSocketWrapper.SendCopyAsync pattern:
            // make Send() fire-and-forget — synchronously call
            // _socket.SendAsync on the calling thread (main). Main
            // remains single-writer to _ws; BG ReceiveLoopAsync is
            // single-reader. CUO production runs this way every day
            // without any explicit synchronization.
            byte[] copy = new byte[message.Length];
            message.CopyTo(copy);
            if (_wasmLoginBatch != null)
            {
                // Coalesce into the login bundle (flushed once by EndBatchAndFlush).
                _wasmLoginBatch.Write(copy, 0, copy.Length);
            }
            else
            {
                _ = _socket.SendAsync(copy, 0, copy.Length);
            }
            Statistics.TotalBytesSent += (uint)message.Length;
            Statistics.TotalPacketsSent++;
            return;
#endif

            lock (_sendStream)
            {
                _sendStream.Enqueue(message);
            }

            Statistics.TotalBytesSent += (uint)message.Length;
            Statistics.TotalPacketsSent++;
        }

        private void ProcessEncryption(Span<byte> buffer)
        {
            if (!_isCompressionEnabled)
                return;

            EncryptionHelper.Instance?.Decrypt(buffer, buffer, buffer.Length);
        }

        private async Task ProcessSendAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected)
                return;

            byte[] sendingBuffer = null;
            int bytesToSend = 0;

            try
            {
                lock (_sendStream)
                {
                    if (_sendStream.Length > 0)
                    {
                        sendingBuffer = ArrayPool<byte>.Shared.Rent(4096); //= new byte[4096];

                        int size = Math.Min(sendingBuffer.Length, _sendStream.Length);

                        bytesToSend = _sendStream.Dequeue(sendingBuffer, 0, size);
                    }
                }

                if (bytesToSend > 0 && sendingBuffer != null)
                {
#if BROWSER_WASM
                    if (CUOEnviroment.Debug) Log.Trace($"[send-debug] S1: pre-SendAsync bytes={bytesToSend}");
                    await _socket.SendAsync(sendingBuffer, 0, bytesToSend, cancellationToken);
                    if (CUOEnviroment.Debug) Log.Trace($"[send-debug] S2: post-SendAsync bytes={bytesToSend}");
#else
                    await _socket.SendAsync(sendingBuffer, 0, bytesToSend, cancellationToken);
#endif
                    ArrayPool<byte>.Shared.Return(sendingBuffer);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error in ProcessSendAsync: {ex.GetType().FullName}: {ex.Message}\n{ex}");
                Disconnected?.Invoke(this, SocketError.SocketError);
            }
        }

        private Span<byte> DecompressBuffer(Span<byte> buffer)
        {
            if (!_isCompressionEnabled)
                return buffer;

            int size = 65536;

            if (!_huffman.Decompress(buffer, _uncompressedBuffer, ref size))
            {
                _ = Disconnect();
                Disconnected?.Invoke(this, SocketError.SocketError);

                return Span<byte>.Empty;
            }

            return _uncompressedBuffer.AsSpan(0, size);
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            try
            {
                _networkTask?.Wait(5000);
            }
            catch { }
            _socket?.Dispose();
        }
    }
}
