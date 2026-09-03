// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
#if BROWSER_WASM
using System.Runtime.InteropServices;
#endif
using System.Threading;
using static SDL3.SDL;

namespace ClassicUO
{
    internal unsafe class GameController : Microsoft.Xna.Framework.Game
    {
        private SDL_EventFilter _filter;

        private bool _ignoreNextTextInput;
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _totalElapsed, _currentFpsTime;
        private uint _totalFrames;
        private UltimaBatcher2D _uoSpriteBatch;
        private RenderTargets _renderTargets = new();
        // Read-only accessor for the gameview screenshot bridge (operator 2026-06-23,
        // authorized screenshot revert). Exposes the world/UI render targets so
        // ClassicUO.Game.ScreenshotBridge can read the WorldRenderTarget (gameview-only)
        // without editing the render loop. Does NOT widen any mutation surface.
        internal RenderTargets RenderTargets => _renderTargets;
        private readonly RenderLists _renderLists = new();
        private bool _suppressedDraw;
        private bool _pluginsInitialized = false;
        private float _displayScale;
#if BROWSER_WASM
        // Counter shared as the "tick number" label in trace output.
        // Incremented (not decremented) so they monotonically grow.
        private int _wasmUpdateTraceRemaining = 0;
        private int _wasmDrawTraceRemaining = 0;
        private uint _wasmLastHeartbeatMs = 0;

        // v0.3.30 frame-time monitor. Tracks the wall-clock delta between
        // consecutive Update() calls. When a frame spikes above
        // FRAME_LONG_MS, emit `[perf] long-frame=Xms (n=Y in last 60s)`
        // through the silencer-compatible console pipe so operators with
        // `?dev=1` can diagnose the perceived "feels less smooth than
        // FPS counter says" issue. Lightweight enough to run in prod —
        // one Date.now() comparison + an int counter — no string alloc
        // unless a long frame actually fires.
        private double _wasmLastUpdateWallMs = 0;
        private int _wasmLongFrameCount60s = 0;
        private double _wasmLongFrameWindowStartMs = 0;
        // v0.8.94 [fps] 5s aggregate (see Update).
        private int _wasmFpsFrames = 0;
        private double _wasmFpsWorstMs = 0;
        private long _wasmFpsWindowStartMs = 0;
        // 20ms = 1.2× a 60Hz rAF interval. Anything below this is jitter
        // we can't avoid (browser scheduler, vsync). Above is a real
        // hitch — GC pause, audio decode, atlas re-upload, etc.
        private const double FRAME_LONG_MS = 20.0;

        // v0.4.48 frame-budget breakdown. operator runmatch3.log (post-
        // v0.4.47) showed `fill-sub total=0ms` for most frames yet long-
        // frame=45-55ms. The labelled lag-diag points
        // (PrepareLights/DrawWorld/base.Draw/Draw.{SetRT,FillObj,
        // PreRenderLists,RenderLists}) all fired below the 3ms threshold,
        // so the labelled C# code accounts for <10ms. The remaining
        // 35-45ms lives outside C# main-frame work: GPU commit, browser
        // compositor blit, rAF dispatch back. Need a SINGLE log line per
        // long-frame to surface that gap without 1000ms cooldown.
        //
        // Track two wall-clock anchors set by GameScene:
        //   - WasmUpdateEndMs : after GameScene.Update returns (end of
        //                       C# Update tick).
        //   - WasmDrawEndMs   : after GameScene.Draw returns (end of C#
        //                       Draw + base.Draw / UI paint).
        // Computed deltas in the long-frame log:
        //   - C#-Update = WasmUpdateEndMs - prev_update_start
        //   - C#-Draw   = WasmDrawEndMs - WasmUpdateEndMs
        //   - js-gap    = nowWallMs - WasmDrawEndMs
        // js-gap is the "everything else" bucket (GPU + compositor + rAF).
        public static long WasmUpdateEndMs;
        public static long WasmDrawEndMs;

        // Auto-save accumulator. browser tab-close does not fire
        // GameScene.Unload reliably + the `beforeunload` JS-side
        // flush is fire-and-forget with no guarantee the IndexedDB
        // transaction commits before the tab dies. User report
        // 2026-04-22: "a veces si se guarda los settings y a veces
        // no" — matches this race exactly: if the user drag-moves a
        // gump and reloads within the auto-save gap, the change is
        // lost. Drop the interval to 5 s so the worst-case loss
        // window is ~5 s rather than ~30 s. The save path itself is
        // cheap (Profile.Save + syncfs-on-modified-files); at 5 s
        // it's still well below any perceptible I/O hitch.
        // v0.3.34: bumped 5_000 → 30_000 to match the documented intent
        // ("every 30 s of game time"). Operator log 2026-05-06 captured
        // saves firing every 5 s during gameplay, each contributing JSON
        // serialise + 3 file writes + IDBFS dirty-tracking work to the
        // main thread. 6× less frequent saves with a 25 s wider tab-close
        // loss window — acceptable trade-off given that explicit flush
        // sites (OptionsGump apply, GameScene logout, drag-end via
        // RequestImmediateAutoSave below) already cover the high-value
        // mutations. The pure tab-close-mid-walk failure mode loses up to
        // 30 s of gump-position drift, which is recoverable by re-opening
        // the gump.
        private double _wasmAutoSaveAccumMs = 0;
        private const double _wasmAutoSaveIntervalMs = 30_000;
        // v0.3.34 lag fix: debounce RequestImmediateAutoSave so back-to-back
        // drag-ends coalesce into one save instead of three. Each forced
        // save runs Profile + Macros + InfoBars + IDBFS flush.
        // v0.4.13: bumped 2 s -> 10 s after operator log (2026-05-07) showed
        // 8 Profile.Save calls in 60 s during a single zone tour. The 2 s
        // window let scroll-as-drag-end fire RIAS at the exact 2 s boundary
        // every cycle. 10 s is well below auto-save cadence and matches the
        // human-perceptible "I dragged something" interval, so users still
        // get prompt persistence on real drag-end.
        private static long _wasmLastForcedSaveMs = 0;
        private const long _wasmForceSaveDebounceMs = 10_000;
        // v0.4.13: hard floor between successive actual saves, regardless of
        // trigger (interval tick OR force flag). The 30 s timer + 10 s RIAS
        // debounce can still race across two adjacent Update ticks (one
        // tick fires the interval reset, the next tick fires a freshly-set
        // force flag) producing two saves ~16 ms apart - exactly what the
        // operator's log captured at 20:26:53 / 20:26:54. This guard
        // serialises every save path through one minimum interval.
        private static long _wasmLastActualSaveMs = 0;
        private const long _wasmMinSaveIntervalMs = 8_000;

