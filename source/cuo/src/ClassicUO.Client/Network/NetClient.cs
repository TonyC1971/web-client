// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Network.Encryption;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using ClassicUO.Network.Socket;

namespace ClassicUO.Network
{
    internal sealed class NetClient
    {
        private const int BUFF_SIZE = 0x10000;

        private readonly byte[] _compressedBuffer = new byte[4096];
        private readonly byte[] _uncompressedBuffer = new byte[BUFF_SIZE];
        private readonly byte[] _sendingBuffer = new byte[4096];
        private readonly Huffman _huffman = new Huffman();
        private bool _isCompressionEnabled;
        private uint? _localIP;
        private readonly CircularBuffer _sendStream;
        private SocketWrapper _socket = null;
        private SocketWrapperType? _socketType;


        public NetClient()
        {
            Statistics = new NetStatistics(this);
            _sendStream = new CircularBuffer();
        }

        public static NetClient Socket { get; private set; } = new();

        public EncryptionType Load(ClientVersion clientVersion, EncryptionType encryption)
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


        public bool IsConnected => _socket != null && _socket.IsConnected;
        public NetStatistics Statistics { get; }
        public EncryptionHelper? Encryption { get; private set; }
        public PacketsTable PacketsTable { get; private set; }

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
                        Log.Error($"error while retriving local endpoint address: \n{ex}");

