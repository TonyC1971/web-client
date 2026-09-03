using System;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Network.Encryption;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network
{
    public class LoginHandshake : IDisposable
    {
        /// <summary>
        /// LoginHandshake supports only one connection. Trying to connect multiple times in one client would need a rework.
        /// </summary>
        public static LoginHandshake Instance {
            get
            {
                if (field == null)
                    field = new();

                return field;
            }

            private set;
        }

        // How long (ms) to wait on the server during a handshake stage before giving up.
        // A reconnect that lands while the server is still starting up can complete the TCP
        // connect but never receive a reply, leaving us pinned in VerifyingAccount with no way
        // out. This watchdog forces a fallback so the reconnect loop can retry.
        private const int HANDSHAKE_TIMEOUT_MS = 10000;

        private ushort _retries;
        private int _reconnectTryCounter = 1;
        private long _reconnectTime;
        private long _handshakeTimeout;
        private bool _isDisposed;
        private uint _pingTime;

        private LoginHandshake() { }

        public LoginSteps CurrentLoginStep { get; private set; } = LoginSteps.Main;
        public ServerListEntry[] Servers { get; private set; }
        public CityInfo[] Cities { get; set; }
        public string[] Characters { get; private set; }
        public byte ServerIndex { get; private set; }

        // When the user steps back from character selection while 'Skip Server Select' is enabled,
        // we need to suppress the auto-skip once so they actually land on the server selection screen
        // instead of being bounced straight back to character selection.
        public bool BypassServerSelectSkipOnce { get; set; }
        public static string Account { get; private set; }
        private static string Password { get; set; }
        public static string IP { get; set; }
        public static ushort Port { get; set; }
        public (int min, int max) LoginDelay { get; private set; }
        public bool ShouldReconnect { get; set; }
        public static bool Reconnect { get; set; }
        public ushort LastServerNum { get; private set; }
        public string LastServerName { get; private set; }
        public byte ErrorPacket { get; private set; } = byte.MaxValue;
        public byte ErrorCode { get; private set; } = byte.MaxValue;
        public string ErrorMessage { get; private set; } = string.Empty;
        public uint CharacterListFlags { get; private set; }

        public event EventHandler<LoginSteps> LoginStepChanged;
        public event EventHandler<SocketError> ConnectionFailed;
        public event EventHandler<string> ErrorOccurred;

        public void Connect(string account, string password, string ip, ushort port)
        {
            if (CurrentLoginStep == LoginSteps.Connecting)
            {
                return;
            }

            Account = account;
            Password = password;
            IP = ip;
            Port = port;

            Log.TraceDebug($"[HandShake] Start login to: {IP},{Port}");

            if (!Reconnect)
            {
                SetLoginStep(LoginSteps.Connecting);
            }

            AsyncNetClient.Socket.Connected -= OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket?.Disconnect();
            AsyncNetClient.Socket = new AsyncNetClient();
            AsyncNetClient.Socket.Connected += OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected += OnNetClientDisconnected;
            System.Threading.Tasks.Task<bool> status = AsyncNetClient.Socket.Connect(ip, port);
        }

        public void Disconnect()
        {
            Log.TraceDebug("[HandShake] Disconnecting...");
            AsyncNetClient.Socket.Connected -= OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket?.Disconnect();
        }

        private void SetError(byte packet = byte.MaxValue, byte code = byte.MaxValue, string msg = "")
        {
            ErrorPacket = packet;
            ErrorCode = code;
            ErrorMessage = msg;
        }

        public void SelectServer(byte index, string serverName)
        {
            Log.TraceDebug($"[HandShake] Selecting server {serverName}.");
            if (CurrentLoginStep == LoginSteps.ServerSelection)
            {
                for (byte i = 0; i < Servers.Length; i++)
                {
                    if (Servers[i].Index == index)
                    {
                        ServerIndex = i;
                        break;
                    }
                }

                World.Instance.ServerName = serverName;
                LastServerNum = (ushort)(1 + ServerIndex);
                LastServerName = Servers[ServerIndex].Name;

                SetLoginStep(LoginSteps.LoginInToServer);

                AsyncNetClient.Socket.Send_SelectServer(index);
            }
        }

        /// <summary>
        /// Call in Update() of login scene
        /// </summary>
        /// <param name="reconnectTime">In ms</param>
        public void HandleReconnect(int reconnectTime)
        {
            // Note: we intentionally don't gate on IsConnected here. On PopUpMessage/Main with
            // Reconnect set we're not in a live session by definition, and Socket.Connected only
            // reflects the last I/O so a timed-out/half-open socket can report a stale 'true' and
            // wedge the retry loop forever. Connect() tears down and replaces the socket anyway,
            // and _reconnectTime enforces the delay between attempts.
            if (Reconnect && (CurrentLoginStep == LoginSteps.PopUpMessage || CurrentLoginStep == LoginSteps.Main))
            {
                if (_reconnectTime >= Time.Ticks)
                    return;

                Log.TraceDebug($"[HandShake] Reconnecting...");
                if (!string.IsNullOrEmpty(Account))
                {
                    Connect(Account, Password, IP, Port);
                }
                else
                {
                    Reconnect = false;
                }

                if (reconnectTime < 1000)
                {
                    reconnectTime = 1000;
                }

                // Exponential backoff (2026-07-23): a fixed ~1s retry STORMS the
                // shard when the handshake keeps failing — a low-RAM / slow
                // machine (GC pauses, tab throttling) or a lossy link misses the
                // HANDSHAKE_TIMEOUT_MS window over and over, which is a
                // self-inflicted DoS on the server (observed hammering Memento
                // ~19 reconnects in 8 min). Double the delay per consecutive
                // attempt, capped at 30s; reset on a successful handshake
                // (ServerListReceived).
                int backoffShift = System.Math.Min(_reconnectTryCounter - 1, 5);
                long backoffMs = System.Math.Min((long)reconnectTime << backoffShift, 30000L);

                _reconnectTime = (long)Time.Ticks + backoffMs;
                _reconnectTryCounter++;
            }
        }

        public void ServerListReceived(ref StackDataReader p)
        {
            Log.TraceDebug($"[HandShake] Got server list.");
            _reconnectTryCounter = 1;   // handshake succeeded → reset the reconnect backoff
            byte flags = p.ReadUInt8();
            ushort count = p.ReadUInt16BE();
            DisposeAllServerEntries();
            Servers = new ServerListEntry[count];

            for (ushort i = 0; i < count; i++)
            {
                Servers[i] = ServerListEntry.Create(ref p);
            }

            SetLoginStep(LoginSteps.ServerSelection);

            if (BypassServerSelectSkipOnce)
            {
                // User explicitly navigated back to server selection, don't auto-skip this time.
                BypassServerSelectSkipOnce = false;
            }
            else if (Settings.GlobalSettings.SkipServerSelect && Servers.Length == 1 && CurrentLoginStep == LoginSteps.ServerSelection) //Double check server selection, the previous call may initiate auto login and already select one
            {
                SelectServer((byte)Servers[0].Index, Servers[0].Name);
                return;
            }
        }

        public event NotifierEventHandler ReceiveCharacterListNotifier;
        public void ReceiveCharacterList(ref StackDataReader p)
        {
            Log.TraceDebug($"[HandShake] Got character list.");
            ParseCharacterList(ref p);
            ParseCities(ref p);
            CharacterListFlags = p.ReadUInt32BE();

            SetLoginStep(LoginSteps.CharacterSelection);
            ReceiveCharacterListNotifier?.Invoke();
        }

        public event NotifierEventHandler UpdateCharacterListNotifier;
        public void UpdateCharacterList(ref StackDataReader p)
        {
            Log.TraceDebug($"[HandShake] Updated character list.");
            ParseCharacterList(ref p);

            if (CurrentLoginStep != LoginSteps.PopUpMessage)
            {
                SetError();
            }

            SetLoginStep(LoginSteps.CharacterSelection);
            UpdateCharacterListNotifier?.Invoke();
        }

        public void HandleErrorCode(ref StackDataReader p)
        {
            byte code = p.ReadUInt8();
            SetError(p[0], code);
            SetLoginStep(LoginSteps.PopUpMessage);
            LoginDelay = default;
        }

        public void HandleLoginDelayPacket(ref StackDataReader p)
        {
            byte delay = p.ReadUInt8();
            LoginDelay = ((delay - 1) * 10, delay * 10);
        }

        public void HandleRelayServerPacket(ref StackDataReader p, bool ignoreRelay)
        {
            Log.TraceDebug($"[HandShake] Got server relay packet.");
            uint ip = p.ReadUInt32LE(); // use LittleEndian here
            ushort port = p.ReadUInt16BE();
            uint seed = p.ReadUInt32BE();

            byte[] ipBytes = new byte[]
            {
                    (byte)(ip & 0xFF),
                    (byte)((ip >> 8) & 0xFF),
                    (byte)((ip >> 16) & 0xFF),
                    (byte)((ip >> 24) & 0xFF)
                    };

            string finalIP = new IPAddress(ipBytes).ToString();

            if (ignoreRelay || ip == 0)
            {
                Log.TraceDebug("Ignoring relay server packet IP address");
                finalIP = IP;
                port = Port;
            }

            AfterRelayConnect(finalIP, port, seed);
        }

        public int GetServerIndexByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Servers == null) return -1;

            for (int i = 0; i < Servers.Length; i++)
                if (Servers[i].Name.Equals(name, StringComparison.InvariantCultureIgnoreCase))
                    return Servers[i].Index;

            return -1;
        }

        public CityInfo GetCity(int index)
        {
            if (Cities != null && index < Cities.Length)
            {
                return Cities[index];
            }

            return null;
        }

        public void SendSelectCharacter(uint index)
        {
            if (Characters != null && index < (uint)Characters.Length)
            {
                AsyncNetClient.Socket.Send_SelectCharacter(index, Characters[index], AsyncNetClient.Socket.LocalIP);
                SetLoginStep(LoginSteps.EnteringBritania);
            }
            else
            {
                Log.Warn($"[HandShake] Invalid character index {index}.");
            }
        }

        public void SendDeleteCharacter(uint index)
        {
            if (CurrentLoginStep == LoginSteps.CharacterSelection)
            {
                AsyncNetClient.Socket.Send_DeleteCharacter((byte)index, AsyncNetClient.Socket.LocalIP);
            }
        }

        public void SendPing()
        {
            if ((CurrentLoginStep == LoginSteps.CharacterCreation || CurrentLoginStep == LoginSteps.CharacterSelection) && Time.Ticks > _pingTime)
            {
                // Note that this will not be an ICMP ping, so it's better that this *not* be affected by -no_server_ping.

                if (AsyncNetClient.Socket.IsConnected)
                {
                    AsyncNetClient.Socket.Statistics.SendPing();
                }

                _pingTime = Time.Ticks + 60000;
            }
        }

        internal void SetLoginStep(LoginSteps step)
        {
            _pingTime = Time.Ticks + 60000;

            // Arm the handshake watchdog while we're waiting on the server during the initial
            // handshake, disarm it otherwise. Each step change re-arms the timer, so a stage only
            // times out if it stalls with no progress. Scoped to Connecting/VerifyingAccount to
            // avoid interfering with the relay/server-selection flow which has its own retry logic.
            if (step is LoginSteps.Connecting or LoginSteps.VerifyingAccount)
                _handshakeTimeout = (long)Time.Ticks + HANDSHAKE_TIMEOUT_MS;
            else
                _handshakeTimeout = 0;

            Log.TraceDebug($"[HandShake] Set login step to {step}.");
            CurrentLoginStep = step;
            LoginStepChanged?.Invoke(this, step);
        }

        /// <summary>
        /// Call in Update() of the login scene. If a handshake stage stalls (e.g. a reconnect that
        /// connects to a server which is still restarting and never replies), force a disconnect and
        /// fall back to the popup so the reconnect loop can retry instead of getting stuck.
        /// </summary>
        public void CheckHandshakeTimeout()
        {
            if (_handshakeTimeout == 0 || Time.Ticks < _handshakeTimeout)
                return;

            _handshakeTimeout = 0;
            Log.Warn($"[HandShake] Handshake timed out at step {CurrentLoginStep}, aborting connection attempt.");

            Disconnect();
            HandleConnectionFailure(SocketError.TimedOut);
        }

        private void OnNetClientConnected(object sender, EventArgs e)
        {
            Log.Info("[HandShake] Connected!");
#if BROWSER_WASM
            // v0.7.9 iter 25: stack-by-stack surface for the
            // [object WebAssembly.Exception] that appears post-Connect.
            // Without try/catch the inner exception is swallowed and
            // playwright only sees the opaque WebAssembly wrapper.
            try
            {
                Log.Trace("[hs-debug] HS1: pre-SetLoginStep");
                SetLoginStep(LoginSteps.VerifyingAccount);
                Log.Trace("[hs-debug] HS2: post-SetLoginStep");

                uint address = AsyncNetClient.Socket.LocalIP;
                Log.Trace($"[hs-debug] HS3: LocalIP=0x{address:X}");

                AsyncNetClient.Encryption?.Initialize(true, address);
                Log.Trace("[hs-debug] HS4: post-Encryption.Initialize");

                // v0.7.9 login-batch: coalesce Send_Seed (0xEF, 21B) + Send_FirstLogin
                // (0x80, 62B) into ONE 83-byte WS message, exactly as CUO does. The proxy
                // can only retry ModernUO's silent-FIN io_uring quirk on that batched
                // bundle; sending the seed alone left TUO stuck on "Verifying Account".
                AsyncNetClient.Socket.BeginBatch();
                if (Client.Game.UO.Version >= ClientVersion.CV_6040)
                {
                    uint clientVersion = (uint)Client.Game.UO.Version;
                    byte major = (byte)(clientVersion >> 24);
                    byte minor = (byte)(clientVersion >> 16);
                    byte build = (byte)(clientVersion >> 8);
                    byte extra = (byte)clientVersion;
                    Log.Trace($"[hs-debug] HS5: pre-Send_Seed v={major}.{minor}.{build}.{extra}");
                    AsyncNetClient.Socket.Send_Seed(address, major, minor, build, extra);
                    Log.Trace("[hs-debug] HS6: post-Send_Seed");
                }
                else
                {
                    Log.Trace("[hs-debug] HS5': pre-Send_Seed_Old");
                    AsyncNetClient.Socket.Send_Seed_Old(address);
                    Log.Trace("[hs-debug] HS6': post-Send_Seed_Old");
                }

                Log.Trace("[hs-debug] HS7: pre-Send_FirstLogin");
                AsyncNetClient.Socket.Send_FirstLogin(Account, Password);
                AsyncNetClient.Socket.EndBatchAndFlush();   // flush seed+login as one 83B bundle
                Log.Trace("[hs-debug] HS8: post-Send_FirstLogin (batched)");
            }
            catch (Exception ex)
            {
                Log.Error($"[hs-debug] OnNetClientConnected EXCEPTION: {ex.GetType().FullName}: {ex.Message}\n{ex}");
            }
#else
            SetLoginStep(LoginSteps.VerifyingAccount);

            uint address = AsyncNetClient.Socket.LocalIP;

            AsyncNetClient.Encryption?.Initialize(true, address);

            if (Client.Game.UO.Version >= ClientVersion.CV_6040)
            {
                uint clientVersion = (uint)Client.Game.UO.Version;

                byte major = (byte)(clientVersion >> 24);
                byte minor = (byte)(clientVersion >> 16);
                byte build = (byte)(clientVersion >> 8);
                byte extra = (byte)clientVersion;

                AsyncNetClient.Socket.Send_Seed(address, major, minor, build, extra);
            }
            else
            {
                AsyncNetClient.Socket.Send_Seed_Old(address);
            }

            AsyncNetClient.Socket.Send_FirstLogin(Account, Password);
#endif
        }

        private void OnNetClientDisconnected(object sender, SocketError e)
        {
            Log.Warn($"Socket disconnected - {e.ToString()}");

            if (CurrentLoginStep == LoginSteps.CharacterCreation)
            {
                return;
            }

            if (e == SocketError.Success)
            {
                return;
            }

            HandleConnectionFailure(e);
        }

        private void HandleConnectionFailure(SocketError e)
        {
            Characters = null;
            DisposeAllServerEntries();

            if (ShouldReconnect)
            {
                Reconnect = true;
                SetError(msg: string.Format(
                                             ResGeneral.ReconnectPleaseWait01,
                                             _reconnectTryCounter,
                                             StringHelper.AddSpaceBeforeCapital(e.ToString())
                                         ));
            }
            else
            {
                SetError(msg: string.Format(
                                                  ResGeneral.ConnectionLost0,
                                                  StringHelper.AddSpaceBeforeCapital(e.ToString())
                                              ));
            }

            SetLoginStep(LoginSteps.PopUpMessage);
            ConnectionFailed?.Invoke(this, e);
        }

        private async void AfterRelayConnect(string ip, ushort port, uint seed)
        {
            AsyncNetClient.Socket.Connected -= OnNetClientConnected;
            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
#if BROWSER_WASM
            // v0.7.9 iter 44 (port from CUO LoginScene.cs:758-804): on
            // browser-wasm, calling `.Wait()` on a Task blocks the main
            // thread, and the WebSocket close + new-WebSocket-open both
            // need the JavaScript event loop to make progress. Blocking
            // main → JS event loop stalls → WS state never transitions
            // → .Wait() never returns → game loop freezes silently
            // ("phantom freeze" — no exception, just no more update
            // ticks). Symptom of the desktop-pattern bug: server sends
            // 0xA8 ServerList + 0x8C ConnectToGameServer, client
            // dequeues them on a single Update tick, ServerListReceived
            // fires SelectServer auto-login, then HandleRelayServerPacket
            // dispatches → AfterRelayConnect → Disconnect().Wait() →
            // freeze. Last update-tick logged was frame 7140 at the
            // same second the relay arrived (iter 43 deploy probe
            // build 20260528225707).
            //
            // Fix: fire-and-forget the new Connect AND wire a one-shot
            // Connected handler that fires the game-phase seed +
            // Send_SecondLogin once the new WS actually opens. Pattern
            // matches CUO's WebSocketWrapper path verbatim.
            AsyncNetClient.Socket.Disconnect();   // fire-and-forget; no .Wait()

            // iter65 fix: defer the new game-phase socket+connect by one JS
            // event-loop turn so the OLD WebSocket finishes closing before the
            // NEW one opens. Opening the new game WS in the same tick the old
            // tears down makes it come up net_WebSockets_InvalidState on Mercury
            // WASM (the new WS's first ReceiveAsync sees it already Closed) ->
            // the client sends 0 bytes -> ModernUO idle-closes the relay ->
            // client stuck forever on "Verifying Account" / never enters the
            // game. Non-blocking await (NOT .Wait()/Task.Run, which froze in
            // iter43 / trapped in iter55). Proxy proof of the bug: the game
            // reconnect logged c2sCount=0 + "no c2s within 30000ms".
            await System.Threading.Tasks.Task.Delay(600);

            AsyncNetClient.Socket = new AsyncNetClient();

            _retries++;
            Log.TraceDebug($"[HandShake] Reconnecting to relay server (wasm, deferred)...");

            EventHandler onGamePhaseConnected = null;
            onGamePhaseConnected = (s, e) =>
            {
                AsyncNetClient.Socket.Connected -= onGamePhaseConnected;
                EncryptionHelper.Instance?.Initialize(false, seed);
                AsyncNetClient.Socket.EnableCompression();
                // v0.8.3: batch the game-phase 4-byte seed + Send_SecondLogin (0x91)
                // into ONE 69-byte WS message, exactly as CUO does, so the proxy's
                // pattern-b-retry can recover ModernUO's silent-FIN on the GAME socket
                // too. Sending them separately (the 4-byte seed ALONE) left the proxy
                // with retryBufSet=false → a silent FIN on the reconnect = stuck on
                // "Verifying Account" (flaky entry — it only failed when the silent FIN
                // happened to hit the game socket; v0.8.0/0.8.1 got lucky, v0.8.2 didn't).
                // Mirror of the login-phase batch in OnNetClientConnected. Flush BEFORE
                // the optional Send_TazUO so the bundle stays the 69-byte form the proxy
                // splits at byte 4.
                AsyncNetClient.Socket.BeginBatch();
                unsafe
                {
                    Span<byte> b = stackalloc byte[4]
                    {
                        (byte)(seed >> 24),
                        (byte)(seed >> 16),
                        (byte)(seed >> 8),
                        (byte)seed
                    };
                    AsyncNetClient.Socket.Send(b, true, true);
                }
                AsyncNetClient.Socket.Send_SecondLogin(Account, Password, seed);
                AsyncNetClient.Socket.EndBatchAndFlush();   // flush seed+0x91 as one 69B bundle
                Log.TraceDebug($"[HandShake] Sent second login (wasm one-shot, batched).");

                if (Settings.GlobalSettings.CustomServer == Settings.CustomServers.Eventine || Settings.GlobalSettings.CustomServer == Settings.CustomServers.LOCAL_SERVER)
                    AsyncNetClient.Socket.Send_TazUO();

                // Re-subscribe Disconnected so a subsequent server
                // close fires the LoginScene's disconnect path.
                AsyncNetClient.Socket.Disconnected += OnNetClientDisconnected;
            };
            AsyncNetClient.Socket.Connected += onGamePhaseConnected;

            try
            {
                AsyncNetClient.Socket.Connect(ip, port);   // fire-and-forget Task — JS event loop owns completion
            }
            catch (Exception ex)
            {
                // Unsubscribe the one-shot so a future reconnect doesn't fire
                // both handlers (CUO O40 fix). Do NOT rethrow: AfterRelayConnect
                // is now async void, so an unhandled exception here would crash
                // the deputy. Log + let the disconnect/retry path recover.
                AsyncNetClient.Socket.Connected -= onGamePhaseConnected;
                Log.Error($"[HandShake] game-phase Connect threw: {ex}");
            }
#else
            AsyncNetClient.Socket.Disconnect().Wait();
            AsyncNetClient.Socket = new AsyncNetClient();

            _retries++;
            Log.TraceDebug($"[HandShake] Reconnecting to relay server...");
            AsyncNetClient.Socket.Connect(ip, port).Wait(3000);

            if (AsyncNetClient.Socket.IsConnected)
            {
                EncryptionHelper.Instance?.Initialize(false, seed);
                AsyncNetClient.Socket.EnableCompression();
                unsafe
                {
                    Span<byte> b = stackalloc byte[4]
                    {
                        (byte)(seed >> 24),
                        (byte)(seed >> 16),
                        (byte)(seed >> 8),
                        (byte)seed
                    };
                    AsyncNetClient.Socket.Send(b, true, true);
                }

                AsyncNetClient.Socket.Send_SecondLogin(Account, Password, seed);
                Log.TraceDebug($"[HandShake] Sent second login.");

                if (Settings.GlobalSettings.CustomServer == Settings.CustomServers.Eventine || Settings.GlobalSettings.CustomServer == Settings.CustomServers.LOCAL_SERVER)
                    AsyncNetClient.Socket.Send_TazUO();
            }
            else
            {
                Log.TraceDebug($"[HandShake] Failed to connect, trying again.");
                if (_retries > 5)
                {
                    _retries = 0;
                    SetError(msg: "Failed to connect to game server after multiple attempts.");
                    SetLoginStep(LoginSteps.PopUpMessage);
                    ErrorOccurred?.Invoke(this, ErrorMessage);
                    return;
                }

                AfterRelayConnect(ip, port, seed);
            }
#endif
        }

        private void ParseCharacterList(ref StackDataReader p)
        {
            int count = p.ReadUInt8();
            Characters = new string[count];

            for (ushort i = 0; i < count; i++)
            {
                Characters[i] = p.ReadASCII(30).TrimEnd('\0');
                p.Skip(30);
            }
        }

        private void ParseCities(ref StackDataReader p)
        {
            byte count = p.ReadUInt8();
            Cities = new CityInfo[count];

            bool isNew = Client.Game.UO.Version >= ClientVersion.CV_70130;

            Vector2[] oldtowns =
            {
                new(105, 130), new(245, 90),
                new(165, 200), new(395, 160),
                new(200, 305), new(335, 250),
                new(160, 395), new(100, 250),
                new(270, 130), new(0xFFFF, 0xFFFF)
            };

            for (int i = 0; i < count; i++)
            {
                CityInfo cityInfo;

                if (isNew)
                {
                    byte cityIndex = p.ReadUInt8();
                    string cityName = p.ReadASCII(32);
                    string cityBuilding = p.ReadASCII(32);
                    ushort cityX = (ushort)p.ReadUInt32BE();
                    ushort cityY = (ushort)p.ReadUInt32BE();
                    sbyte cityZ = (sbyte)p.ReadUInt32BE();
                    uint cityMapIndex = p.ReadUInt32BE();
                    uint cityDescription = p.ReadUInt32BE();
                    p.Skip(4);

                    cityInfo = new CityInfo
                    (
                        cityIndex,
                        cityName,
                        cityBuilding,
                        Client.Game.UO.FileManager.Clilocs.GetString((int)cityDescription),
                        cityX,
                        cityY,
                        cityZ,
                        cityMapIndex,
                        isNew
                    );
                }
                else
                {
                    byte cityIndex = p.ReadUInt8();
                    string cityName = p.ReadASCII(31);
                    string cityBuilding = p.ReadASCII(31);

                    cityInfo = new CityInfo
                    (
                        cityIndex,
                        cityName,
                        cityBuilding,
                        string.Empty,
                        (ushort)oldtowns[i % oldtowns.Length].X,
                        (ushort)oldtowns[i % oldtowns.Length].Y,
                        0,
                        0,
                        isNew
                    );
                }

                Cities[i] = cityInfo;
            }
        }

        private void DisposeAllServerEntries()
        {
            if (Servers != null)
            {
                for (int i = 0; i < Servers.Length; i++)
                {
                    if (Servers[i] != null)
                    {
                        Servers[i].Dispose();
                        Servers[i] = null;
                    }
                }

                Servers = null;
            }
        }

        public void Dispose()
        {
            Instance = null;

            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            AsyncNetClient.Socket.Disconnected -= OnNetClientDisconnected;
            AsyncNetClient.Socket.Connected -= OnNetClientConnected;

            DisposeAllServerEntries();
            Characters = null;
            Cities = null;
        }
    }

    public delegate void NotifierEventHandler();

    public class CityInfo
    {
        public CityInfo
        (
            int index,
            string city,
            string building,
            string description,
            ushort x,
            ushort y,
            sbyte z,
            uint map,
            bool isNew
        )
        {
            Index = index;
            City = city;
            Building = building;
            Description = description;
            X = x;
            Y = y;
            Z = z;
            Map = map;
            IsNewCity = isNew;
        }

        public readonly string Building;
        public readonly string City;
        public readonly string Description;
        public readonly int Index;
        public readonly bool IsNewCity;
        public readonly uint Map;
        public readonly ushort X, Y;
        public readonly sbyte Z;
    }
}
