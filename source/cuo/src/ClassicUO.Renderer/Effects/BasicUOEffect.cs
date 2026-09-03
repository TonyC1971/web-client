using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Effects
{
    internal class BasicUOEffect : Effect
    {
        // On wasm the full IsometricWorld.fx -> .fxc -> MojoShader
        // glsles3 transpile produces a fragment shader whose hue
        // branch writes zero (diagnosed in webclient-wasm
        // PHASE_PLAN §P4d.5). We ship a simplified variant alongside
        // (IsometricWorld.wasm.fxc) that uses the same vertex format
        // but a pixel shader that does `tex * alpha` only — no hue,
        // no partial hue, no spectral, no lighting. Good enough to
        // light up the login screen; full effects come back when
        // we either rebuild the real .fxc against a newer MojoShader
        // or patch the transpile directly.
        public BasicUOEffect(GraphicsDevice graphicsDevice)
#if BROWSER_WASM
            : base(graphicsDevice, Resources.GetUOShaderWasm().ToArray())
#else
            : base(graphicsDevice, Resources.GetUOShader().ToArray())
#endif
        {
            MatrixTransform = Parameters["MatrixTransform"];
            WorldMatrix = Parameters["WorldMatrix"];
            Viewport = Parameters["Viewport"];
            // Brightlight / CircleOfTransparencyRadius exist only in
            // the full desktop shader. On wasm these return null;
            // callers in UltimaBatcher2D use `?.SetValue(...)` to
            // no-op on the wasm variant.
            Brighlight = Parameters["Brightlight"];
            CircleOfTransparencyRadius = Parameters["CircleOfTransparencyRadius"];
            // Bug O4 partial — day/night darkness uniform on wasm
            // shader. Null on desktop (handled via null-safe setter
            // in UltimaBatcher2D.SetGlobalLight).
            GlobalLight = Parameters["GlobalLight"];
            // Sprite smoothing (texel-AA) knobs — wasm shader only;
            // null on the desktop shader, callers null-guard like the
            // other wasm-only params above.
            SmoothWidth = Parameters["SmoothWidth"];
            SmoothMode = Parameters["SmoothMode"];
            DrawTexSize = Parameters["DrawTexSize"];

            CurrentTechnique = Techniques["HueTechnique"];
            Pass = CurrentTechnique.Passes[0];
        }

        public EffectParameter MatrixTransform { get; }
        public EffectParameter WorldMatrix { get; }
        public EffectParameter Viewport { get; }
        public EffectParameter Brighlight { get; }
        public EffectParameter CircleOfTransparencyRadius { get; }
        public EffectParameter GlobalLight { get; }
        public EffectParameter SmoothWidth { get; }
        public EffectParameter SmoothMode { get; }
        public EffectParameter DrawTexSize { get; }
        public EffectPass Pass { get; }
    }
}
