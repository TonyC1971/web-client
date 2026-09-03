// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Platforms;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ClassicUO.Network.PacketHandlers;
using Myra;
using SDL3;
using static SDL3.SDL;
using Keyboard = ClassicUO.Input.Keyboard;
using Mouse = ClassicUO.Input.Mouse;
using ClassicUO.Game.UI.MyraWindows;

namespace ClassicUO
{
    internal unsafe class GameController : Microsoft.Xna.Framework.Game
    {
        private SDL_EventFilter _filter;

        private bool _ignoreNextTextInput;
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _totalElapsed, _currentFpsTime;
        private uint _totalFrames;
#if BROWSER_WASM
        private uint _updateTickFrames;

        // [perf] frame-time monitor — ported from CUO GameController
        // (v0.3.30/v0.4.48) 2026-06-01 to give TUO the same long-frame
        // telemetry CUO has. Tracks the wall-clock delta between
        // consecutive Update() calls; when a frame spikes above
        // FRAME_LONG_MS it emits one `[perf] long-frame=Xms upd=… drw=…
        // gap=…` line through Console.WriteLine (silenced unless ?dev=2).
        // Allocation-free unless a long frame actually fires, so it's safe
        // to leave on in prod. This is what the smoke perf-frame-aggregate
        // check parses and what the operator's Memento ?dev=2 roam captures
        // to attribute the tirones (C# Update vs C# Draw vs browser GPU gap).
        private double _wasmLastUpdateWallMs = 0;
        private int _wasmLongFrameCount60s = 0;
        private double _wasmLongFrameWindowStartMs = 0;
        // 20ms = 1.2× a 60Hz rAF interval. Below this is unavoidable jitter
        // (browser scheduler, vsync); above is a real hitch (GC, audio
        // decode, atlas re-upload, chunk fill).
        private const double FRAME_LONG_MS = 20.0;
        // Wall-clock anchors set at the end of Update/Draw so the long-frame
        // log can split the spike into C#-Update / C#-Draw / js-gap buckets.
        public static long WasmUpdateEndMs;
        public static long WasmDrawEndMs;

        // v0.7.9 iter 22: DOM-event drain bridge. Without this, the JS
        // bridge `wasm_push_mouse_button` / `wasm_push_mouse_motion` /
        // `wasm_push_key` writes events into a C ring buffer in
        // source/webclient/native-shims/SDL3.c but NOTHING reads them
        // out — clicks land at the bridge (logged as `[bridge-click]`)
        // and die there. CUO drains the ring every Update tick via the
        // exact same pattern (see source/cuo/src/.../GameController.cs:
        // PumpWasmInput). Without the drain, TUO's LoginGump renders
        // but never receives input. Verified iter 21: render loop
        // running (3× [draw-tick] in 30 s) yet 5 post-click screenshots
        // byte-identical because the clicks never reached UIManager.
        private const int _wasmEventSize           = 128;
        private const int _wasmDrainBatch          = 32;
        private long      _wasmInputEventsDispatched = 0;

        [DllImport("SDL3", EntryPoint = "sdl3_drain_events", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int sdl3_drain_events(byte* buf, int max);

        private unsafe void PumpWasmInput()
        {
            byte* buf = stackalloc byte[_wasmDrainBatch * _wasmEventSize];
            while (true)
            {
                int n = sdl3_drain_events(buf, _wasmDrainBatch);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    SDL_Event* ev = (SDL_Event*)(buf + i * _wasmEventSize);
                    HandleSdlEvent(IntPtr.Zero, ev);
                    if (_wasmInputEventsDispatched == 0)
                    {
                        Log.Trace($"[pump-input] first DOM input event dispatched, type=0x{ev->type:X}");
                    }
                    _wasmInputEventsDispatched++;
                }
                if (n < _wasmDrainBatch) break;
            }
        }
#endif
        private UltimaBatcher2D _uoSpriteBatch;
        private bool _suppressedDraw;
        private Texture2D _background;
        private bool _pluginsInitialized;
        private Rectangle bufferRect = Rectangle.Empty;
        private bool _fullscreenBorderless;
        private RenderTarget2D _screenRenderTarget;
#if BROWSER_WASM
        // v0.7.9 iter 60: gate the screen-RT composite OFF on WASM. The
        // in-world frame already composites GameScene's _worldRenderTarget
        // (which renders 2162 objects correctly, MOJOSHADER_FLIP_RENDERTARGET
        // is effective) straight to the current target. Wrapping that in a
        // SECOND full-screen render-target (_screenRenderTarget) + composite
        // is the layer that froze the WebGL canvas in-world (screenshots
        // byte-identical t+10s→t+57s while GameScene.Draw ran at 60fps).
        // LoginScene presented fine through it because it's a light frame;
        // the triple-composite (worldRT→screenRT→backbuffer) in-world is
        // where the present stopped reaching the canvas. Rendering Scene +
        // UI directly to the backbuffer removes that layer. RenderScale is
        // 1.0 on WASM so the screen-RT's scaling purpose is moot here.
        private bool _useScreenRenderTarget = false;
#else
        private bool _useScreenRenderTarget = true; // Re-enabling to debug rendering issues
#endif

        private static Vector3 bgHueShader = new(0, 0, 0.3f);
        private bool drawScene;

#if DEBUG
        static GameController()
        {
            RegisterFnaLoggerListeners();
        }
#endif

        private static string DefaultWindowTitle => $"[TazUO - {CUOEnviroment.Version}]";

        public GameController(IPluginHost pluginHost)
        {
            GraphicManager = new GraphicsDeviceManager(this);

            GraphicManager.PreparingDeviceSettings += (sender, e) =>
            {
                e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage =
                    RenderTargetUsage.DiscardContents;
            };

            GraphicManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;

            Window.ClientSizeChanged += WindowOnClientSizeChanged;
            Window.AllowUserResizing = true;
            Window.Title = DefaultWindowTitle;
            IsMouseVisible = Settings.GlobalSettings.RunMouseInASeparateThread;

            IsFixedTimeStep = false; // Settings.GlobalSettings.FixedTimeStep;
            TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 250.0);
            PluginHost = pluginHost;
            bufferRect = new Rectangle(0, 0, GraphicManager.PreferredBackBufferWidth, GraphicManager.PreferredBackBufferHeight);

#if !BROWSER_WASM
            // v0.7.9: SDL_SetHint + SDL_StartTextInput throw
            // ArgumentNullException under Mercury MT's static SDL2 build
            // (P/Invoke `byte*` marshalling). CUO's GameController ctor
            // omits both calls entirely and boots cleanly. Touch-input
            // and on-screen keyboard aren't relevant to a web client
            // where the host page already provides DOM input bridging
            // (see main.js #p4d7 DOM input bridge attached to canvas).
            SDL.SDL_SetHint(SDL_HINT_ENABLE_SCREEN_KEYBOARD, "0");
            SDL.SDL_StartTextInput(Window.Handle);
#endif
        }

