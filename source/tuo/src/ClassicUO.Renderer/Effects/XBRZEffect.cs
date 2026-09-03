// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    // task #163: xBRZ freescale post-processing effect. Same effect frame as
    // XBREffect (T0 technique, decal sampler, batcher vertex layout); adds
    // outputSize because xbrz-freescale interpolates by the destination
    // footprint instead of assuming an integer scale factor.
    public class XBRZEffect : Effect
    {
        public XBRZEffect(GraphicsDevice graphicsDevice) : base(graphicsDevice, Resources.GetXBRZShader().ToArray())
        {
            MatrixTransform = Parameters["MatrixTransform"];
            TextureSize = Parameters["textureSize"];
            OutputSize = Parameters["outputSize"];
        }

        public EffectParameter MatrixTransform { get; }
        public EffectParameter TextureSize { get; }
        public EffectParameter OutputSize { get; }
    }
}
