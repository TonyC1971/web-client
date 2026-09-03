using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace ClassicUO.Wasm;

// Bridge that lets the JS host ask CUO to resize the backing store
// + world viewport to an arbitrary size. LoginScene keeps the canvas
// at 640x480 (LoginBackground is a 640x480 GumpPicTiled). Once
// GameScene is active the canvas tracks window.innerWidth /
// innerHeight so the world renders at true viewport resolution
// (no CSS stretch, no pixelation, correct mouse coords + camera).
//
// main.js calls this from the debounced window.resize listener.
// Routes through GameController.WasmResizeViewport which both
// calls SetWindowSize (→ SDL_SetWindowSize → canvas.width/height)
// AND expands WorldViewportGump to fill the new area (desktop
// CUO gates the second step behind a profile flag; on wasm we
// always want the maximize behaviour).
internal static partial class WasmViewport
{
    // Returns Task instead of void: under WasmEnableThreads=true
    // the .NET runtime refuses synchronous JS→C# crossings
    // ("Cannot call synchronous C# methods" — the deputy thread
    // can be mid-frame or mid-GC, so main.js cannot block waiting
    // on it). Returning a Task lets the runtime queue + marshal
    // the call to the deputy asynchronously. Main.js side must
    // `await` the call (already awaited inside the retry loop).
    [JSExport]
    internal static Task ResizeGame(int w, int h)
    {
        ResizeGameImpl(w, h);
        return Task.CompletedTask;
    }

    // NOTE: do NOT add a [JSExport] that main.js polls during boot
    // (e.g. an IsGameScene() probe). iter63 proved that marshalling a
    // [JSExport] to the deputy WHILE it is initialising hangs the deputy
    // (boot reaches the canvas transfer then never renders the LoginGump).
    // The in-world signal is PUSHed instead: C# LoginComplete emits
    // "gamescene-active" via the NON-BLOCKING WasmSignal (async shim), and
    // main.js's listener calls ResizeGame below — post-login, interop-safe.

    private static void ResizeGameImpl(int w, int h)
    {
        if (w < 640) w = 640;
        if (h < 480) h = 480;

        var game = ClassicUO.Client.Game;
        if (game == null)
        {
            return;
        }

        // Only resize once scene is GameScene — LoginScene needs
        // 640x480 for the fixed-coord login background. Scene-type
        // gate by NAME (not `is`) because AOT+trim has shown flaky
        // cross-assembly pattern matches on internal types.
        var sceneName = game.Scene?.GetType().Name ?? "null";
        if (sceneName != "GameScene")
        {
            return;
        }

        var gm = game.GraphicManager;
        int pbWBefore = gm.PreferredBackBufferWidth;
        int pbHBefore = gm.PreferredBackBufferHeight;
        game.SetWindowSize(w, h);

        // World viewport (WorldViewportGump) auto-size policy — user
        // directive 2026-04-22 amendment:
        //   * First call of the session AND the loaded profile's
        //     GameWindowSize is still the factory default → this is a
        //     fresh profile with no saved layout. Apply the 50 % default
        //     so the new user sees a reasonable game window framed by the
        //     chat / backpack / paperdoll border.
        //     ⚠️ TazUO's factory default is 800x680 (Profile.cs:290),
        //     NOT CUO's 600x480 (source/webclient/classicuo-wasm keeps
        //     600x480). Detecting the WRONG default here means a fresh
        //     tuo profile is treated as "user-sized" → the 50 % never
        //     applies and the 680-tall default OVERFLOWS a <680px canvas
        //     (the operator's guest-on-tuo overflow report, 2026-07-23).
        //     Re-verify this constant on every TazUO sync — see
        //     docs/WEB_FORK_ADAPTATIONS.md.
        //   * Profile carries a non-default GameWindowSize → the
        //     user already picked a size on a previous session (or
        //     `[gumps.xml]` was restored with their drag-resized
        //     WorldViewportGump). Leave the gump alone, only grow
        //     the backing-store canvas via SetWindowSize above so
        //     the browser-window-resize reshapes the surrounding
        //     black bars but not the actual game area.
        //   * Subsequent calls (resize-poll retries, browser window
        //     resize mid-session) → never touch the WorldViewportGump
        //     again. User may have drag-resized manually and we would
        //     override their change.
        bool wvpOk = false;
        int gwW = 0, gwH = 0;
        string wvpReason = "skipped";
        if (!_wasmInitialWvpSized)
        {
            _wasmInitialWvpSized = true;
            try
            {
                var profile = ClassicUO.Configuration.ProfileManager.CurrentProfile;
                bool isFreshProfile =
                    profile == null ||
                    (profile.GameWindowSize.X == 800 && profile.GameWindowSize.Y == 680);

                if (isFreshProfile)
                {
                    gwW = (int)(w * 0.5);
                    gwH = (int)(h * 0.5);
                    var wvp = ClassicUO.Game.Managers.UIManager.GetGump<ClassicUO.Game.UI.Gumps.WorldViewportGump>();
                    if (wvp != null)
                    {
                        wvp.ResizeGameWindow(new Microsoft.Xna.Framework.Point(gwW, gwH));
                        wvpOk = true;
                        wvpReason = "fresh-profile-50pct";
                    }
                    else
                    {
                        wvpReason = "fresh-profile-but-no-wvp-yet";
                    }
                }
                else
                {
                    gwW = profile.GameWindowSize.X;
                    gwH = profile.GameWindowSize.Y;
                    wvpReason = $"respecting-saved-{gwW}x{gwH}";
                }
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[viewport] wvp policy threw: {ex.GetType().Name} {ex.Message}");
                wvpReason = "exception";
            }
        }
        else
        {
            wvpReason = "subsequent-call";
        }

        System.Console.WriteLine($"[viewport] ResizeGame canvas={w}x{h} game={gwW}x{gwH} pb {pbWBefore}x{pbHBefore}->{gm.PreferredBackBufferWidth}x{gm.PreferredBackBufferHeight} wvp={wvpOk} reason={wvpReason}");
    }

    // Per-session flag — set true on the first time we land on
    // GameScene and apply the auto-size policy. Reset would be
    // new-session territory (page reload); we don't have a hook for
    // it, so a fresh Mono runtime gives us a fresh `false`.
    private static bool _wasmInitialWvpSized = false;
}