        // v0.4.19 click-flow trace: capture per-frame SelectedObject snapshots
        // ONLY in the 250 ms window after a [click-recv] log. That gives us
        // 7-8 frames of post-click state without flooding the console at
        // idle. GameScene end-of-FillGameObjectList + end-of-DrawUI write
        // these fields; [click-recv] reads them so we can correlate the
        // value at click time with the value at the end of the prior render
        // pass.
        public static uint   _wasmTraceUntilTicks;
        public static uint   _wasmTraceClickTicks;
        public static string _lastFillEndKind = "(uninit)";
        public static uint   _lastFillEndTick;
        public static string _lastDrawUiEndKind = "(uninit)";
        public static uint   _lastDrawUiEndTick;

        // P4d.7 — drain DOM-originated input events out of the C ring
        // buffer in source/webclient/native-shims/SDL3.c and dispatch each via
        // HandleSdlEvent. No reverse-PInvoke from SDL's own queue
        // (that path crashed the Mono interpreter — see
        // tools/test-bot-memory.md "P4d.7 attempt"); instead JS calls
        // wasm_push_* directly, and we pull on every Update tick.
        private const int _wasmEventSize    = 128;   // matches SDL_Event padded size
        private const int _wasmDrainBatch   = 32;    // events per drain pass
        private long      _wasmInputEventsDispatched = 0;

        [DllImport("SDL3", EntryPoint = "sdl3_drain_events", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sdl3_drain_events(byte* buf, int max);

        // Native bridge that calls `globalThis.__wasm_flush_idbfs` via
        // EM_ASM inside source/webclient/native-shims/SDL3.c. Used by the auto-save
        // tick below to persist CUO profile data (gump positions,
        // settings, macros) from MEMFS -> IndexedDB so a reload finds
        // it. Runs on Mono's main thread — safe vs the "Cannot call
        // synchronous C# methods" trap that hits JSExport from JS.
        [DllImport("SDL3", EntryPoint = "wasm_flush_idbfs", CallingConvention = CallingConvention.Cdecl)]
        private static extern void wasm_flush_idbfs();

        // (Nothing here — FlushIdbfs is defined as a static helper
        // below at class-scope so callers can use it unconditionally
        // on both wasm + desktop. See the `FlushIdbfs` partial block.)

        private void PumpWasmInput()
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
                        // One-shot milestone trace so the bot knows the
                        // DOM -> managed input bridge is live (pattern
                        // matched by tools/test-bot.py events_wired heuristic).
                        WasmTrace.W(
                            $"[cuo-trace] kb: first DOM input event dispatched, type=0x{ev->type:X}");
                    }
                    _wasmInputEventsDispatched++;
                }
                if (n < _wasmDrainBatch) break;
            }
        }
#endif

        // Cross-platform IDBFS flush helper. On wasm, calls the
        // [DllImport(wasm_flush_idbfs)] bridge which in turn pokes
        // globalThis.__wasm_flush_idbfs → Module.FS.syncfs. On
        // desktop this is a no-op (nothing to flush). Lets callers
        // use `GameController.FlushIdbfs()` unconditionally without
        // scattering #if BROWSER_WASM blocks at every save site.
        // Bug O2 — prior to this, only the 5 s auto-save tick
        // flushed, so any write between ticks could be lost on
        // tab close.
        public static void FlushIdbfs()
        {
#if BROWSER_WASM
            try { wasm_flush_idbfs(); } catch { /* best-effort */ }
#endif
        }

#if BROWSER_WASM
        // Shared with the auto-save block in Update(). Setting this
        // to true makes the next Update tick save the current profile
        // + macros + infobars and FlushIdbfs() immediately instead of
        // waiting for the 5 s accumulator. Used by drag-end sites and
        // WorldMapGump.SaveSettings that mutate persistent state but
        // don't write to disk themselves — closes the "drag+reload
        // within 5 s = change lost" leg of bug O2.
        internal static bool _wasmForceNextAutoSave;
