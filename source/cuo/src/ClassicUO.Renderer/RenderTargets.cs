using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;


namespace ClassicUO.Renderer
{
    public class RenderTargets
    {
        private RenderTarget2D _uiRenderTarget;
        private RenderTarget2D _lightRenderTarget;
        private RenderTarget2D _worldRenderTarget;
        // Upscaler path (task #163): when UpscaleMode is xBR/xBRZ the world +
        // light RTs render at NATIVE world resolution (WorldScale < 1) and the
        // blit magnifies through the upscaler effect. The composed intermediate
        // holds world×lights so the effect sees the LIT, post-hue image and the
        // two RTs can never disagree in resolution. All of it is dead code
        // while UpscaleMode == 0 (the shipped texel-AA modes).
        public const int UPSCALE_NONE = 0;
        public const int UPSCALE_XBR = 3;
        public const int UPSCALE_XBRZ = 4;
        public int UpscaleMode;
        public float WorldScale = 1f;
        private RenderTarget2D _composedWorldRT;
        // Supersample intermediate for the scale≈1 regime: at default zoom the
        // output rect matches the native world size, so a direct upscaler blit
        // has no room to work. Instead the composite runs through xBR/xBRZ into
        // a 2× RT and a LINEAR downscale to the screen resolves the smoothed
        // edges (ordered-grid supersampling of the pattern-detected image).
        // Only allocated while an upscale mode is active at ~1:1 zoom.
        private RenderTarget2D _ssRT;
        // WebGL2 guarantees ≥2048 but every GPU of the last decade reports
        // ≥8192; past that we degrade to a same-size xBRZ pass (factor 1).
        private const int SS_MAX_RT_DIM = 8192;
        // Note the namespaces differ: upstream's XBREffect sits directly in
        // ClassicUO.Renderer; our XBRZEffect lives in .Effects with the rest.
        private XBREffect _xbrEffect;
        private Effects.XBRZEffect _xbrzEffect;

        private Rectangle _gameWindowOnScreen;
        private Rectangle _gameWindowAfterDPI;
        private Rectangle _gameWorldSceneOnScreen;
        private Rectangle _gameWorldSceneAfterDPI;

        private Func<Vector3> _lightsHue;
        private Func<BlendState> _lightsBlendState;

        private Texture2D _background;
        private SamplerState _defaultSamplerState;

        public RenderTarget2D UiRenderTarget { get => _uiRenderTarget; }
        public RenderTarget2D LightRenderTarget { get => _lightRenderTarget; }
        public RenderTarget2D WorldRenderTarget { get => _worldRenderTarget; }

        public void SetLightsConfiguration(Func<BlendState> lightsBlendState, Func<Vector3> lightsHue)
        {
            _lightsBlendState = lightsBlendState;
            _lightsHue = lightsHue;
        }

        public void EnsureSizes(GraphicsDevice graphicsDevice, Rectangle gameWindowOnScreen, Rectangle gameWorldSceneAfterDPI, float dpiScale)
        {
            _gameWindowOnScreen = gameWindowOnScreen;
            _gameWindowAfterDPI = ScaleRectangle(gameWindowOnScreen, dpiScale);
            _gameWorldSceneOnScreen = ScaleRectangle(gameWorldSceneAfterDPI, 1/dpiScale);
            _gameWorldSceneAfterDPI = gameWorldSceneAfterDPI;

            EnsureSize(graphicsDevice, ref _uiRenderTarget, _gameWindowAfterDPI.Width, _gameWindowAfterDPI.Height);
            // Upscaler path: world + lights shrink to native world pixels
            // (WorldScale = 1/(zoom*dpr), set by GameScene alongside the
            // matrix strip); UI stays full-res. WorldScale is 1 for the
            // texel-AA/off modes — identical sizing to the shipped pipeline.
            int worldW = System.Math.Max(1, (int)(_gameWorldSceneAfterDPI.Width * WorldScale));
            int worldH = System.Math.Max(1, (int)(_gameWorldSceneAfterDPI.Height * WorldScale));
            EnsureSize(graphicsDevice, ref _lightRenderTarget, worldW, worldH);
            EnsureSize(graphicsDevice, ref _worldRenderTarget, worldW, worldH);
            if (UpscaleMode != UPSCALE_NONE)
            {
                EnsureSize(graphicsDevice, ref _composedWorldRT, worldW, worldH);
            }
            else
            {
                // Leaving the upscaler modes: free the intermediates (a 2× SS
                // RT at 1440p is ~56 MB of VRAM — don't keep it warm for nothing).
                if (_composedWorldRT != null) { _composedWorldRT.Dispose(); _composedWorldRT = null; }
                if (_ssRT != null) { _ssRT.Dispose(); _ssRT = null; }
            }

            if (dpiScale == Math.Floor(dpiScale))
            {
                // Use PointClamp for integer DPI scaling to avoid blurriness
                _defaultSamplerState = SamplerState.PointClamp;
            }
            else
            {
                // Use LinearClamp for non-integer DPI scaling for smoother results
                _defaultSamplerState = SamplerState.LinearClamp;
            }
        }

