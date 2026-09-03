using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Effects
{
    // xBRZ freescale upscaler (task #163): fullscreen blit effect for the
    // native-resolution WorldRenderTarget path. Mirrors XBREffect's shape;
    // `textureSize` = the RT's native world-pixel size, `outputSize` = the
    // on-screen destination rect size — both set per frame by the blit.
    public class XBRZEffect : Effect
    {
        public XBRZEffect(GraphicsDevice graphicsDevice)
            : base(graphicsDevice, Resources.GetXBRZShader().ToArray())
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
