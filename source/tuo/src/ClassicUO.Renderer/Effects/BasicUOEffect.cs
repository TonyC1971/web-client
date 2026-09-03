using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Effects
{
    internal class BasicUOEffect : Effect
    {
        // v0.7.9 WASM port: on wasm the full IsometricWorld.fx -> .fxc ->
        // MojoShader glsles3 transpile produces a fragment shader whose
        // hue branch writes zero (diagnosed in CUO PHASE_PLAN §P4d.5).
        // We ship a simplified variant alongside (IsometricWorld.wasm.fxc)
        // that uses the same vertex format but a pixel shader that does
        // `tex * alpha` only — no hue, no partial hue, no spectral, no
        // lighting. Good enough to light up the login screen; full
        // effects come back when MojoShader is rebuilt or the transpile
        // is patched directly. Mirrors source/cuo/.../BasicUOEffect.cs.
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
            Brighlight = Parameters["Brightlight"];
            // TexelSize: only declared in the desktop shader. On wasm
            // this returns null; UltimaBatcher2D's callers must use
            // null-safe `?.SetValue(...)` on TexelSize / Brighlight /
            // any other desktop-only uniform.
            TexelSize = Parameters["TexelSize"];

            // WASM black-world-text fix: the shared IsometricWorld.wasm shader
            // multiplies every NON-gump sprite by max(GlobalLight, 0.08) (the
            // day/night hook at the bottom of PixelShader_Hue). CUO binds this
            // parameter so FNA pushes the shader's 1.0 default; TUO never bound
            // it, so under the MojoShader glsles3 transpile the uniform sat at
            // 0 → every world sprite (overhead "say" text, mobiles, land,
            // statics) rendered at 8% brightness. White overhead text collapsed
            // to ~black while the LoginGump (a GUMP — isGump=true skips the
            // multiply) rendered correctly. Bind it AND pin it to 1.0; the real
            // day/night darkness comes from the LightRT multiplicative composite,
            // exactly like CUO (whose GameScene notes "GlobalLight stays at 1.0").
            GlobalLight = Parameters["GlobalLight"];
            GlobalLight?.SetValue(1.0f);
            // Sprite smoothing (texel-AA) knobs — wasm shader only; null-safe.
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
        public EffectParameter TexelSize { get; }
        public EffectParameter GlobalLight { get; }
        public EffectParameter SmoothWidth { get; }
        public EffectParameter SmoothMode { get; }
        public EffectParameter DrawTexSize { get; }
        public EffectPass Pass { get; }
    }
}