                        _localIP = 0x100007f;
                    }
                }

                return _localIP.Value;
            }
        }


        public event EventHandler Connected;
        public event EventHandler<SocketError> Disconnected;

        private void SetupSocket(SocketWrapperType wrapperType)
        {
            _socket?.Dispose();

            _socket = wrapperType switch
            {
                SocketWrapperType.TcpSocket => new TcpSocketWrapper(),
                SocketWrapperType.WebSocket => new WebSocketWrapper(),
                _ => throw new ArgumentOutOfRangeException(nameof(wrapperType), wrapperType, null)
            };

            _socket.OnConnected += (o, e) =>
            {
                Statistics.Reset();
                Connected?.Invoke(this, EventArgs.Empty);
            };

            // Reset the compression flag on ANY socket-side teardown —
            // not just explicit Disconnect(). The async WebSocket
            // error path doesn't route through Disconnect(), so on
            // wasm a game-phase drop used to leave _isCompressionEnabled
            // = true. The next login attempt then called Send_FirstLogin
            // with the wrong encryption branch and got stuck at
            // "Verifying Account" (bug O39).
            _socket.OnDisconnected += (_, _) => {
                _isCompressionEnabled = false;
                Disconnected?.Invoke(this, SocketError.Success);
            };
            _socket.OnError += (_, e) => {
                _isCompressionEnabled = false;
                Disconnected?.Invoke(this, e);
            };
        }

        public void Connect(string ip, ushort port)
        {
            _sendStream.Clear();
            _huffman.Reset();
#if BROWSER_WASM
            // Defense-in-depth against the "logout + relogin with a
            // different account hangs at Entering Britannia" symptom
            // reported 2026-04-24. OnDisconnected / OnError already
            // reset `_isCompressionEnabled = false`, but the WebSocket
            // close is fire-and-forget (ContinueWith cancel) — if the
            // user drives a new Connect quickly enough, the async
            // close-complete hasn't fired the event yet. Pin the flag
            // false at the start of every Connect so FirstLogin always
            // reaches the login server un-compressed + un-encrypted as
            // the protocol requires, no matter what state leaked from
            // the previous session.
            _isCompressionEnabled = false;
#endif
            Statistics.Reset();

            if (string.IsNullOrEmpty(ip))
                throw new ArgumentNullException(nameof(ip));

            var isWebsocketAddress = ip.ToLowerInvariant().Substring(0, 2) is "ws" or "wss";
#if BROWSER_WASM
            // On wasm the proxy listens at ws://host:port/ws — the path
            // is part of the URI. The desktop formula `{ip}:{port}` splices
            // the port between host and path (ws://host/ws:8080), which
            // `Uri.TryCreate` misparses. Instead, if `ip` already contains
            // a scheme, treat it as the full URI and only overwrite the
            // port when it's missing.
            //
            // Also: after the login phase the 0x8C relay packet hands us
            // the game-server IP in bare form (e.g. "127.0.0.1"). Our
            // proxy rewrites that address to point back at itself, but
            // the scheme is lost. On wasm any connection needs to be
            // WebSocket (the browser sandbox blocks raw TCP), so if the
            // sticky _socketType is WebSocket, force the scheme + path
            // from the original Settings.IP URL so the game-phase
            // reconnect goes to the same /ws endpoint.
            Uri uri;
            if (isWebsocketAddress)
            {
                if (!Uri.TryCreate(ip, UriKind.Absolute, out uri))
                    throw new UriFormatException($"NetClient::Connect() invalid Uri {ip}");
                if (uri.IsDefaultPort && port != 0)
                {
                    var ub = new UriBuilder(uri) { Port = port };
                    uri = ub.Uri;
                }
            }
            else if (_socketType == SocketWrapperType.WebSocket)
            {
                // Game-phase reconnect after 0x8C relay. Reuse the
                // proxy host+path from Settings, replace the host
                // with whatever the 0x8C target says (the proxy
                // rewrites it anyway, so this is usually the same
                // machine) and keep the ws:// scheme + /ws path.
                var settingsUri = new Uri(Settings.GlobalSettings.IP);
                var ub = new UriBuilder(settingsUri) { Host = ip, Port = port };
                uri = ub.Uri;
            }
            else
            {
                var addr0 = $"tcp://{ip}:{port}";
                if (!Uri.TryCreate(addr0, UriKind.RelativeOrAbsolute, out uri))
                    throw new UriFormatException($"NetClient::Connect() invalid Uri {addr0}");
            }
#else
            var addr = $"{(isWebsocketAddress ? "" : "tcp://")}{ip}:{port}";

            if (!Uri.TryCreate(addr, UriKind.RelativeOrAbsolute, out var uri))
                throw new UriFormatException($"NetClient::Connect() invalid Uri {addr}");
#endif

            Log.Trace($"Connecting to {uri}");

            // First connected socket sets the type for any future sockets.
            // This prevents the client from swapping from WS -> TCP on game server login
            SetupSocket(_socketType ??= isWebsocketAddress ? SocketWrapperType.WebSocket : SocketWrapperType.TcpSocket);
            _socket.Connect(uri);
        }

        public void Disconnect()
        {
            _isCompressionEnabled = false;
            Statistics.Reset();
            // Null guard — reachable from LoginScene.StepBack paths
            // before any server has been selected, at which point
            // SetupSocket has never run and _socket is null. Desktop
            // never hit this because login flow was serialised
            // differently. Fixes bug O41.
            _socket?.Disconnect();
        }

        public void EnableCompression()
        {
            _isCompressionEnabled = true;
            _huffman.Reset();
            _sendStream.Clear();
        }


        public ArraySegment<byte> CollectAvailableData()
        {
            if (_socket == null)
            {
                return ArraySegment<byte>.Empty;
            }

            try
            {
                var size = _socket.Read(_compressedBuffer);

                if (size <= 0)
                {
                    return ArraySegment<byte>.Empty;
                }

                Statistics.TotalBytesReceived += (uint)size;

                var segment = new ArraySegment<byte>(_compressedBuffer, 0, size);
                var span = _compressedBuffer.AsSpan(0, size);

                ProcessEncryption(span);

                return DecompressBuffer(segment);
            }
            catch (SocketException ex)
            {
                Log.Error("socket error when receving:\n" + ex);

                Disconnect();
                Disconnected?.Invoke(this, ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Log.Error("main exception:\n" + ex);
                    Log.Error("socket error when receving:\n" + socketEx);

                    Disconnect();
                    Disconnected?.Invoke(this, socketEx.SocketErrorCode);
                }
                else
                {
                    Log.Error("fatal error when receving:\n" + ex);

                    Disconnect();
                    Disconnected?.Invoke(this, SocketError.SocketError);

                    throw;
                }
            }

            return ArraySegment<byte>.Empty;
        }

        public void Flush()
        {
            ProcessSend();
            Statistics.Update();
        }

        public void Send(Span<byte> message, bool ignorePlugin = false, bool skipEncryption = false)
        {
            if (!IsConnected || message.IsEmpty)
            {
                return;
            }

            if (!ignorePlugin && !Plugin.ProcessSendPacket(ref message))
            {
                return;
            }

            if (message.IsEmpty)
                return;

            PacketLogger.Default?.Log(message, true);

            if (!skipEncryption)
            {
                Encryption?.Encrypt(!_isCompressionEnabled, message, message, message.Length);
            }

            lock (_sendStream)
            {
                //_socket.Send(data, 0, length);
                _sendStream.Enqueue(message);
            }

            Statistics.TotalBytesSent += (uint)message.Length;
            Statistics.TotalPacketsSent++;
        }

        private void ProcessEncryption(Span<byte> buffer)
        {
            if (!_isCompressionEnabled)
                return;

            Encryption?.Decrypt(buffer, buffer, buffer.Length);
        }

        private void ProcessSend()
        {
            if (!IsConnected)
                return;

            try
            {
                lock (_sendStream)
                {
                    while (_sendStream.Length > 0)
                    {
                        var read = _sendStream.Dequeue(_sendingBuffer, 0, _sendingBuffer.Length);

                        if (read <= 0)
                        {
                            break;
                        }

                        _socket.Send(_sendingBuffer, 0, read);
                    }
                }
            }
            catch (SocketException ex)
            {
                Log.Error("socket error when sending:\n" + ex);

                Disconnect();
                Disconnected?.Invoke(this, ex.SocketErrorCode);
            }
            catch (Exception ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Log.Error("main exception:\n" + ex);
                    Log.Error("socket error when sending:\n" + socketEx);

                    Disconnect();
                    Disconnected?.Invoke(this, socketEx.SocketErrorCode);
                }
                else
                {
                    Log.Error("fatal error when sending:\n" + ex);

                    Disconnect();
                    Disconnected?.Invoke(this, SocketError.SocketError);

                    throw;
                }
            }
        }

        private ArraySegment<byte> DecompressBuffer(ArraySegment<byte> buffer)
        {
#if BROWSER_WASM
            // Path C (v0.1.9): for encrypt=none shards (ModernUO) the proxy
            // does the Huffman decode server-side; the wasm client receives
            // already-decoded bytes and skips its decoder. For ENCRYPTED
            // shards (Sphere with USECRYPT=1) the proxy can't see plaintext,
            // so it can't Huffman-decompress upstream of the wasm client —
            // the wasm client must run the desktop Huffman path itself.
            // Falls through to the standard path below when encryption is
            // active. See docs/ENCRYPTION.md for the full flow.
            if (Encryption == null || Encryption.EncryptionType == ClassicUO.Network.Encryption.EncryptionType.NONE)
            {
                return buffer;
            }
#endif
            if (!_isCompressionEnabled)
                return buffer;

            var size = 65536;
            if (!_huffman.Decompress(buffer, _uncompressedBuffer, ref size))
            {
                Disconnect();
                Disconnected?.Invoke(this, SocketError.SocketError);

                return ArraySegment<byte>.Empty;
            }

            return new ArraySegment<byte>(_uncompressedBuffer, 0, size);
        }

    }
}