        public readonly float MinRenderScale = 0.1f;
        public readonly float MaxRenderScale = 1.75f;

        public float RenderScale
        {
            get;
            set => field = Math.Clamp(value, MinRenderScale, MaxRenderScale);
        } = 1f;

        public Scene Scene { get; private set; }
        public AudioManager Audio { get; private set; }
        public UltimaOnline UO { get; } = new UltimaOnline();
        public IPluginHost PluginHost { get; private set; }
        public GraphicsDeviceManager GraphicManager { get; }
        public readonly uint[] FrameDelay = new uint[2];
        public static int SupportedRefreshRate = 0;
        public event EventHandler<float> ScaleChanged;

        private readonly List<(uint, Action)> _queuedActions = new();

        public void EnqueueAction(uint time, Action action) => _queuedActions.Add((Time.Ticks + time, action));

        protected override void Initialize()
        {
            Log.Trace("[init-debug] I1: GameController.Initialize enter");
            MainThreadQueue.Load();
            Log.Trace("[init-debug] I2: MainThreadQueue.Load done");

            PreloadSettings();
            Log.Trace("[init-debug] I3: PreloadSettings done");
#if !BROWSER_WASM
            // v0.7.9: HiDef profile + ApplyChanges recreates the
            // GraphicsDevice — WebGL/Emscripten-SDL2 handles this poorly
            // under our WASM bring-up (CUO hit an OOB trap after FNA3D's
            // glsles3 log; TUO is downstream of the same FNA build).
            if (GraphicManager.GraphicsDevice.Adapter.IsProfileSupported(GraphicsProfile.HiDef))
            {
                GraphicManager.GraphicsProfile = GraphicsProfile.HiDef;
            }

            GraphicManager.ApplyChanges();
#endif
            Log.Trace("[init-debug] I4: HiDef/ApplyChanges done");

            SetRefreshRate(Settings.GlobalSettings.FPS);
            SupportedRefreshRate = Settings.GlobalSettings.FPS;
            Log.Trace("[init-debug] I5: SetRefreshRate done");

            try
            {
                _uoSpriteBatch = new UltimaBatcher2D(GraphicsDevice);
            }
            catch (Exception ex) when (Client.IsShaderCompileFailure(ex))
            {
                Client.ShowErrorMessage(Client.GraphicsShaderHelpMessage);
                throw; // preserve existing crash logging / report
            }
            Log.Trace("[init-debug] I6: UltimaBatcher2D done");

            _filter = HandleSdlEvent;
#if !BROWSER_WASM
            // v0.7.9: Mono-WASM's reverse-PInvoke marshalling for the
            // SDL_EventFilter delegate (-> native function pointer) traps
            // with a WASM OOB before the sdl_* stub is even reached.
            // Event routing to HandleSdlEvent will need a JS bridge later;
            // for now we skip the register so Initialize can complete and
            // the login scene can render. Mirrors source/cuo/.
            SDL_SetEventFilter(_filter, IntPtr.Zero);
#endif
            Log.Trace("[init-debug] I7: SetEventFilter skipped/done");

#if !BROWSER_WASM
            // v0.7.9: SDL_GetDisplayForWindow / SDL_GetCurrentDisplayMode
            // / Marshal.PtrToStructure go through the same P/Invoke path
            // that crashes under Mercury MT. The display refresh-rate
            // probe is purely informational (drives SupportedRefreshRate
            // which only gates FPS clamping); the FPS fallback we set
            // above from Settings.GlobalSettings.FPS is sufficient on WASM.
            uint displayId = SDL.SDL_GetDisplayForWindow(Window.Handle);
            nint displayMode = SDL.SDL_GetCurrentDisplayMode(displayId);
            if (displayMode != IntPtr.Zero)
            {
                // Marshal the pointer to the display mode structure
                SDL_DisplayMode mode = Marshal.PtrToStructure<SDL.SDL_DisplayMode>(displayMode);

                float refreshRate = mode.refresh_rate;
                if (refreshRate > 0)
                    SupportedRefreshRate = (int)refreshRate;
            }
#endif
            Log.Trace("[init-debug] I8: display-mode probe skipped/done");

            base.Initialize();
            Log.Trace("[init-debug] I9: base.Initialize done");
        }

        private void PreloadSettings()
        {
            bool platformDefault = PlatformHelper.IsLinux;
            _ = Client.Settings.GetAsyncOnMainThread(SettingsScope.Global, Constants.SqlSettings.MANAGED_ZLIB, platformDefault, (b) =>
            {
                if (ZLib.CommandLineOverride)
                    _ = Client.Settings.SetAsync(SettingsScope.Global, Constants.SqlSettings.MANAGED_ZLIB, true);
                else
                    ZLib.SetForceManagedZlib(b);
            });
        }

        private const int MAX_PACKETS_PER_FRAME = 25;

        private void ProcessNetworkPackets()
        {
            int packetsProcessed = 0;
            while (packetsProcessed < MAX_PACKETS_PER_FRAME)
            {
                bool hasPacket = AsyncNetClient.Socket.TryDequeuePacket(out byte[] message);

                if (!hasPacket)
                    break;

                int c = PacketParser.Instance.ParsePackets(Client.Game.UO.World, message);

                AsyncNetClient.Socket.Statistics.TotalPacketsReceived += (uint)c;
                packetsProcessed++;
            }
        }

