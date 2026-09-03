// SPDX-License-Identifier: BSD-2-Clause
//
// One-shot canary that proves writing to a custom RenderTarget2D
// actually lands pixels under our wasm build. Runs once at the end
// of GameController.LoadContent. Logs `[fbo-test] verdict=PASS` (or
// FAIL_DRAW / FAIL_CLEAR) to console.error so the bot's console.txt
// greps cleanly. Cheap (~16 KB transient + 2 draws).
//
// History:
//   2026-04-26 — first added; reproduced the FAIL_DRAW symptom
//   that justified -DMOJOSHADER_FLIP_RENDERTARGET being added to
//   the wasm FNA3D + MojoShader compile flags. Kept as a regression
//   guard so any change to the wasm build flag set surfaces here
//   before silently breaking the world / lights / UI render targets.

using System;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Diagnostics
{
    internal static class FboFunctionalTest
    {
        private static bool _ran;

        public static void Run(GraphicsDevice gd, UltimaBatcher2D batcher)
        {
            if (_ran)
            {
                return;
            }
            _ran = true;

            WasmTrace.W("[fbo-test] begin");

            PresentationParameters pp = gd.PresentationParameters;
            WasmTrace.W(
                $"[fbo-test] pp BackBufferFormat={pp.BackBufferFormat} " +
                $"DepthStencilFormat={pp.DepthStencilFormat} " +
                $"MultiSampleCount={pp.MultiSampleCount} " +
                $"RenderTargetUsage={pp.RenderTargetUsage}");

            RunVariant(
                "baseline", gd, batcher,
                pp.BackBufferFormat,
                pp.DepthStencilFormat,
                pp.MultiSampleCount,
                pp.RenderTargetUsage);

            WasmTrace.W("[fbo-test] end");
        }

        private static void RunVariant(
            string name,
            GraphicsDevice gd,
            UltimaBatcher2D batcher,
            SurfaceFormat surfaceFormat,
            DepthFormat depthFormat,
            int multiSampleCount,
            RenderTargetUsage usage)
        {
            const int Size = 64;
            const int QuadX = 16;
            const int QuadY = 16;
            const int QuadW = 32;
            const int QuadH = 32;

            string ctorTag =
                $"variant={name} fmt={surfaceFormat} depth={depthFormat} " +
                $"msaa={multiSampleCount} usage={usage}";

            RenderTarget2D rt = null;
            try
            {
                rt = new RenderTarget2D(gd, Size, Size, false, surfaceFormat, depthFormat, multiSampleCount, usage);
            }
            catch (Exception e)
            {
                WasmTrace.W($"[fbo-test] {ctorTag} CTOR_EXCEPTION: {e.GetType().Name}: {e.Message}");
                return;
            }

            Color[] afterClear = new Color[Size * Size];
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Red);
                gd.SetRenderTarget(null);
                rt.GetData<Color>(afterClear);
            }
            catch (Exception e)
            {
                WasmTrace.W($"[fbo-test] {ctorTag} CLEAR_EXCEPTION: {e.GetType().Name}: {e.Message}");
                rt.Dispose();
                return;
            }

            Color clearCenter = afterClear[(Size / 2) * Size + (Size / 2)];
            bool clearOk = clearCenter.R > 200 && clearCenter.G < 60 && clearCenter.B < 60;
            WasmTrace.W(
                $"[fbo-test] {ctorTag} clear center=({clearCenter.R},{clearCenter.G},{clearCenter.B},{clearCenter.A}) " +
                $"clear={(clearOk ? "PASS" : "FAIL")}");

            Color[] afterDraw = new Color[Size * Size];
            try
            {
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Red);

                Texture2D white = SolidColorTextureCache.GetTexture(Color.White);
                batcher.SetBlendState(BlendState.Opaque);
                batcher.SetSampler(SamplerState.PointClamp);
                batcher.Begin();
                batcher.Draw(
                    white,
                    new Rectangle(QuadX, QuadY, QuadW, QuadH),
                    Vector3.UnitZ,
                    0f);
                batcher.End();
                batcher.SetSampler(null);
                batcher.SetBlendState(null);

                gd.SetRenderTarget(null);
                rt.GetData<Color>(afterDraw);
            }
            catch (Exception e)
            {
                WasmTrace.W($"[fbo-test] {ctorTag} DRAW_EXCEPTION: {e.GetType().Name}: {e.Message}");
                rt.Dispose();
                return;
            }

            Color cCenter  = afterDraw[32 * Size + 32];
            Color cInside  = afterDraw[24 * Size + 24];
            Color cOutside = afterDraw[ 4 * Size +  4];
            Color cTopLeft = afterDraw[ 0 * Size +  0];
            Color cBotRgt  = afterDraw[63 * Size + 63];

            bool drawHitCenter = !(cCenter.R > 200 && cCenter.G < 60 && cCenter.B < 60);
            bool drawHitInside = !(cInside.R > 200 && cInside.G < 60 && cInside.B < 60);
            bool outsideStillRed =
                (cOutside.R > 200 && cOutside.G < 60 && cOutside.B < 60) &&
                (cTopLeft.R > 200 && cTopLeft.G < 60 && cTopLeft.B < 60) &&
                (cBotRgt.R > 200 && cBotRgt.G < 60 && cBotRgt.B < 60);

            string verdict;
            if (!clearOk)
            {
                verdict = "FAIL_CLEAR";
            }
            else if (drawHitCenter && drawHitInside && outsideStillRed)
            {
                verdict = "PASS";
            }
            else if (!drawHitCenter && !drawHitInside)
            {
                verdict = "FAIL_DRAW";
            }
            else
            {
                verdict = "PARTIAL";
            }

            WasmTrace.W(
                $"[fbo-test] {ctorTag} draw " +
                $"center=({cCenter.R},{cCenter.G},{cCenter.B},{cCenter.A}) " +
                $"inside=({cInside.R},{cInside.G},{cInside.B},{cInside.A}) " +
                $"outside=({cOutside.R},{cOutside.G},{cOutside.B},{cOutside.A}) " +
                $"tl=({cTopLeft.R},{cTopLeft.G},{cTopLeft.B},{cTopLeft.A}) " +
                $"br=({cBotRgt.R},{cBotRgt.G},{cBotRgt.B},{cBotRgt.A}) " +
                $"verdict={verdict}");

            rt.Dispose();
        }
    }
}