        private static Rectangle ScaleRectangle(Rectangle gameWindowOnScreen, float dpiScale) => new(
                (int)(gameWindowOnScreen.X / dpiScale),
                (int)(gameWindowOnScreen.Y / dpiScale),
                (int)(gameWindowOnScreen.Width / dpiScale),
                (int)(gameWindowOnScreen.Height / dpiScale)
            );

        private static void EnsureSize(GraphicsDevice graphicsDevice, ref RenderTarget2D renderTarget, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            if (renderTarget == null || renderTarget.IsDisposed || renderTarget.Width != width || renderTarget.Height != height)
            {
                renderTarget?.Dispose();

                PresentationParameters pp = graphicsDevice.PresentationParameters;

                renderTarget = new RenderTarget2D(
                    graphicsDevice,
                    width,
                    height,
                    false,
                    pp.BackBufferFormat,
                    pp.DepthStencilFormat,
                    pp.MultiSampleCount,
                    pp.RenderTargetUsage
                    );
            }
        }

        public void InitializeBackground(Texture2D background)
        {
            _background = background ?? throw new ArgumentNullException(nameof(background));
        }

        public void Draw(UltimaBatcher2D batcher)
        {
            // 2026-04-26: wasm runs the full composite the same as
            // desktop now. Earlier short-circuit was needed because
            // glDrawElements to a custom FBO no-op'd silently — the
            // root cause was the missing -DMOJOSHADER_FLIP_RENDERTARGET
            // in the wasm FNA3D / MojoShader compile flags, fixed in
            // wasm-fna-native-mercury.targets._FnaCompileFlags.

            // draw world
            Vector3 fullAlphaNoColor = Vector3.UnitZ;

            batcher.Begin();
            batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            var rect = new Rectangle(
                0,
                0,
                _gameWindowOnScreen.Width,
                _gameWindowOnScreen.Height
            );
            batcher.DrawTiled(
                _background,
                rect,
                _background.Bounds,
                new Vector3(0, 0, 0.1f),
                0f
            );

            if (UpscaleMode != UPSCALE_NONE && _composedWorldRT != null)
            {
                // Upscaler path (task #163): compose world×lights 1:1 at native
                // resolution, then magnify the LIT composite to the screen rect
                // through the xBR/xBRZ effect. The UI pass below is untouched.
                batcher.End();
                batcher.GraphicsDevice.SetRenderTarget(_composedWorldRT);
                batcher.Begin();
                batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);
                var nativeRect = new Rectangle(0, 0, _composedWorldRT.Width, _composedWorldRT.Height);
                batcher.Draw(WorldRenderTarget, nativeRect, fullAlphaNoColor, 0f);
                batcher.SetBlendState(_lightsBlendState?.Invoke());
                batcher.Draw(LightRenderTarget, nativeRect, _lightsHue?.Invoke() ?? Vector3.Up, 0f);
                batcher.SetBlendState(null);
                batcher.End();
                batcher.GraphicsDevice.SetRenderTarget(null);

                Effect upscale = ResolveUpscaleEffect(batcher.GraphicsDevice);

                // Two regimes:
                //  - magnified (zoom in): the screen rect is meaningfully larger
                //    than the native composite → one direct upscaler blit.
                //  - scale≈1 (default view, the common case): no magnification
                //    to exploit → supersample instead: xBR/xBRZ into a 2× RT,
                //    then a linear downscale resolves the smoothed edges.
                bool magnified = _gameWorldSceneOnScreen.Width > (int)(_composedWorldRT.Width * 1.05f);

                if (magnified)
                {
                    var vp = batcher.GraphicsDevice.Viewport;
                    Matrix ortho = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, -1);
                    upscale.Parameters["MatrixTransform"]?.SetValue(ortho);
                    upscale.Parameters["textureSize"]?.SetValue(new Vector2(_composedWorldRT.Width, _composedWorldRT.Height));
                    upscale.Parameters["outputSize"]?.SetValue(new Vector2(_gameWorldSceneOnScreen.Width, _gameWorldSceneOnScreen.Height));

                    batcher.Begin(upscale);
                    batcher.SetSampler(SamplerState.PointClamp); // upscalers analyse the raw texel grid
                    batcher.Draw(_composedWorldRT, _gameWorldSceneOnScreen, fullAlphaNoColor, 0f);
                    batcher.SetSampler(null);
                    batcher.End();
                }
                else
                {
                    int ssFactor = (_composedWorldRT.Width * 2 <= SS_MAX_RT_DIM && _composedWorldRT.Height * 2 <= SS_MAX_RT_DIM) ? 2 : 1;
                    EnsureSize(batcher.GraphicsDevice, ref _ssRT, _composedWorldRT.Width * ssFactor, _composedWorldRT.Height * ssFactor);

                    batcher.GraphicsDevice.SetRenderTarget(_ssRT);
                    var ssRect = new Rectangle(0, 0, _ssRT.Width, _ssRT.Height);
                    Matrix ssOrtho = Matrix.CreateOrthographicOffCenter(0, _ssRT.Width, _ssRT.Height, 0, 0, -1);
                    upscale.Parameters["MatrixTransform"]?.SetValue(ssOrtho);
                    upscale.Parameters["textureSize"]?.SetValue(new Vector2(_composedWorldRT.Width, _composedWorldRT.Height));
                    upscale.Parameters["outputSize"]?.SetValue(new Vector2(_ssRT.Width, _ssRT.Height));

                    batcher.Begin(upscale);
                    batcher.SetSampler(SamplerState.PointClamp);
                    batcher.Draw(_composedWorldRT, ssRect, fullAlphaNoColor, 0f);
                    batcher.SetSampler(null);
                    batcher.End();
                    batcher.GraphicsDevice.SetRenderTarget(null);

                    batcher.Begin();
                    batcher.SetSampler(SamplerState.LinearClamp); // downscale resolve
                    batcher.Draw(_ssRT, _gameWorldSceneOnScreen, fullAlphaNoColor, 0f);
                    batcher.SetSampler(null);
                    batcher.End();
                }