        protected override void LoadContent()
        {
            Log.Trace("[lc-debug] L1: LoadContent enter");
            base.LoadContent();
            Log.Trace("[lc-debug] L2: base.LoadContent done");
            Fonts.Initialize(GraphicsDevice);
            Log.Trace("[lc-debug] L3: Fonts.Initialize done");
            SolidColorTextureCache.Initialize(GraphicsDevice);
            Log.Trace("[lc-debug] L4: SolidColorTextureCache.Initialize done");

            Audio = new AudioManager();
            Log.Trace("[lc-debug] L5: AudioManager ctor done");

            byte[] bytes = Loader.GetBackgroundImage().ToArray();
            using var ms = new MemoryStream(bytes);
            _background = Texture2D.FromStream(GraphicsDevice, ms);
            Log.Trace($"[lc-debug] L6: background tex {_background.Width}x{_background.Height}");
            SetWindowPositionBySettings();
            Log.Trace("[lc-debug] L7: SetWindowPositionBySettings done");

#if false
            SetScene(new MainScene(this));
#else
            Log.Trace("[lc-debug] L8: pre-UO.Load");
            UO.Load(this);
            Log.Trace("[lc-debug] L9: post-UO.Load");

            PNGLoader.Instance.GraphicsDevice = GraphicsDevice;
            PNGLoader.Instance.LoadResourceAssets(Client.Game.UO.Gumps.GetGumpsLoader);
            Log.Trace("[lc-debug] L10: PNGLoader done");

            MyraEnvironment.Game = this;
            MyraEnvironment.SetMouseCursorFromWidget = false;
            MyraEnvironment.MouseInfoGetter = Mouse.GetMyraMouseInfo;
            MyraEnvironment.DefaultDebugFont = TrueTypeLoader.Instance.GetFont(EmbeddedFontNames.ROBOTO, 16);
            MyraStyle.SetDefault(); //Must occur after png loading
            Log.Trace("[lc-debug] L11: Myra init done");

            // v0.7.9 iter 19: re-enable Audio.Initialize on wasm now
            // that AudioManager.Initialize has its own `#if BROWSER_WASM`
            // branch that sets `_canReproduceAudio = false` and skips the
            // FAudio probe. Without this call LoginMusicIndex was 0 and
            // LoginScene.Load → PlayMusic(0) triggered SDL_INIT_AUDIO
            // (which Emscripten then fails on, traps `unreachable`).
            // With the WASM branch active, PlayMusic short-circuits via
            // `if (!_canReproduceAudio) return;` and LoginMusicIndex is
            // still set for callers that read it. UO is playable silent
            // until the Web Audio bridge is ported from CUO.
            Audio.Initialize();
            Log.Trace("[lc-debug] L12: Audio.Initialize done (WASM-safe branch)");

#if !BROWSER_WASM
            // v0.7.9: VoiceRecognitionManager is stubbed in WasmStubs.cs
            // but its .Instance singleton accessor invokes the underlying
            // Vosk constructor on first access — even though we removed
            // Vosk via <PackageReference Remove>. The stub Instance returns
            // a noop but TUO's TextRecognized event subscribe path
            // dereferences something internal that's null in the stub.
            // Safest: skip the wiring entirely on WASM.
            VoiceRecognitionManager.Instance.TextRecognized += OnVoiceTextRecognized;
#endif
            Log.Trace("[lc-debug] L13: VoiceRecognition wired (skipped on WASM)");

            Settings.GlobalSettings.Encryption = (byte)AsyncNetClient.Load(UO.FileManager.Version, (EncryptionType)Settings.GlobalSettings.Encryption);
            Log.Trace("[lc-debug] L14: AsyncNetClient.Load done");

            LoadPlugins();
            Log.Trace("[lc-debug] L15: LoadPlugins done");

            UIManager.World = UO.World;
            Log.Trace("[lc-debug] L16: UIManager.World set");

            Log.Trace("[lc-debug] L16.5: pre-new LoginScene");
            var loginScene = new LoginScene(UO.World);
            Log.Trace("[lc-debug] L16.7: new LoginScene done, pre-SetScene");
            SetScene(loginScene);
            Log.Trace("[lc-debug] L17: SetScene(LoginScene) done");
#endif
        }

        private void OnVoiceTextRecognized(string text)
        {
            SystemChatControl chat = UIManager.SystemChat;
            if (chat == null || chat.IsDisposed)
                return;

            if (!chat.IsActive)
            {
                chat.IsActive = true;
                chat.SetFocus();
            }

            chat.TextBoxControl.AppendText(text);
        }

        private void LoadPlugins()
        {
            Log.Trace("Loading plugins...");
            PluginHost?.Initialize();

            foreach (string p in Settings.GlobalSettings.Plugins)
            {
                Plugin.Create(p);
                _pluginsInitialized = true; //Moved here, if no plugins loaded, no need to run plugin code later
            }

            Log.Trace("Done!");
        }

        protected override void UnloadContent()
        {
            ItemDatabaseManager.Instance.Dispose();
            SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);

            Settings.GlobalSettings.WindowPosition = new Point(
                Math.Max(0, Window.ClientBounds.X - left),
                Math.Max(0, Window.ClientBounds.Y - top)
            );

            Audio?.StopMusic();
            VoiceRecognitionManager.Instance.Dispose();
            Settings.GlobalSettings.Save();

            if (_pluginsInitialized)
                Plugin.OnClosing();

            _screenRenderTarget?.Dispose();
            _screenRenderTarget = null;

            UO.Unload();
            base.UnloadContent();
        }