#endif

        // Fire-and-forget request that the next Update tick persists
        // the current profile + flushes IDBFS. Safe on desktop (no-op).
        // Bug O2 remainder — the three explicit call sites (OptionsGump
        // apply, GameScene logout, LastCharacterManager) already flush
        // inline; use this helper for implicit persistence (drag-end
        // on any gump, WorldMapGump setting toggles) where the mutator
        // doesn't write to disk itself.
        public static void RequestImmediateAutoSave()
        {
#if BROWSER_WASM
            // v0.3.34: 2 s debounce so a flurry of drag-ends (gump shuffle,
            // multi-tab macro edits) coalesces into one forced save instead
            // of N. Drops the Update-tick save burst the operator's log
            // captured at GameScene init.
            long nowMs = Environment.TickCount64;
            if (nowMs - _wasmLastForcedSaveMs < _wasmForceSaveDebounceMs) return;
            _wasmLastForcedSaveMs = nowMs;
            _wasmForceNextAutoSave = true;
#endif
        }

        public GameController(IPluginHost pluginHost)
        {
            GraphicManager = new GraphicsDeviceManager(this);

            GraphicManager.PreparingDeviceSettings += (sender, e) =>
            {
                e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage =
                    RenderTargetUsage.DiscardContents;
            };

            GraphicManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;
            SetVSync(false);

            Window.ClientSizeChanged += WindowOnClientSizeChanged;
            Window.AllowUserResizing = true;
            Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
            IsMouseVisible = Settings.GlobalSettings.RunMouseInASeparateThread;

            IsFixedTimeStep = false; // Settings.GlobalSettings.FixedTimeStep;
            TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 250.0);
            PluginHost = pluginHost;
        }

        public Scene Scene { get; private set; }
        public AudioManager Audio { get; private set; }
        public UltimaOnline UO { get; } = new UltimaOnline();
        public IPluginHost PluginHost { get; private set; }
        public GraphicsDeviceManager GraphicManager { get; }

        public Rectangle ClientBounds
        {
            get
            {
                var window_rectangle = Window.ClientBounds;
                return new Rectangle(
                    window_rectangle.X,
                    window_rectangle.Y,
                    (int)((float)(window_rectangle.Width) / DpiScale),
                    (int)((float)(window_rectangle.Height) / DpiScale)
                );
            }
        }

        public readonly uint[] FrameDelay = new uint[2];

        private readonly List<(uint, Action)> _queuedActions = new ();

        public void EnqueueAction(uint time, Action action)
        {
            _queuedActions.Add((Time.Ticks + time, action));
        }

        protected override void Initialize()
        {
            WasmTrace.W("[cuo-trace] GameController.Initialize enter");
#if !BROWSER_WASM
            // HiDef profile + ApplyChanges recreates the GraphicsDevice
            // which WebGL/Emscripten-SDL2 handles poorly under our
            // wasm bring-up (p4_cuo hits an OOB trap after FNA3D's
            // glsles3 log). Desktop builds keep the original path.
            if (GraphicManager.GraphicsDevice.Adapter.IsProfileSupported(GraphicsProfile.HiDef))
            {
                GraphicManager.GraphicsProfile = GraphicsProfile.HiDef;
            }

            GraphicManager.ApplyChanges();
#endif
            WasmTrace.W("[cuo-trace] HiDef block done");

            SetRefreshRate(Settings.GlobalSettings.FPS);
            WasmTrace.W("[cuo-trace] SetRefreshRate done");
            _uoSpriteBatch = new UltimaBatcher2D(GraphicsDevice);
            WasmTrace.W("[cuo-trace] UltimaBatcher2D done");

            _filter = HandleSdlEvent;
#if BROWSER_WASM
            // Mono-WASM's reverse-PInvoke marshalling for delegates
            // (here: SDL_EventFilter -> fn pointer) traps with a WASM
            // OOB before the sdl3_* stub is even reached. Event routing
            // to HandleSdlEvent will be re-wired via a JS bridge in a
            // later milestone; for now we just skip the register so
            // Initialize can complete and the login scene can render.
            WasmTrace.W("[cuo-trace] skipping SDL_SetEventFilter on WASM");
#else
            WasmTrace.W("[cuo-trace] about to SDL_SetEventFilter");
            SDL_SetEventFilter(_filter, IntPtr.Zero);
            WasmTrace.W("[cuo-trace] SDL_SetEventFilter done");
#endif

            Microsoft.Xna.Framework.Input.TextInputEXT.StartTextInput();
            WasmTrace.W("[cuo-trace] StartTextInput done");

            _displayScale = DpiScale;
            WasmTrace.W("[cuo-trace] DpiScale got");

            base.Initialize();
            WasmTrace.W("[cuo-trace] GameController.Initialize exit");
        }

        protected override void LoadContent()
        {
            WasmTrace.W("[cuo-trace] LoadContent enter");
            base.LoadContent();
            WasmTrace.W("[cuo-trace] base.LoadContent done");

            Fonts.Initialize(GraphicsDevice);
            WasmTrace.W("[cuo-trace] Fonts.Initialize done");
            SolidColorTextureCache.Initialize(GraphicsDevice);
            WasmTrace.W("[cuo-trace] SolidColorTextureCache.Initialize done");
            Audio = new AudioManager();
            WasmTrace.W("[cuo-trace] new AudioManager done");

            var bytes = Loader.GetBackgroundImage().ToArray();
            WasmTrace.W($"[cuo-trace] background PNG bytes = {bytes.Length}");
            using var ms = new MemoryStream(bytes);
            var bgTex = Texture2D.FromStream(GraphicsDevice, ms);
            WasmTrace.W($"[cuo-trace] background texture = {bgTex.Width}x{bgTex.Height}");
            _renderTargets.InitializeBackground(bgTex);
            WasmTrace.W("[cuo-trace] InitializeBackground done");
#if false
            SetScene(new MainScene(this));
#else
            UO.Load(this);
            WasmTrace.W("[cuo-trace] UO.Load done");
            // AudioManager on wasm routes to Web Audio via the bridge
            // added in main.js + source/webclient/native-shims/SDL3.c (wasm_play_
            // pcm/music). Safe to Initialize now — no FAudio P/Invoke
            // happens anymore in the wasm code path (Sound.cs
            // BROWSER_WASM branch bypasses DynamicSoundEffectInstance).
            Audio.Initialize();
            WasmTrace.W("[cuo-trace] Audio.Initialize done");
            // TODO: temporary fix to avoid crash when laoding plugins
            WasmTrace.W("[cuo-trace] about to NetClient.Socket.Load");
            Settings.GlobalSettings.Encryption = (byte) NetClient.Socket.Load(UO.FileManager.Version, (EncryptionType) Settings.GlobalSettings.Encryption);
            WasmTrace.W("[cuo-trace] NetClient.Socket.Load done");

            Log.Trace("Loading plugins...");
            PluginHost?.Initialize();
            WasmTrace.W("[cuo-trace] PluginHost.Initialize done");

            foreach (string p in Settings.GlobalSettings.Plugins)
            {
                Plugin.Create(p);
            }
            _pluginsInitialized = true;
            WasmTrace.W("[cuo-trace] Plugins loaded");

            Log.Trace("Done!");

            WasmTrace.W("[cuo-trace] about to SetScene(LoginScene)");
            SetScene(new LoginScene(UO.World));
            WasmTrace.W("[cuo-trace] SetScene done");
#endif
#if BROWSER_WASM
            // Regression canary for the wasm RenderTarget2D pipeline.
            // Reads `[fbo-test] verdict=...` in console.txt — must
            // stay PASS or the world / lights / UI render-target
            // composite path is broken.
            ClassicUO.Diagnostics.FboFunctionalTest.Run(GraphicsDevice, _uoSpriteBatch);
#endif
            SetWindowPositionBySettings();
        }

        protected override void UnloadContent()
        {
            SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);

            Settings.GlobalSettings.WindowPosition = new Point(
                Math.Max(0, Window.ClientBounds.X - left),
                Math.Max(0, Window.ClientBounds.Y - top)
            );

            Audio?.StopMusic();
            Settings.GlobalSettings.Save();
            Plugin.OnClosing();

            UO.Unload();

            base.UnloadContent();
        }

        public void SetWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
#if DEV_BUILD
                Window.Title = $"ClassicUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
