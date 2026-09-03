using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game
{
    // Gameview-only screenshot capture for the browser client (operator 2026-06-23,
    // authorized screenshot revert). Reads the WORLD render target — that RT holds
    // ONLY the game-world scene (gumps, chat, the border and the cursor live in the
    // separate UiRenderTarget), so a capture can never include UI chrome — and
    // PNG-encodes it via the public Texture2D.SaveAsPng path (GetTextureData2D +
    // FNA3D_Image_SavePNG, the same readback FboFunctionalTest proves works on this
    // WASM/ES3 build). The base64 PNG is handed to JS by the [JSExport]
    // ClassicUO.Wasm.WasmRailBridge.CaptureGameview shim; the player's browser keeps
    // the last 5 in an OPFS ring-buffer and chooses which to upload (never automatic).
    //
    // This is NOT the disabled GameController.TakeScreenshot() (that wrote PNGs to
    // disk and was neutralised for abuse-prevention). Gated to in-game; returns ""
    // on any failure so a capture attempt can never throw into the JS bridge or crash
    // the render thread.
    public static class ScreenshotBridge
    {
        public static string CaptureGameviewB64()
        {
            try
            {
                var world = Client.Game?.UO?.World;
                if (world == null || !world.InGame) return string.Empty;

                RenderTarget2D rt = Client.Game?.RenderTargets?.WorldRenderTarget;
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
