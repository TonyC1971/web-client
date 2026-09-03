// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class LightningEffect
    {
        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            ushort hue = Hue;

            if (ProfileManager.CurrentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, false, 1);
#if BROWSER_WASM
            // Bug O23: the desktop path sets hueVec.Y = SHADER_LIGHTS
            // when hue > 1 so the world shader's LIGHTS mode drives
            // the additive glow. On wasm the LIGHTS mode is disabled
            // (GameScene.PrepareLightsRendering short-circuits under
            // BROWSER_WASM), so the fallthrough `color * alpha` with
            // an unbound lights-LUT sample produced an invisible or
            // single-frame bolt. Force SHADER_NONE so the additive
            // blend at least renders the raw sprite texture — glow
            // effect lost, lightning visible. Proper fix waits for
            // the threaded-RT light pass (O4) to come back.
            hueVec.Y = ShaderHueTranslator.SHADER_NONE;
#else
            hueVec.Y = hueVec.X > 1.0f ? ShaderHueTranslator.SHADER_LIGHTS : ShaderHueTranslator.SHADER_NONE;
#endif

            ref var index = ref Client.Game.UO.FileManager.Gumps.File.GetValidRefEntry(AnimationGraphic);

            posX -= index.Width >> 1;
            posY -= index.Height;

            batcher.SetBlendState(BlendState.Additive);

            DrawGump
            (
                batcher,
                AnimationGraphic,
                posX,
                posY,
                hueVec,
                depth
            );

            batcher.SetBlendState(null);

            return true;
        }
    }
}