#endif
            }
            else
            {
#if DEV_BUILD
                Window.Title = $"{title} - ClassicUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"{title} - ClassicUO - {CUOEnviroment.Version}";
#endif
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetScene<T>() where T : Scene
        {
            return Scene as T;
        }

        public void SetScene(Scene scene)
        {
            Scene?.Dispose();
            Scene = scene;
            Scene?.Load();
        }

        public void SetVSync(bool value)
        {
            GraphicManager.SynchronizeWithVerticalRetrace = value;
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

        private void SetWindowPosition(int x, int y)
        {
            SDL_SetWindowPosition(Window.Handle, x, y);
        }

        public void SetWindowSize(int width, int height)
        {
            //width = (int) ((double) width * Client.Game.GraphicManager.PreferredBackBufferWidth / Client.Game.Window.ClientBounds.Width);
            //height = (int) ((double) height * Client.Game.GraphicManager.PreferredBackBufferHeight / Client.Game.Window.ClientBounds.Height);

            /*if (CUOEnviroment.IsHighDPI)
            {
                width *= 2;
                height *= 2;
            }
            */

            GraphicManager.PreferredBackBufferWidth = width;
            GraphicManager.PreferredBackBufferHeight = height;
            GraphicManager.ApplyChanges();
        }

        public void SetWindowBorderless(bool borderless)
        {
            SDL_WindowFlags flags = (SDL_WindowFlags)SDL_GetWindowFlags(Window.Handle);

            if ((flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) != 0 && borderless)
            {
                return;
            }

            if ((flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) == 0 && !borderless)
            {
                return;
            }

            SDL_SetWindowBordered(
                Window.Handle,
                !borderless
            );
            SDL_DisplayMode* displayMode = (SDL_DisplayMode * )SDL_GetCurrentDisplayMode(
                SDL_GetDisplayForWindow(Window.Handle)
            );

            int width = displayMode->w;
            int height = displayMode->h;

            if (borderless)
            {
                SetWindowSize(width, height);
                SDL_GetDisplayUsableBounds(
                    SDL_GetDisplayForWindow(Window.Handle),
                    out SDL_Rect rect
                );
                SDL_SetWindowPosition(Window.Handle, rect.x, rect.y);
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
        }

        public void MaximizeWindow()
        {
            SDL_MaximizeWindow(Window.Handle);

            GraphicManager.PreferredBackBufferWidth = Client.Game.Window.ClientBounds.Width;
            GraphicManager.PreferredBackBufferHeight = Client.Game.Window.ClientBounds.Height;
            GraphicManager.ApplyChanges();
        }

        public bool IsWindowMaximized()
        {
            SDL_WindowFlags flags = (SDL_WindowFlags)SDL_GetWindowFlags(Window.Handle);

            return (flags & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0;
        }

        public void RestoreWindow()
        {
            SDL_RestoreWindow(Window.Handle);
        }

        public void SetWindowPositionBySettings()
        {
            var borderSizesRetrieved = SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);

            if (!borderSizesRetrieved)
            {
                top = 0;
                left = 0;
            }

            if (Settings.GlobalSettings.WindowPosition.HasValue)
            {
                int x = left + Settings.GlobalSettings.WindowPosition.Value.X;
                int y = top + Settings.GlobalSettings.WindowPosition.Value.Y;
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                SDL_Point desiredStartPoint = new() { x = x, y = y };
                var displayId = SDL_GetDisplayForPoint(ref desiredStartPoint);
                if (displayId <= 0)
                {
                    // Make sure the window is actually in view and not out of bounds
                    SetWindowPosition(left, top);
                }

                var boundsRetrieved = SDL_GetDisplayUsableBounds(displayId, out SDL_Rect displayBounds);
                if (!boundsRetrieved)
                {
                    return; // we have no clue - the user is unfortunately on their own
                }

                if (x < displayBounds.x || x >= displayBounds.x + displayBounds.w)
                {
                    // Make sure the window is actually in view and not out of bounds
                    x = left + displayBounds.x;
                }

                if (y < displayBounds.y || y >= displayBounds.y + displayBounds.h)
                {
                    y = top + displayBounds.y;
                }

                SetWindowPosition(x, y);
            }
        }

        protected override void Update(GameTime gameTime)
        {
#if BROWSER_WASM
            // v0.3.30 frame-time monitor — fires per frame, allocation-free
            // unless a long-frame actually triggers. Window-aggregated count
            // (resets every 60s) so the operator sees "spike rate" rather
            // than a single isolated event.
            // Environment.TickCount64 is the .NET runtime's wall-clock ms
            // counter — stable in Mono WASM, no JSInterop needed. Avoids
            // gameTime.ElapsedGameTime which can lie when FNA's variable
            // timestep is active (returns the requested target, not the
            // measured wall delta).
            long nowWallMs = Environment.TickCount64;
            if (_wasmLastUpdateWallMs > 0) {
                double delta = nowWallMs - _wasmLastUpdateWallMs;
                if (delta >= FRAME_LONG_MS) {
                    _wasmLongFrameCount60s++;
                    if (_wasmLongFrameWindowStartMs == 0)
                        _wasmLongFrameWindowStartMs = nowWallMs;
                    // v0.4.40: Console.WriteLine (= console.log on JS side)
                    // instead of Console.Error.WriteLine. The frame-hitch
                    // counter is informational, not an error; tagging it
                    // [error] in the silencer tag wrapper polluted external
                    // monitoring (anything filtering by console.error sees
                    // what looks like real errors). console.log passes
                    // through baseLog untagged; ?dev gate still silences
                    // in production via the silencer's noop branch.
                    // v0.4.48: include C#-Update / C#-Draw / js-gap split so
                    // the operator can tell at a glance whether the lag is
                    // in C# main-frame work or in browser-side GPU/compositor.
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
                    && nowWallMs - _wasmLongFrameWindowStartMs > 60_000) {
                    Console.WriteLine(
                        $"[perf] long-frame window: {_wasmLongFrameCount60s} hitches >{FRAME_LONG_MS}ms in last 60s");
                    _wasmLongFrameCount60s = 0;
                    _wasmLongFrameWindowStartMs = 0;
                }
                // v0.8.94 [fps]: 5 s aggregate so sessions are comparable
                // without opening the Debug gump. Allocation-free per frame
                // (two longs + a max); one Console.WriteLine per 5 s.
                _wasmFpsFrames++;
                if (delta > _wasmFpsWorstMs) _wasmFpsWorstMs = delta;
                if (_wasmFpsWindowStartMs == 0) _wasmFpsWindowStartMs = nowWallMs;
                long fpsElapsed = nowWallMs - _wasmFpsWindowStartMs;
                if (fpsElapsed >= 5_000)
                {
                    double avgFps = _wasmFpsFrames * 1000.0 / fpsElapsed;
                    double avgMs = (double)fpsElapsed / _wasmFpsFrames;
                    Console.WriteLine($"[fps] avg={avgFps:F1} frameAvg={avgMs:F1}ms worst={_wasmFpsWorstMs:F0}ms frames={_wasmFpsFrames}/5s");
                    _wasmFpsFrames = 0;
                    _wasmFpsWorstMs = 0;
                    _wasmFpsWindowStartMs = nowWallMs;
                }
            }
            _wasmLastUpdateWallMs = nowWallMs;

            // Per-frame cuo-trace is OFF in production — the logs were
            // left in from the bring-up phase but at ~10 fps and ~15
            // trace lines per Draw they saturated DevTools and pegged
            // the Mono GC with string allocations, crashing the tab
            // after ~30 seconds. Flip `t` / `tr` to true ONLY for
            // targeted bring-up debugging (and expect the crash-after-
            // seconds symptom to return while it's on).
            // DIAGNOSTIC flag for deputy-thread deadlock hunts.
            // Flip to true only when investigating a freeze — at 60 fps
            // × 7 trace lines × Console.Error.WriteLine (JSInterop +
            // string alloc) it costs ~420 logs/s and causes visible
            // stutter + DevTools saturation. OFF in production.
            bool t = false;
            _wasmUpdateTraceRemaining++;

            // P4d.7 — drain DOM-originated input events into
            // HandleSdlEvent BEFORE Mouse.Update(), so clicks that
            // arrived this frame can update Mouse state (button
            // bitmask, click position) in time for the scene update.
            PumpWasmInput();

            // Auto-save: browser tab-close doesn't reliably fire
            // GameScene.Unload (the desktop path that runs the only
            // in-game Profile.Save). Persist every 30 s of game time
            // while in GameScene so gump positions, settings, macros
            // written to /Data actually land in IndexedDB via the JS
            // IDBFS flush.
            _wasmAutoSaveAccumMs += gameTime.ElapsedGameTime.TotalMilliseconds;
            if ((_wasmAutoSaveAccumMs >= _wasmAutoSaveIntervalMs || _wasmForceNextAutoSave) &&
                Scene?.GetType().Name == "GameScene" &&
                Configuration.ProfileManager.CurrentProfile != null &&
                !string.IsNullOrEmpty(Configuration.ProfileManager.ProfilePath))
            {
                _wasmAutoSaveAccumMs = 0;
                _wasmForceNextAutoSave = false;
                // v0.4.13 hard floor: skip the actual save if the previous
                // one fired less than _wasmMinSaveIntervalMs ago. Prevents
                // the interval+force two-tick race that captured saves
                // ~16 ms apart in the v0.4.12 log. Pending changes wait
                // for the next trigger; max-loss-on-tab-close is bounded
                // by the 30 s auto-save tick (unchanged).
                long _saveNowMs = Environment.TickCount64;
                if (_saveNowMs - _wasmLastActualSaveMs >= _wasmMinSaveIntervalMs)
                {
                    _wasmLastActualSaveMs = _saveNowMs;
                    try
                    {
                        Configuration.ProfileManager.CurrentProfile.Save(
                            UO.World,
                            Configuration.ProfileManager.ProfilePath);
                        try { UO.World.Macros?.Save(); } catch { }
                        try { UO.World.InfoBars?.Save(); } catch { }
                        try { wasm_flush_idbfs(); } catch { }
                        WasmTrace.W("[cuo-trace] auto-save profile + IDBFS flush");
                    }
                    catch (Exception ex)
                    {
                        WasmTrace.W($"[cuo-trace] auto-save threw: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
#endif
            if (Profiler.InContext(Profiler.ProfilerContext.OUT_OF_CONTEXT))
            {
                Profiler.ExitContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);
            }

            Time.Ticks = (uint)gameTime.TotalGameTime.TotalMilliseconds;
            Time.Delta = (float)gameTime.ElapsedGameTime.TotalSeconds;

            Mouse.Update();
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update Mouse.Update done");
#endif

            var data = NetClient.Socket.CollectAvailableData();
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update CollectAvailableData done");
#endif
            var packetsCount = PacketHandlers.Handler.ParsePackets(NetClient.Socket, UO.World, data);
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update ParsePackets done");
#endif

            NetClient.Socket.Statistics.TotalPacketsReceived += (uint)packetsCount;
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update pre-Flush");
#endif
            NetClient.Socket.Flush();
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update Flush done");
#endif

            Plugin.Tick();
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update Plugin.Tick done");
#endif

            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Profiler.EnterContext(Profiler.ProfilerContext.UPDATE_WORLD);
                Scene.Update();
                Profiler.ExitContext(Profiler.ProfilerContext.UPDATE_WORLD);
            }
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update Scene.Update done");
#endif

            UIManager.Update();
#if BROWSER_WASM
            if (t) WasmTrace.W("[cuo-trace] Update UIManager.Update done");
#endif

            _totalElapsed += gameTime.ElapsedGameTime.TotalMilliseconds;
            _currentFpsTime += gameTime.ElapsedGameTime.TotalMilliseconds;

            if (_currentFpsTime >= 1000)
            {
                CUOEnviroment.CurrentRefreshRate = _totalFrames;

                _totalFrames = 0;
                _currentFpsTime = 0;
            }

            _suppressedDraw = false;

#if !BROWSER_WASM
            double x = _intervalFixedUpdate[
                !IsActive
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.ReduceFPSWhenInactive
                    ? 1
                    : 0
            ];
#endif

#if BROWSER_WASM
            // v0.3.32 lag fix. Under WASM the browser's requestAnimationFrame
            // is the natural frame governor — it caps at the display refresh
            // (60/120/144Hz). The desktop wall-clock SuppressDraw branch
            // below was designed for an unbounded fixed-timestep loop and
            // becomes a beat-frequency stutter generator under rAF.
            //
            // Concrete repro on a 144Hz monitor with FPS=60:
            //   _intervalFixedUpdate[0] = 1000/60 = 16.67 ms
            //   rAF tick = 1000/144 ≈ 6.94 ms
            //   _totalElapsed += 6.94 each Update; Draw fires when > 16.67.
            //   Pattern: 3 ticks (20.8 ms) → Draw, 2 ticks (13.9 ms) → skip,
            //   3 ticks → Draw, 2 ticks → skip … i.e. alternating 21/14 ms
            //   gaps between rendered frames — visible judder masquerading as
            //   "60 fps but feels less smooth than the counter says".
            //
            // The bot-perf-walk.mjs run on 2026-05-05 confirmed: rAF intervals
            // were perfectly clean (avg 6.95 ms, max 19.31 ms across 13.9k
            // samples on uonexus.com) so there is NO main-thread spike to
            // blame — only this software cap. The engine [perf] long-frame
            // monitor never fired (threshold 20 ms; max measured 19.31 ms),
            // which is exactly what we'd expect when the only "lag" is the
            // intentional Draw skip every other tick.
            //
            // Fix: never suppress Draw under WASM. Let it render once per
            // rAF, which is uniform by definition. Update keeps Profile.FPS
            // as the simulation rate hint via TargetElapsedTime; Draw is
            // free-running synced to vsync. Thread.Sleep(1) on Mono WASM is
            // also unnecessary (and not a real sleep on single-thread).
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

            UO.GameCursor?.Update();
            Audio?.Update();


            for (var i = _queuedActions.Count - 1; i >= 0; i--)
            {
                (var time, var fn) = _queuedActions[i];

                if (Time.Ticks > time)
                {
                    fn();
                    _queuedActions.RemoveAt(i);
                    break;
                }
            }

             base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
#if BROWSER_WASM
            bool tr = false; // see Update() for why this is off
            _wasmDrawTraceRemaining++;
            // Heartbeat every 60 draws, with hi-res every 5 draws in the
            // 360..500 window (SGen OOB reproduces ~Draw#420 post-char-
            // select — we need the exact crash frame). Logs managed-heap
            // size + delta-ms since previous heartbeat so an allocation
            // spike / stall is visible.
            bool beat = (_wasmDrawTraceRemaining % 60 == 0) ||
                        (_wasmDrawTraceRemaining >= 360 && _wasmDrawTraceRemaining <= 500 && _wasmDrawTraceRemaining % 5 == 0);
            if (beat)
            {
                long heap = GC.GetTotalMemory(false);
                uint now = Time.Ticks;
                uint dMs = _wasmLastHeartbeatMs == 0 ? 0 : (now - _wasmLastHeartbeatMs);
                _wasmLastHeartbeatMs = now;
                // Fires `cuo:draw-heartbeat` every 60 frames during
                // gameplay. main.js uses it as the belt-and-suspenders
                // BOOT_OK fallback if the login-gump-added signal got
                // missed. Direct C# -> JS, no console sniff.
                WasmSignal.Send("draw-heartbeat");
            }
#endif
            _renderTargets.EnsureSizes(
                GraphicsDevice,
                new Rectangle(0, 0, GraphicManager.PreferredBackBufferWidth, GraphicManager.PreferredBackBufferHeight),
                Scene.Camera.Bounds,
                DpiScale
            );
#if BROWSER_WASM
            if (tr) {
                var ui = _renderTargets.UiRenderTarget;
                var bb = GraphicManager;
                WasmTrace.W(
                    $"[cuo-trace] Draw EnsureSizes done  " +
                    $"UiRT={(ui == null ? "null" : $"{ui.Width}x{ui.Height}")}  " +
                    $"BackBuffer={bb.PreferredBackBufferWidth}x{bb.PreferredBackBufferHeight}  " +
                    $"ClientBounds={Window.ClientBounds.Width}x{Window.ClientBounds.Height}  " +
                    $"DpiScale={DpiScale}");
            }
#endif

            Profiler.EndFrame();
            Profiler.BeginFrame();

            if (Profiler.InContext(Profiler.ProfilerContext.OUT_OF_CONTEXT))
            {
                Profiler.ExitContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);
            }

            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME);

            _totalFrames++;

            GraphicsDevice.Clear(Color.Black);
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw Clear(Black) done");
#endif

            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.Draw(_uoSpriteBatch, _renderTargets);
            }
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw Scene.Draw done");
#endif

            // 2026-04-26: wasm runs the same UiRT bind + composite as
            // desktop. Root cause of the prior FBO-no-op symptom was
            // -DMOJOSHADER_FLIP_RENDERTARGET missing from the wasm
            // FNA3D / MojoShader compile flags (see
            // wasm-fna-native-mercury.targets._FnaCompileFlags).
            _uoSpriteBatch.GraphicsDevice.SetRenderTarget(_renderTargets.UiRenderTarget);
            GraphicsDevice.Clear(Color.Transparent);
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw SetRenderTarget(UI) + Clear done");
#endif

            if ((UO.World?.InGame ?? false) && SelectedObject.Object is TextObject t)
            {
                if (t.IsTextGump)
                {
                    t.ToTopD();
                }
                else
                {
                    UO.World.WorldTextManager?.MoveToTop(t);
                }
            }

            SelectedObject.HealthbarObject = null;
            SelectedObject.SelectedContainer = null;
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw reset SelectedObject done");
#endif

            _uoSpriteBatch.Begin();
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw spriteBatch.Begin done");
#endif
            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.DrawUI(_uoSpriteBatch);
            }
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw Scene.DrawUI done");
#endif
            _uoSpriteBatch.End();
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw spriteBatch.End done");
#endif

            UIManager.Draw(_uoSpriteBatch);
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw UIManager.Draw done");
#endif

            _uoSpriteBatch.Begin();
            UO.GameCursor?.Draw(_uoSpriteBatch);
            _uoSpriteBatch.End();
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw cursor done");
#endif

            _uoSpriteBatch.GraphicsDevice.SetRenderTarget(null);

            _renderTargets.Draw(_uoSpriteBatch);
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw _renderTargets.Draw done");
#endif

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME);
            Profiler.EnterContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);

            Plugin.ProcessDrawCmdList(GraphicsDevice);
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw Plugin.ProcessDrawCmdList done");
#endif

            base.Draw(gameTime);