        public void SetWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
#if DEV_BUILD
                Window.Title = $"TazUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = DefaultWindowTitle;
#endif
            }
            else
            {
#if DEV_BUILD
                Window.Title = $"{title} - TazUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"{title} - {DefaultWindowTitle}";
#endif
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetScene<T>() where T : Scene => Scene as T;

        public void SetScene(Scene scene)
        {
            Scene?.Dispose();

            UIManager.Clear(); //Ensure we clear out all UI from previous scene

            Scene = scene;
            Scene?.Load();

            if (Scene != null && Scene.IsLoaded)
                drawScene = true;
            else
                drawScene = false;
        }

        public void SetVSync(bool value)
        {
            GraphicManager.SynchronizeWithVerticalRetrace = value;
            GraphicManager.ApplyChanges();
        }

        public void SetRefreshRate(int rate)
        {
            if (rate < Constants.MIN_FPS)
            {
                rate = Constants.MIN_FPS;
            }
            else if (rate > Constants.MAX_FPS)
            {
                rate = Constants.MAX_FPS;
            }

            float frameDelay;

            if (rate == Constants.MIN_FPS)
            {
                // The "real" UO framerate is 12.5. Treat "12" as "12.5" to match.
                frameDelay = 80;
            }
            else
            {
                frameDelay = 1000.0f / rate;
            }

            FrameDelay[0] = FrameDelay[1] = (uint)frameDelay;
            FrameDelay[1] = FrameDelay[1] >> 1;

            Settings.GlobalSettings.FPS = rate;

            _intervalFixedUpdate[0] = frameDelay;
            _intervalFixedUpdate[1] = 217; // 5 FPS
        }

        private void SetWindowPosition(int x, int y) => SDL_SetWindowPosition(Window.Handle, x, y);

        public void SetScale(float scale)
        {
            RenderScale = Math.Max(scale, 0.1f);
            ScaleChanged?.Invoke(this, RenderScale);
        }

        public void SetWindowSize(int width, int height, bool bufferOnly = false)
        {
            bufferRect = new Rectangle(0, 0, width, height);

            GraphicManager.PreferredBackBufferWidth = width;
            GraphicManager.PreferredBackBufferHeight = height;

            if (bufferOnly)
                return;

            GraphicManager.ApplyChanges();
        }

        public void SetWindowBorderless(bool borderless)
        {
            // Track fullscreen-borderless with an explicit flag rather than reading the
            // SDL_WINDOW_BORDERLESS flag: the plain borderless-window mode also toggles
            // that flag, so it can no longer tell the two modes apart. Without this, a
            // normal borderless window would be resized to display bounds when leaving
            // fullscreen, and entering fullscreen from a borderless window would no-op.
            if (_fullscreenBorderless == borderless)
            {
                return;
            }

            _fullscreenBorderless = borderless;

            SDL_SetWindowBordered(Window.Handle, !borderless);

            if (!SDL_GetDisplayBounds(SDL_GetDisplayForWindow(Window.Handle), out SDL_Rect rect))
                return;

            int width = rect.w;
            int height = rect.h;

            if (borderless)
            {
                SetWindowSize(width, height);
                SDL_GetDisplayUsableBounds(
                    SDL_GetDisplayForWindow(Window.Handle),
                    out SDL_Rect rectusable
                );
                SDL_SetWindowPosition(Window.Handle, rectusable.x, rectusable.y);
            }
            else
            {
                SDL_GetWindowBordersSize(Window.Handle, out int top, out _, out int bottom, out _);

                SetWindowSize(width, height - (top - bottom));
                SetWindowPositionBySettings();
            }

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
            bufferRect = new Rectangle(0, 0, GraphicManager.PreferredBackBufferWidth, GraphicManager.PreferredBackBufferHeight);
        }

        /// <summary>
        /// Toggles the window border (title bar and edges) while keeping the window in a
        /// normal windowed state. Unlike <see cref="SetWindowBorderless"/>, this does not
        /// resize the window to fill the display. Because stripping the border from a
        /// maximized window makes it cover the whole screen (borderless fullscreen), the
        /// window is first restored to a normal size when removing the border.
        /// </summary>
        public void SetWindowBordered(bool bordered)
        {
            if (!bordered && IsWindowMaximized())
                SDL_RestoreWindow(Window.Handle);

            SDL_SetWindowBordered(Window.Handle, bordered);
        }

        public void MaximizeWindow()
        {
            SDL_MaximizeWindow(Window.Handle);

            GraphicManager.PreferredBackBufferWidth = Client.Game.Window.ClientBounds.Width;
            GraphicManager.PreferredBackBufferHeight = Client.Game.Window.ClientBounds.Height;
            GraphicManager.ApplyChanges();
            bufferRect = new Rectangle(0, 0, Client.Game.Window.ClientBounds.Width, Client.Game.Window.ClientBounds.Height);
        }

        public bool IsWindowMaximized()
        {
            var flags = (SDL_WindowFlags)SDL_GetWindowFlags(Window.Handle);

            return (flags & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0;
        }

        public void RestoreWindow() => SDL_RestoreWindow(Window.Handle);

        public void SetWindowPositionBySettings()
        {
            SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);

            if (Settings.GlobalSettings.WindowPosition.HasValue)
            {
                int x = left + Settings.GlobalSettings.WindowPosition.Value.X;
                int y = top + Settings.GlobalSettings.WindowPosition.Value.Y;
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                SetWindowPosition(x, y);
            }
        }

        protected override void Update(GameTime gameTime)
        {
            Profiler.EnterContext("Update");

            Time.Ticks = (uint)gameTime.TotalGameTime.TotalMilliseconds;
            Time.Delta = (float)gameTime.ElapsedGameTime.TotalSeconds;

#if BROWSER_WASM
            // v0.7.9 iter 20: Update tick counter — paired with [draw-tick]
            // to distinguish "main loop frozen entirely" vs "Update runs
            // but Draw doesn't" vs "both run but no visual change". If
            // Update fires but Draw doesn't, the bug is in the Draw path
            // gating (likely Scene.IsActive or render-target invariant).
            _updateTickFrames++;
            if ((_updateTickFrames % 60) == 0)
            {
                Log.Trace($"[update-tick] frame={_updateTickFrames} ticks={Time.Ticks}");
            }

            // [perf] long-frame monitor (ported from CUO). Environment.TickCount64
            // is the runtime wall-clock ms counter — stable on Mercury WASM, no
            // JSInterop, and unlike gameTime.ElapsedGameTime it reports the
            // measured delta, not FNA's requested fixed-timestep target.
            long nowWallMs = Environment.TickCount64;
            if (_wasmLastUpdateWallMs > 0)
            {
                double delta = nowWallMs - _wasmLastUpdateWallMs;
                if (delta >= FRAME_LONG_MS)
                {
                    _wasmLongFrameCount60s++;
                    if (_wasmLongFrameWindowStartMs == 0)
                        _wasmLongFrameWindowStartMs = nowWallMs;
                    long updateEnd = WasmUpdateEndMs;
                    long drawEnd = WasmDrawEndMs;
                    long prevStart = (long)_wasmLastUpdateWallMs;
                    long updMs = updateEnd > prevStart ? updateEnd - prevStart : 0;
                    long drwMs = drawEnd > updateEnd ? drawEnd - updateEnd : 0;
                    long gapMs = drawEnd > 0 ? Math.Max(0, nowWallMs - drawEnd) : 0;
                    Console.WriteLine(
                        $"[perf] long-frame={delta:F1}ms upd={updMs}ms drw={drwMs}ms gap={gapMs}ms (n={_wasmLongFrameCount60s} since +{(nowWallMs - _wasmLongFrameWindowStartMs):F0}ms)");
                }
                if (_wasmLongFrameWindowStartMs > 0
                    && nowWallMs - _wasmLongFrameWindowStartMs > 60_000)
                {
                    Console.WriteLine(
                        $"[perf] long-frame window: {_wasmLongFrameCount60s} hitches >{FRAME_LONG_MS}ms in last 60s");
                    _wasmLongFrameCount60s = 0;
                    _wasmLongFrameWindowStartMs = 0;
                }
            }
            _wasmLastUpdateWallMs = nowWallMs;

            // v0.7.9 iter 22: drain DOM input events from the JS bridge
            // into HandleSdlEvent BEFORE Mouse.Update() so clicks that
            // arrived this frame can update Mouse state (button bitmask,
            // click position) in time for the scene/UIManager update.
            PumpWasmInput();
#endif

            Profiler.EnterContext("Mouse");
            Mouse.Update();
            Profiler.ExitContext("Mouse");

            Profiler.EnterContext("ProcessNetworkPackets");
            ProcessNetworkPackets();
            Profiler.ExitContext("ProcessNetworkPackets");

            if(_pluginsInitialized)
            {
                Profiler.EnterContext("PluginTick");
                Plugin.Tick();
                Profiler.ExitContext("PluginTick");
            }

            if(drawScene)
            {
                Profiler.EnterContext("SceneUpdate");
                Scene.Update();
                Profiler.ExitContext("SceneUpdate");
            }

            Profiler.EnterContext("UIManagerUpdate");
            UIManager.Update();
            Profiler.ExitContext("UIManagerUpdate");

            Profiler.EnterContext("MainThreadQueue");
            MainThreadQueue.ProcessQueue();
            Profiler.ExitContext("MainThreadQueue");

            Profiler.EnterContext("FpsTiming");
            _totalElapsed += gameTime.ElapsedGameTime.TotalMilliseconds;
            _currentFpsTime += gameTime.ElapsedGameTime.TotalMilliseconds;

            if (_currentFpsTime >= 1000)
            {
                CUOEnviroment.CurrentRefreshRate = _totalFrames;

                _totalFrames = 0;
                _currentFpsTime = 0;
            }

#if !BROWSER_WASM
            double x = _intervalFixedUpdate[
                !IsActive
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.ReduceFPSWhenInactive
                    ? 1
                    : 0
            ];
#endif
            _suppressedDraw = false;

#if BROWSER_WASM
            // v0.7.9 iter 21 root cause + fix (ported from CUO v0.3.32):
            //
            //   Under emscripten_set_main_loop the browser's
            //   requestAnimationFrame is the natural frame governor — it
            //   caps at the display refresh rate (60/120/144 Hz). The
            //   desktop wall-clock SuppressDraw branch below was designed
            //   for an *unbounded* fixed-timestep loop, where C# code
            //   could spin Update→Draw thousands of times per second on
            //   a fast CPU and needed a software cap to stop heating the
            //   GPU. Under rAF the loop is already capped, and the same
            //   software check double-throttles.
            //
            //   In TUO's specific case the double-throttle didn't just
            //   stutter — it skipped Draw on EVERY frame (measured in
            //   iter 20: [update-tick] fired 2280× in 41 s, [draw-tick]
            //   fired ZERO times). Result: the canvas painted exactly
            //   one frame (the LoginGump on entry) and never updated
            //   again. Caret didn't blink, mouse cursor sprite didn't
            //   move with the pointer, clicks reached the bridge but
            //   produced no visible change (focus events fired but the
            //   resulting UI state was never rendered).
            //
            //   The 60 Hz fixed timestep + 1000/60 frame-delay equality
            //   was the precise corner case: `gameTime.ElapsedGameTime`
            //   ≈ `_intervalFixedUpdate[0]` made `_totalElapsed > x`
            //   false on the very first call (16.667 > 16.667 is false
            //   under floating-point equality), SuppressDraw fired
            //   before modulo could clear the accumulator, and the
            //   pattern repeated forever.
            //
            //   Fix: skip the cap entirely on WASM. rAF holds us at the
            //   display rate; the Thread.Sleep(1) inside the else branch
            //   is also a no-op on Mercury MT single-thread anyway.
            _totalElapsed = 0;
#else
            if (_totalElapsed > x)
            {
                _totalElapsed %= x;
            }
            else
            {
                _suppressedDraw = true;
                SuppressDraw();

                if (!gameTime.IsRunningSlowly)
                {
                    Thread.Sleep(1);
                }
            }
#endif
            Profiler.ExitContext("FpsTiming");

            Profiler.EnterContext("GameCursor");
            UO.GameCursor?.Update();
            Profiler.ExitContext("GameCursor");

            Profiler.EnterContext("Audio");
            Audio?.Update();
            Profiler.ExitContext("Audio");

#if BROWSER_WASM
            // Anchor end of C# Update tick for the long-frame upd/drw/gap split.
            WasmUpdateEndMs = Environment.TickCount64;
#endif

            base.Update(gameTime);

            Profiler.ExitContext("Update");
        }

        public static void UpdateBackgroundHueShader()
        {
            if (ProfileManager.CurrentProfile != null)
                bgHueShader = ShaderHueTranslator.GetHueVector(ProfileManager.CurrentProfile.MainWindowBackgroundHue, false, bgHueShader.Z);
        }

        private void EnsureScreenRenderTarget()
        {
            int width = GraphicManager.PreferredBackBufferWidth;
            int height = GraphicManager.PreferredBackBufferHeight;

            // Sanity check dimensions
            if (width <= 0 || height <= 0)
            {
                Log.Warn($"Invalid render target dimensions: {width}x{height}");
                return;
            }

            if (_screenRenderTarget == null ||
                _screenRenderTarget.IsDisposed ||
                _screenRenderTarget.Width != width ||
                _screenRenderTarget.Height != height)
            {
                _screenRenderTarget?.Dispose();

                try
                {
                    PresentationParameters pp = GraphicsDevice.PresentationParameters;
                    _screenRenderTarget = new RenderTarget2D(
                        GraphicsDevice,
                        width,
                        height,
                        false,
                        pp.BackBufferFormat,
                        pp.DepthStencilFormat,
                        pp.MultiSampleCount,
                        RenderTargetUsage.DiscardContents
                    );
                    Log.Trace($"Created render target: {width}x{height}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to create render target ({width}x{height}): {ex.Message}");
                    throw;
                }
            }
        }

        protected override void Draw(GameTime gameTime)
        {
            Profiler.EnterContext("Draw");

            Profiler.EndFrame();

            Profiler.EnterContext("PreDraw");
            UIManager.PreDraw();
            Profiler.ExitContext("PreDraw");

            Profiler.BeginFrame();

            Profiler.EnterContext("RenderSetup");
            _totalFrames++;

#if BROWSER_WASM
            // v0.7.9 iter 20: prove the FNA main loop is running on
            // Mercury MT WASM. After iter 19 shipped, 6 byte-identical
            // screenshots across 4 different mouse clicks suggested
            // the Draw cycle had stopped — but every supporting trace
            // (FNA's RunEmscriptenMainLoop, emscripten_set_main_loop)
            // looks identical to CUO's working code. This tick logs
            // once per second (every 60 frames at 60 FPS) so we can
            // confirm Draw is or isn't being called. If we see ticks,
            // the click→focus path is the real bug, not the loop.
            if ((_totalFrames % 60) == 0)
            {
                Log.Trace($"[draw-tick] frame={_totalFrames} ticks={Time.Ticks}");
            }
#endif

            bool useRenderTarget = false;

            if (_useScreenRenderTarget)
            {
                EnsureScreenRenderTarget();

                useRenderTarget = _screenRenderTarget != null && !_screenRenderTarget.IsDisposed;

                if (!useRenderTarget)
                {
                    Log.Warn($"Render target invalid: null={_screenRenderTarget == null}, disposed={_screenRenderTarget?.IsDisposed ?? false}, bufferSize={GraphicManager.PreferredBackBufferWidth}x{GraphicManager.PreferredBackBufferHeight}");
                }
            }

            if (useRenderTarget)
            {
                GraphicsDevice.SetRenderTarget(_screenRenderTarget);
                GraphicsDevice.Clear(Color.Black);
            }
            else
            {
                GraphicsDevice.Clear(Color.Black);
            }
            Profiler.ExitContext("RenderSetup");

            Profiler.EnterContext("SceneRender");

            _uoSpriteBatch.Begin();
            _uoSpriteBatch.DrawTiled(_background, bufferRect, _background.Bounds, bgHueShader);
            _uoSpriteBatch.End();

            if (drawScene)
                Scene.Draw(_uoSpriteBatch);

            UIManager.Draw(_uoSpriteBatch);

            SelectedObject.HealthbarObject = null;
            SelectedObject.SelectedContainer = null;

            _uoSpriteBatch.Begin();
            UO.GameCursor?.Draw(_uoSpriteBatch);
            _uoSpriteBatch.End();

            Profiler.ExitContext("SceneRender");

            Profiler.EnterContext("PluginRender");
            if (useRenderTarget)
            {
                if(_pluginsInitialized)
                    Plugin.ProcessDrawCmdList(GraphicsDevice);

                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Clear(Color.Black);

                var srcRect = new Rectangle(0, 0, _screenRenderTarget.Width, _screenRenderTarget.Height);
                Rectangle destRect = srcRect;

                _uoSpriteBatch.Begin();
                if(RenderScale != 1.0f)
                {
                    destRect = new Rectangle(0, 0, (int)(_screenRenderTarget.Width * RenderScale), (int)(_screenRenderTarget.Height * RenderScale));
                    _uoSpriteBatch.SetSampler(SamplerState.AnisotropicClamp);
                }
                _uoSpriteBatch.Draw(_screenRenderTarget, destRect, srcRect, new Vector3(0, 0, 1f));
                _uoSpriteBatch.End();
            }
            else
            {
                if(_pluginsInitialized)
                    Plugin.ProcessDrawCmdList(GraphicsDevice);
            }
            Profiler.ExitContext("PluginRender");

            base.Draw(gameTime);

#if BROWSER_WASM
            // Anchor end of C# Draw for the long-frame upd/drw/gap split.
            WasmDrawEndMs = Environment.TickCount64;
#endif

            Profiler.ExitContext("Draw");
        }

        protected override bool BeginDraw() => !_suppressedDraw && base.BeginDraw();

        /// <summary>
        /// Must be called during a batch, cannot call before batcher.Begin or after batcher.End
        /// </summary>
        /// <param name="batcher"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        [Conditional("DEBUG")]
        public static void DrawFlushCounts(UltimaBatcher2D batcher, int x, int y)
        {
            Vector3 hueVec = new(0, 1, 1);
            string s = $"Flushes: {batcher.FlushesDone}\nSwitches: {batcher.TextureSwitches}";
            batcher.DrawString(Fonts.Bold, s, x, y, hueVec);
            hueVec = Vector3.Zero;
            batcher.DrawString(Fonts.Bold, s, x + 1, y - 1, hueVec);
        }

        private void WindowOnClientSizeChanged(object sender, EventArgs e)
        {
            int width = Window.ClientBounds.Width;
            int height = Window.ClientBounds.Height;

            if (!IsWindowMaximized())
            {
                if (ProfileManager.CurrentProfile != null)
                    ProfileManager.CurrentProfile.WindowClientBounds = new Point(width, height);
            }

            SetWindowSize(width, height, true);

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile != null)
            {
                if (ProfileManager.CurrentProfile.GameWindowFullSize)
                {
                    viewport.ResizeGameWindow(new Point(width, height));
                    viewport.X = 0;
                    viewport.Y = 0;
                }
                else
                    viewport.OnWindowResized();
            }
        }

        private bool HandleSdlEvent(IntPtr userdata, SDL_Event* sdlEvent)
        {
            if (sdlEvent == null)
            {
                Log.Error("SDL Event was null, this is an unexpected error.");
                return false;
            }

            switch ((SDL_EventType)sdlEvent->type)
            {
                case SDL_EventType.SDL_EVENT_AUDIO_DEVICE_ADDED:
                    Log.Trace($"AUDIO ADDED: {sdlEvent->adevice.which}");
                    Audio?.OnAudioDeviceAdded();
                    break;

                case SDL_EventType.SDL_EVENT_AUDIO_DEVICE_REMOVED:
                    Log.Trace($"AUDIO REMOVED: {sdlEvent->adevice.which}");
                    Audio?.OnAudioDeviceRemoved();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_ENTER:
                    Mouse.MouseInWindow = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                    Mouse.MouseInWindow = false;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                    if (_pluginsInitialized)
                        Plugin.OnFocusGained();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    // Drop tracked key state so a key held while we lose focus doesn't stick "pressed"
                    // for polled hotkeys (the key-up may never reach us).
                    ClassicUO.Game.Managers.Hotkeys.HotKeys.ClearHeldKeys();
                    if (_pluginsInitialized)
                        Plugin.OnFocusLost();
                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN when Scene is not null:
                    Keyboard.OnKeyDown(sdlEvent->key);

                    if (Plugin.ProcessHotkeys(
                            (int)sdlEvent->key.key,
                            (int)sdlEvent->key.mod,
                            true
                        )
                    )
                    {
                        _ignoreNextTextInput = false;

                        UIManager.KeyboardFocusControl?.InvokeKeyDown(
                            (SDL_Keycode)sdlEvent->key.key,
                            sdlEvent->key.mod
                        );

                        Scene.OnKeyDown(sdlEvent->key);
                    }
                    else
                    {
                        _ignoreNextTextInput = true;
                    }

                    break;

                case SDL_EventType.SDL_EVENT_KEY_UP when Scene is not null:
                    var key = (SDL_Keycode)sdlEvent->key.key;

                    Keyboard.OnKeyUp(sdlEvent->key);

                    UIManager.KeyboardFocusControl?.InvokeKeyUp(key, sdlEvent->key.mod);

                    Scene.OnKeyUp(sdlEvent->key);

                    Plugin.ProcessHotkeys(0, 0, false);

                    if (key == SDL_Keycode.SDLK_PRINTSCREEN)
                    {
                        if (Keyboard.Ctrl)
                        {
                            if (Tooltip.IsEnabled)
                            {
                                ClipboardScreenshot(new Rectangle(Tooltip.X, Tooltip.Y, Tooltip.Width, Tooltip.Height), GraphicsDevice);
                            }
                            else if (MultipleToolTipGump.SSIsEnabled)
                            {
                                ClipboardScreenshot(new Rectangle(MultipleToolTipGump.SSX, MultipleToolTipGump.SSY, MultipleToolTipGump.SSWidth, MultipleToolTipGump.SSHeight), GraphicsDevice);
                            }
                            else if (UIManager.MouseOverControl != null && UIManager.MouseOverControl.IsVisible)
                            {
                                IGui c = UIManager.MouseOverControl.RootParent;
                                if (c != null)
                                {
                                    ClipboardScreenshot(c.Bounds, GraphicsDevice);
                                }
                                else
                                {
                                    ClipboardScreenshot(UIManager.MouseOverControl.Bounds, GraphicsDevice);
                                }
                            }
                        }
                        else
                        {
                            TakeScreenshot();
                        }
                    }

                    break;

                case SDL_EventType.SDL_EVENT_TEXT_INPUT when Scene is not null:
                    if (_ignoreNextTextInput)
                    {
                        break;
                    }

                    // Fix for linux OS: https://github.com/andreakarasho/ClassicUO/pull/1263
                    // Fix 2: SDL owns this behaviour. Cheating is not a real solution.
                    /*if (!Utility.Platforms.PlatformHelper.IsWindows)
                    {
                        if (Keyboard.Alt || Keyboard.Ctrl)
                        {
                            break;
                        }
                    }*/

                    string s = Marshal.PtrToStringUTF8((IntPtr)sdlEvent->text.text);

                    if (!string.IsNullOrEmpty(s))
                    {
                        UIManager.KeyboardFocusControl?.InvokeTextInput(s);
                        Scene.OnTextInput(s);
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION when Scene is not null:

                    if (UO.GameCursor != null && !UO.GameCursor.AllowDrawSDLCursor)
                    {
                        UO.GameCursor.AllowDrawSDLCursor = true;
                        UO.GameCursor.Graphic = 0xFFFF;
                    }

                    Mouse.Update();

                    if (Mouse.IsDragging)
                    {
                        if (!Scene.OnMouseDragging())
                        {
                            UIManager.OnMouseDragging();
                        }
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL when Scene is not null:
                    Mouse.Update();
                    bool isScrolledUp = sdlEvent->wheel.y > 0;

                    Mouse.RaiseWheelEvent(isScrolledUp);

                    if (_pluginsInitialized)
                        Plugin.ProcessMouse(0, (int)sdlEvent->wheel.y);

                    if (!Scene.OnMouseWheel(isScrolledUp))
                    {
                        UIManager.OnMouseWheel(isScrolledUp);
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when Scene is not null:
                    {
                        SDL_MouseButtonEvent mouse = sdlEvent->button;

                        // The values in MouseButtonType are chosen to exactly match the SDL values
                        var buttonType = (MouseButtonType)mouse.button;

                        uint lastClickTime = 0;

                        switch (buttonType)
                        {
                            case MouseButtonType.Left:
                                lastClickTime = Mouse.LastLeftButtonClickTime;

                                break;

                            case MouseButtonType.Middle:
                                lastClickTime = Mouse.LastMidButtonClickTime;

                                break;

                            case MouseButtonType.Right:
                                lastClickTime = Mouse.LastRightButtonClickTime;

                                break;

                            case MouseButtonType.XButton1:
                            case MouseButtonType.XButton2:
                                break;

                            default:
                                Log.Warn($"No mouse button handled: {mouse.button}");

                                break;
                        }

                        Mouse.ButtonPress(buttonType);
                        Mouse.Update();

                        uint ticks = Time.Ticks;

                        if (lastClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                        {
                            lastClickTime = 0;

                            bool res =
                                Scene.OnMouseDoubleClick(buttonType)
                                || UIManager.OnMouseDoubleClick(buttonType);

                            if (res)
                            {
                                lastClickTime = 0xFFFF_FFFF;
                            }
                        }
                        else
                        {
                            if (
                                _pluginsInitialized &&
                                buttonType != MouseButtonType.Left
                                && buttonType != MouseButtonType.Right
                            )
                            {
                                Plugin.ProcessMouse(sdlEvent->button.button, 0);
                            }

                            if (!Scene.OnMouseDown(buttonType))
                            {
                                UIManager.OnMouseButtonDown(buttonType);
                            }

                            lastClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                        }

                        switch (buttonType)
                        {
                            case MouseButtonType.Left:
                                Mouse.LastLeftButtonClickTime = lastClickTime;

                                break;

                            case MouseButtonType.Middle:
                                Mouse.LastMidButtonClickTime = lastClickTime;

                                break;

                            case MouseButtonType.Right:
                                Mouse.LastRightButtonClickTime = lastClickTime;

                                break;
                        }

                        break;
                    }

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP when Scene is not null:
                    {
                        SDL_MouseButtonEvent mouse = sdlEvent->button;

                        // The values in MouseButtonType are chosen to exactly match the SDL values
                        var buttonType = (MouseButtonType)mouse.button;

                        uint lastClickTime = 0;

                        switch (buttonType)
                        {
                            case MouseButtonType.Left:
                                lastClickTime = Mouse.LastLeftButtonClickTime;

                                break;

                            case MouseButtonType.Middle:
                                lastClickTime = Mouse.LastMidButtonClickTime;

                                break;

                            case MouseButtonType.Right:
                                lastClickTime = Mouse.LastRightButtonClickTime;

                                break;

                            default:
                                Log.Warn($"No mouse button handled: {mouse.button}");

                                break;
                        }

                        if (lastClickTime != 0xFFFF_FFFF)
                        {
                            if (
                                !Scene.OnMouseUp(buttonType)
                                || UIManager.LastControlMouseDown(buttonType) != null
                            )
                            {
                                UIManager.OnMouseButtonUp(buttonType);
                            }
                        }

                        Mouse.ButtonRelease(buttonType);
                        Mouse.Update();

                        break;
                    }

                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN when Scene is not null:
                    if (!IsActive || ProfileManager.CurrentProfile == null || !ProfileManager.CurrentProfile.ControllerEnabled)
                    {
                        break;
                    }
                    Controller.OnButtonDown(sdlEvent->gbutton);
                    UIManager.KeyboardFocusControl?.InvokeControllerButtonDown((SDL.SDL_GamepadButton)sdlEvent->gbutton.button);
                    Scene.OnControllerButtonDown(sdlEvent->gbutton);

                    if (sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK)
                    {
                        SDL_Event e = new();
                        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN;
                        e.button.button = (byte)MouseButtonType.Left;
                        SDL_PushEvent(ref e);
                    }
                    else if (sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK)
                    {
                        SDL_Event e = new();
                        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN;
                        e.button.button = (byte)MouseButtonType.Right;
                        SDL_PushEvent(ref e);
                    }
                    else if (sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START && UO.World.InGame)
                    {
                        Gump g = UIManager.GetGump<ModernOptionsGump>();
                        if (g == null)
                        {
                            UIManager.Add(new ModernOptionsGump(UIManager.World));
                        }
                        else
                        {
                            g.Dispose();
                        }
                    }
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP when Scene is not null:
                    if (!IsActive || ProfileManager.CurrentProfile == null || !ProfileManager.CurrentProfile.ControllerEnabled)
                    {
                        break;
                    }
                    Controller.OnButtonUp(sdlEvent->gbutton);
                    UIManager.KeyboardFocusControl?.InvokeControllerButtonUp((SDL.SDL_GamepadButton)sdlEvent->gbutton.button);
                    Scene.OnControllerButtonUp(sdlEvent->gbutton);

                    if (sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK)
                    {
                        SDL_Event e = new();
                        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP;
                        e.button.button = (byte)MouseButtonType.Left;
                        SDL_PushEvent(ref e);
                    }
                    else if (sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK)
                    {
                        SDL_Event e = new();
                        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP;
                        e.button.button = (byte)MouseButtonType.Right;
                        SDL_PushEvent(ref e);
                    }
                    break;

                case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION when Scene is not null: //Work around because sdl doesn't see trigger buttons as buttons, they are axis probably for pressure support
                                                                  //GameActions.Print(typeof(SDL_GamepadButton).GetEnumName((SDL_GamepadButton)sdlEvent->gbutton.button));
                    if (!IsActive || ProfileManager.CurrentProfile == null || !ProfileManager.CurrentProfile.ControllerEnabled)
                    {
                        break;
                    }
                    if (sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK || sdlEvent->gbutton.button == (byte)SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE) //Left trigger BACK Right trigger GUIDE
                    {
                        if (sdlEvent->gaxis.value > 32000)
                        {
                            if (
                                ((SDL.SDL_GamepadButton)sdlEvent->gbutton.button == SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK && !Controller.Button_LeftTrigger)
                                || ((SDL.SDL_GamepadButton)sdlEvent->gbutton.button == SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE && !Controller.Button_RightTrigger)
                                )
                            {
                                Controller.OnButtonDown(sdlEvent->gbutton);
                                UIManager.KeyboardFocusControl?.InvokeControllerButtonDown((SDL.SDL_GamepadButton)sdlEvent->gbutton.button);
                                Scene.OnControllerButtonDown(sdlEvent->gbutton);
                            }
                        }
                        else if (sdlEvent->gaxis.value < 5000)
                        {
                            Controller.OnButtonUp(sdlEvent->gbutton);
                            UIManager.KeyboardFocusControl?.InvokeControllerButtonUp((SDL.SDL_GamepadButton)sdlEvent->gbutton.button);
                            Scene.OnControllerButtonUp(sdlEvent->gbutton);
                        }
                    }
                    break;
            }

            return true;
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            Scene?.Dispose();

            base.OnExiting(sender, args);
        }

        private void TakeScreenshot()
        {
#if !BROWSER_WASM
            string screenshotsFolder = FileSystemHelper.CreateFolderIfNotExists(
                CUOEnviroment.ExecutablePath,
                "Data",
                "Client",
                "Screenshots"
            );

            string path = Path.Combine(
                screenshotsFolder,
                $"screenshot_{DateTime.Now:yyyy-MM-dd_hh-mm-ss}.png"
            );

            Color[] colors;
            int width, height;

            // Use render target if available and in use, otherwise use back buffer
            if (_useScreenRenderTarget && _screenRenderTarget != null && !_screenRenderTarget.IsDisposed)
            {
                width = _screenRenderTarget.Width;
                height = _screenRenderTarget.Height;
                colors = new Color[width * height];
                _screenRenderTarget.GetData(colors);
            }
            else
            {
                width = GraphicManager.PreferredBackBufferWidth;
                height = GraphicManager.PreferredBackBufferHeight;
                colors = new Color[width * height];
                GraphicsDevice.GetBackBufferData(colors);
            }

            using (
                var texture = new Texture2D(
                    GraphicsDevice,
                    width,
                    height,
                    false,
                    SurfaceFormat.Color
                )
            )
            using (FileStream fileStream = File.Create(path))
            {
                texture.SetData(colors);
                texture.SaveAsPng(fileStream, texture.Width, texture.Height);
                string message = string.Format(ResGeneral.ScreenshotStoredIn0, path);

                if (
                    ProfileManager.CurrentProfile == null
                    || ProfileManager.CurrentProfile.HideScreenshotStoredInMessage
                )
                {
                    Log.Info(message);
                }
                else
                {
                    GameActions.Print(UO.World, message, 0x44, MessageType.System);
                }
            }
#else
            // v0.7.9: PNG screenshot-to-disk disabled on the web build.
            // Profile-sync uploads /Data/Profiles/ to the operator under
            // the user's Discord ID, and PNG payloads would balloon both
            // server disk and the per-user 10 MB profile cap. Users can
            // still capture the canvas via the browser's native tools
            // (Win+Shift+S / Cmd+Shift+5). Mirrors the equivalent no-op
            // in source/cuo/src/ClassicUO.Client/GameController.cs.
#endif
        }

        public void ClipboardScreenshot(Rectangle position, GraphicsDevice graphicDevice)
        {
#if !BROWSER_WASM
            var colors = new Color[position.Width * position.Height];

            // Use render target if available and in use, otherwise use back buffer
            if (_useScreenRenderTarget && _screenRenderTarget != null && !_screenRenderTarget.IsDisposed)
            {
                _screenRenderTarget.GetData(0, position, colors, 0, colors.Length);
            }
            else
            {
                graphicDevice.GetBackBufferData(position, colors, 0, colors.Length);
            }

            using (
                var texture = new Texture2D(
                    GraphicsDevice,
                    position.Width,
                    position.Height,
                    false,
                    SurfaceFormat.Color
                )
            )
            {
                texture.SetData(colors);

                string screenshotsFolder = FileSystemHelper.CreateFolderIfNotExists(
                    CUOEnviroment.ExecutablePath,
                    "Data",
                    "Client",
                    "Screenshots"
                );

                string path = Path.Combine(
                    screenshotsFolder,
                    $"screenshot_{DateTime.Now:yyyy-MM-dd_hh-mm-ss}.png"
                );

                using FileStream fileStream = File.Create(path);
                texture.SaveAsPng(fileStream, texture.Width, texture.Height);
                string message = string.Format(ResGeneral.ScreenshotStoredIn0, path);

                if (ProfileManager.CurrentProfile == null || ProfileManager.CurrentProfile.HideScreenshotStoredInMessage)
                {
                    Log.Info(message);
                }
                else
                {
                    GameActions.Print(UO.World, message, 0x44, MessageType.System);
                }
            }
#else
            // v0.7.9: Region-clip PNG dump disabled on the web build for
            // the same abuse-prevention reason as TakeScreenshot() above.
#endif
        }

        private static void FnaLogInfo(string message)=> Log.Info(message);

        private static void FnaLogWarn(string message)
        {
            {
                // This message spams the console and is generally unhelpful.
                if (message == null || message.StartsWith("Scissor rect and viewport"))
                    return;

                Log.Warn(message);
            }
        }

        private static void FnaLogError(string message) => Log.Error(message);


        private static void RegisterFnaLoggerListeners()
        {
            FNALoggerEXT.LogInfo += FnaLogInfo;
            FNALoggerEXT.LogWarn += FnaLogWarn;
            FNALoggerEXT.LogError += FnaLogError;
        }
    }
}
