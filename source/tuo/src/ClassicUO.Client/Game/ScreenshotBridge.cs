using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using ClassicUO.Game.Scenes;

namespace ClassicUO.Game
{
    // Gameview-only screenshot capture for the browser client (operator 2026-06-23,
    // authorized screenshot revert). Reads the GameScene world render target — that RT
    // is sized to Camera.Bounds and holds ONLY the game-world scene (gumps, chat, the
    // border and the cursor are composited separately), so a capture can never include
    // UI chrome — and PNG-encodes it via the public Texture2D.SaveAsPng path (the same
    // readback the disabled disk TakeScreenshot used, proven to work on this WASM/ES3
    // build). The base64 PNG is handed to JS by the [JSExport]
    // ClassicUO.Wasm.WasmRailBridge.CaptureGameview shim; the player's browser keeps the
    // last 5 in an OPFS ring-buffer and chooses which to upload (never automatic).
    //
    // This is NOT the disabled GameController.TakeScreenshot() (that wrote full-screen
    // PNGs to disk and was neutralised for abuse-prevention). Gated to in-game; returns
    // "" on any failure so a capture attempt can never throw into the JS bridge.
    public static class ScreenshotBridge
    {
        public static string CaptureGameviewB64()
        {
            try
            {
                if (World.Instance == null || !World.Instance.InGame) return string.Empty;

                GameScene scene = Client.Game?.GetScene<GameScene>();
                RenderTarget2D rt = scene?.WorldRenderTarget;
                if (rt == null || rt.IsDisposed) return string.Empty;

                int w = rt.Width, h = rt.Height;
                if (w <= 0 || h <= 0) return string.Empty;

                using var ms = new MemoryStream(Math.Max(1024, w * h / 4));
                rt.SaveAsPng(ms, w, h);
                if (ms.Length <= 0) return string.Empty;
                return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