#if BROWSER_WASM
            if (tr) WasmTrace.W("[cuo-trace] Draw base.Draw done");
#endif
        }

        private float _screenScale = Settings.GlobalSettings.ScreenScale;
        public float ScreenScale {
            get => _screenScale;
            set {
                if (value != _screenScale) {
                    _screenScale = value;
                    UO.GameCursor?.CreateGraphic(DpiScale);
                }
            }
        }

        public float DpiScale
        {
            get => SDL_GetWindowDisplayScale(Window.Handle) * ScreenScale;
        }

        public int ScaleWithDpi(int value, float previousDpi = 1)
        {
            return (int)Math.Round((value / previousDpi) * DpiScale);
        }

        protected override bool BeginDraw()
        {
            return !_suppressedDraw && base.BeginDraw();
        }

        private void WindowOnClientSizeChanged(object sender, EventArgs e)
        {
            int width = Window.ClientBounds.Width;
            int height = Window.ClientBounds.Height;

            WindowOnClientSizeChanged(width, height);
        }

        private void WindowOnClientSizeChanged(int width, int height)
        {
            if (!IsWindowMaximized() && Window.AllowUserResizing)
            {
                if (ProfileManager.CurrentProfile != null)
                    ProfileManager.CurrentProfile.WindowClientBounds = new Point(width, height);
            }

            SetWindowSize(width, height);

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        private bool HandleSdlEvent(IntPtr userData, SDL_Event* sdlEvent)
        {
            // Don't pass SDL events to the plugin host before the plugins are initialized
            // or the garbage collector can get screwed up
            if (_pluginsInitialized && Plugin.ProcessWndProc(sdlEvent) != 0)
            {
                if ((SDL_EventType)sdlEvent->type == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
                {
                    if (UO.GameCursor != null)
                    {
                        UO.GameCursor.AllowDrawSDLCursor = false;
                    }
                }

                return true;
            }

            switch ((SDL_EventType)sdlEvent->type)
            {
                case SDL_EventType.SDL_EVENT_AUDIO_DEVICE_ADDED:
                    Console.WriteLine("AUDIO ADDED: {0}", sdlEvent->adevice.which);

                    break;

                case SDL_EventType.SDL_EVENT_AUDIO_DEVICE_REMOVED:
                    Console.WriteLine("AUDIO REMOVED: {0}", sdlEvent->adevice.which);

                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_ENTER:
                    Mouse.MouseInWindow = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                    Mouse.MouseInWindow = false;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                    Plugin.OnFocusGained();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    Plugin.OnFocusLost();
                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:

                    Keyboard.OnKeyDown(sdlEvent->key);

                    if (
                        Plugin.ProcessHotkeys(
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

                case SDL_EventType.SDL_EVENT_KEY_UP:

                    Keyboard.OnKeyUp(sdlEvent->key);
                    UIManager.KeyboardFocusControl?.InvokeKeyUp(
                        (SDL_Keycode)sdlEvent->key.key,
                        sdlEvent->key.mod
                    );
                    Scene.OnKeyUp(sdlEvent->key);
                    Plugin.ProcessHotkeys(0, 0, false);

                    // SDL_PRINTSCREEN screenshot capture intentionally
                    // disabled on the web build — webclient profile sync
                    // ships /Data/Profiles/ to the operator and we do
                    // NOT want users filling that storage with PNGs.
                    // The browser's native screenshot tooling stays
                    // available outside the canvas, so users aren't
                    // blocked from capturing the screen if they want.

                    break;

                case SDL_EventType.SDL_EVENT_TEXT_INPUT:

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

                    /* We get to do strlen ourselves! */
                    byte* ptr = sdlEvent->text.text;
                    while (*ptr != 0)
                    {
                        ptr++;
                    }

                    string s = System.Text.Encoding.UTF8.GetString(
                        sdlEvent->text.text,
                        (int)(ptr - sdlEvent->text.text)
                    );

                    if (!string.IsNullOrEmpty(s))
                    {
                        UIManager.KeyboardFocusControl?.InvokeTextInput(s);
                        Scene.OnTextInput(s);
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:

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

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    Mouse.Update();
                    bool isScrolledUp = sdlEvent->wheel.y > 0;

                    Plugin.ProcessMouse(0, (int)sdlEvent->wheel.y);

                    if (!Scene.OnMouseWheel(isScrolledUp))
                    {
                        UIManager.OnMouseWheel(isScrolledUp);
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                {
                    SDL_MouseButtonEvent mouse = sdlEvent->button;

                    // The values in MouseButtonType are chosen to exactly match the SDL values
                    MouseButtonType buttonType = (MouseButtonType)mouse.button;

                    // v0.4.18 [click-recv] diag: log every click that
                    // reaches CUO with what's currently under the cursor.
                    // Sources of "click does nothing" the user reported:
                    //   1. Click never reaches CUO → no [click-recv] line
                    //      at all → blame DOM input bridge / SDL ring.
                    //   2. [click-recv] fires with cursor_target=null →
                    //      hit-test returned no GameObject → either no
                    //      mob is rendered under cursor THIS frame OR the
                    //      hit-test (CheckMouseSelection) rejected it.
                    //   3. [click-recv] fires with a valid target but the
                    //      DC handler returns false → handler bug.
                    {
                        var _so = SelectedObject.Object;
                        string _soKind = _so switch {
                            Mobile mb => $"Mobile(serial=0x{mb.Serial:X8} graphic=0x{mb.Graphic:X4})",
                            Item it => $"Item(serial=0x{it.Serial:X8} graphic=0x{it.Graphic:X4})",
                            Static st => $"Static(graphic=0x{st.Graphic:X4})",
                            Land ld => $"Land(graphic=0x{ld.Graphic:X4})",
                            Multi mt => $"Multi(graphic=0x{mt.Graphic:X4})",
                            TextObject txt => $"TextObject(owner={(txt.Owner is Entity e ? $"0x{e.Serial:X8}" : "non-entity")})",
                            null => "null",
                            _ => _so.GetType().Name,
                        };
                        // v0.4.19 [click-recv] extended: also dump UI-gating
                        // state (MouseOverControl, DraggingControl, IsMouseOverWorld)
                        // and the last frame's fill-pass result. The pair-pattern
                        // (1st click hits Mobile, 2nd click 130 ms later sees null
                        // at same pos) implies SOMETHING between the two pumps
                        // is nullifying SelectedObject. Either the LAST render's
                        // FillGameObjectList didn't repopulate it, or DrawUI's
                        // !IsMouseOverWorld branch nulled it. These extra fields
                        // tell us which.
                        var _moc = UIManager.MouseOverControl;
                        var _dc  = UIManager.DraggingControl;
                        bool _imow = UIManager.IsMouseOverWorld;
                        string _mocKind = _moc?.GetType().Name ?? "null";
                        string _dcKind  = _dc?.GetType().Name  ?? "null";
                        var _lastDown = SelectedObject.LastLeftDownObject;
                        string _lastDownKind = _lastDown switch {
                            Mobile mb2 => $"Mobile(0x{mb2.Serial:X8})",
                            Item it2 => $"Item(0x{it2.Serial:X8})",
                            null => "null",
                            _ => _lastDown.GetType().Name,
                        };
                        Console.WriteLine($"[click-recv] btn={buttonType} ticks={Time.Ticks} pos=({Mouse.Position.X},{Mouse.Position.Y}) target={_soKind} lastDown={_lastDownKind} IsMouseOverWorld={_imow} MouseOverControl={_mocKind} DraggingControl={_dcKind} LBtnDown={Mouse.LButtonPressed} fillEnd={_lastFillEndKind} fillEndTick={_lastFillEndTick} drawUiEnd={_lastDrawUiEndKind} drawUiEndTick={_lastDrawUiEndTick}");
                        // Arm the [fill-end] / [drawui-end] per-frame trace
                        // for the next ~250 ms so we capture the next ~7 frames
                        // of state between this click and the next one. Idle
                        // frames are silent.
                        _wasmTraceUntilTicks = (uint)(Time.Ticks + 250);
                        _wasmTraceClickTicks = (uint)Time.Ticks;

                        // v0.8.92 [tile-stack]: missing-statics hunt. When the
                        // operator clicks an "empty" spot where a building
                        // should be, the pick falls through to the Land behind
                        // — but that alone can't distinguish "static absent
                        // from the chunk" (load bug) from "in the chunk but
                        // not rendered" (visibility bug), because CUO's mouse
                        // picking only sees DRAWN objects. So on every world
                        // click, dump the engine's COMPLETE object stack at
                        // the clicked tile, drawn or not, with Z + the
                        // AllowedToDraw verdict. Compare against the offline
                        // statics0.mul decode for the same tile to corner the
                        // bug. Console.WriteLine (WasmTrace is stripped in
                        // prod). Bounded to 32 entries; never throws.
                        if (_imow && _so is GameObject _go && !(_so is TextObject))
                        {
                            try
                            {
                                var _map = UO.World?.Map;
                                if (_map != null)
                                {
                                    int _tx = _go.X, _ty = _go.Y;
                                    var _chk = _map.GetChunk2(_tx >> 3, _ty >> 3, false);
                                    Console.WriteLine($"[tile-stack] tile=({_tx},{_ty}) chunk=({_tx >> 3},{_ty >> 3}) staticsLoaded={(_chk != null ? _chk.StaticsLoaded.ToString() : "chunk-NULL")} clicked={_soKind}");
                                    if (_chk != null)
                                    {
                                        int _n = 0;
                                        for (var _o = _chk.GetHeadObject(_tx & 7, _ty & 7); _o != null && _n < 32; _o = _o.TNext, _n++)
                                        {
                                            string _ok = _o switch
                                            {
                                                Mobile _m2 => $"Mobile 0x{_m2.Graphic:X4}",
                                                Item _i2 => $"Item 0x{_i2.Graphic:X4}",
                                                Static _s2 => $"Static 0x{_s2.Graphic:X4}",
                                                Multi _mu2 => $"Multi 0x{_mu2.Graphic:X4}",
                                                Land _l2 => $"Land 0x{_l2.Graphic:X4}",
                                                _ => _o.GetType().Name,
                                            };
                                            Console.WriteLine($"[tile-stack]   {_ok} Z={_o.Z} draw={_o.AllowedToDraw}");
                                        }
                                        if (_n == 0) Console.WriteLine("[tile-stack]   (tile list EMPTY)");
                                    }
                                }
                            }
                            catch (Exception _tsEx)
                            {
                                Console.WriteLine("[tile-stack] failed: " + _tsEx.Message);
                            }
                        }
                    }

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
                        // v0.4.14 dclick-diag: instrumentation for the user-
                        // reported "horse mount needs many double-click tries"
                        // bug. Captures the qualifying click delta and the
                        // handler outcome.
                        // v0.4.15: capture _dcDelta BEFORE resetting
                        // lastClickTime. v0.4.14 had this backwards (read
                        // after reset, so delta always == ticks). Lost
                        // signal in v0.4.14 logs.
                        uint _dcDelta = lastClickTime == 0 || lastClickTime == 0xFFFF_FFFF
                            ? 0
                            : ticks - lastClickTime;
                        lastClickTime = 0;

                        bool res =
                            Scene.OnMouseDoubleClick(buttonType)
                            || UIManager.OnMouseDoubleClick(buttonType);
                        Console.WriteLine($"[dclick-diag] btn={buttonType} ticks={ticks} delta={_dcDelta}ms threshold={Mouse.MOUSE_DELAY_DOUBLE_CLICK}ms QUALIFIED handler_res={res} pos=({Mouse.Position.X},{Mouse.Position.Y})");

                        if (!res)
                        {
                            if (!Scene.OnMouseDown(buttonType))
                            {
                                UIManager.OnMouseButtonDown(buttonType);
                            }
                        }
                        else
                        {
                            lastClickTime = 0xFFFF_FFFF;
                        }
                    }
                    else
                    {
                        // v0.4.14 dclick-diag: log when a click does NOT
                        // qualify so we can see if the user's second click
                        // arrived past the 350 ms threshold. missed_by < 100 ms
                        // means a small bump in MOUSE_DELAY_DOUBLE_CLICK would
                        // recover most of the failed mount attempts.
                        if (lastClickTime > 0 && (buttonType == MouseButtonType.Left || buttonType == MouseButtonType.Right))
                        {
                            uint _missDelta = ticks - lastClickTime;
                            Console.WriteLine($"[dclick-diag] btn={buttonType} ticks={ticks} delta={_missDelta}ms threshold={Mouse.MOUSE_DELAY_DOUBLE_CLICK}ms MISSED missed_by={_missDelta - Mouse.MOUSE_DELAY_DOUBLE_CLICK}ms pos=({Mouse.Position.X},{Mouse.Position.Y})");
                        }

                        if (
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

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                {
                    SDL_MouseButtonEvent mouse = sdlEvent->button;

                    // The values in MouseButtonType are chosen to exactly match the SDL values
                    MouseButtonType buttonType = (MouseButtonType)mouse.button;

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
                case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED:
                {
                    // when starting scaled, SDL will raise the scale changed event before the window has properly loaded and the previous scale set
                    if (_displayScale != 0 && _displayScale != DpiScale)
                    {
                        // The effective DPI scale has changed. SDL handles the window content automatically
                        // but we need to make sure to resize the window properly
                        // This is especially important when the window size is restricted, for example
                        // in the LoginScene
                        WindowOnClientSizeChanged(
                            Client.Game.ScaleWithDpi(Window.ClientBounds.Width, previousDpi: _displayScale),
                            Client.Game.ScaleWithDpi(Window.ClientBounds.Height, previousDpi: _displayScale)
                        );

                        SDL_GetWindowMinimumSize(Client.Game.Window.Handle, out int previousMinWidth, out int previousMinHeight);

                        SDL_SetWindowMinimumSize(
                            Client.Game.Window.Handle,
                            Client.Game.ScaleWithDpi(previousMinWidth, previousDpi: _displayScale),
                            Client.Game.ScaleWithDpi(previousMinHeight, previousDpi: _displayScale)
                        );

                        _displayScale = DpiScale;
                    }
                    break;
                }
            }

            return true;
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            Scene?.Dispose();

            base.OnExiting(sender, args);
        }

        // Screenshot-to-disk disabled on the web build. Profile-sync
        // uploads /Data/Profiles/ to the operator under the user's
        // Discord ID, and PNG-sized payloads would balloon both the
        // server disk and the per-user 10 MB profile cap. Users who
        // want a screenshot can still use the browser's native
        // capture (Win+Shift+S, etc.) on the canvas.
        private void TakeScreenshot()
        {
        }
    }
}