                batcher.Begin(); // resume the default pass for the UI blit below
            }
            else
            {
                batcher.SetSampler(_defaultSamplerState);

                batcher.Draw(
                    WorldRenderTarget,
                    _gameWorldSceneOnScreen,
                    fullAlphaNoColor,
                    0f
                );

                // draw lights
                batcher.SetBlendState(_lightsBlendState?.Invoke());

                batcher.Draw(
                    LightRenderTarget,
                    _gameWorldSceneOnScreen,
                    _lightsHue?.Invoke() ?? Vector3.Up,
                    0f
                );

                batcher.SetBlendState(null);
            }

            // Draw UI at original window size (render target is DPI-scaled but destination is not)
            batcher.Draw(
                UiRenderTarget,
                _gameWindowOnScreen,
                fullAlphaNoColor,
                0f
            );

            // Reset sampler to default
            batcher.SetSampler(null);
            batcher.End();
        }

        // Lazy effect creation — the GraphicsDevice only exists at draw time,
        // and the effects stay unloaded for everyone not using the upscalers.
        private Effect ResolveUpscaleEffect(GraphicsDevice device)
        {
            if (UpscaleMode == UPSCALE_XBRZ)
            {
                return _xbrzEffect ??= new Effects.XBRZEffect(device);
            }

            return _xbrEffect ??= new XBREffect(device);
        }
    }
}
