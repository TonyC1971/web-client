using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StbRectPackSharp;
using System;
using System.Collections.Generic;

namespace ClassicUO.Renderer
{
    public class TextureAtlas : IDisposable
    {
        private readonly int _width,
            _height;
        private readonly SurfaceFormat _format;
        private readonly GraphicsDevice _device;
        private readonly List<Texture2D> _textureList;
        private Packer _packer;

        public TextureAtlas(GraphicsDevice device, int width, int height, SurfaceFormat format)
        {
            _device = device;
            _width = width;
            _height = height;
            _format = format;

            _textureList = new List<Texture2D>();
        }

        public int TexturesCount => _textureList.Count;

        public unsafe Texture2D AddSprite(
            ReadOnlySpan<uint> pixels,
            int width,
            int height,
            out Rectangle pr
        )
        {
            var index = _textureList.Count - 1;

            if (index < 0)
            {
                index = 0;
                CreateNewTexture2D();
            }

            // Sprite-smoothing support (2026-07-10): pack with a 1px border on
            // every side and upload the pixels into the inner rect. The border
            // texels stay at the texture's initial contents (zeroed = fully
            // transparent), so the shader's texel-AA neighbour taps at sprite
            // edges read clean alpha=0 instead of an adjacent sprite packed
            // flush against this one (StbRectPack packs edge-to-edge). Costs
            // ~9% atlas area on 44px art; `pr` returned to callers is the
            // INNER rect, so every existing UV consumer is unchanged.
            while (!_packer.PackRect(width + 2, height + 2, out pr))
            {
                CreateNewTexture2D();
                index = _textureList.Count - 1;
            }

            pr = new Rectangle(pr.X + 1, pr.Y + 1, width, height);

            Texture2D texture = _textureList[index];

            fixed (uint* src = pixels)
            {
                texture.SetDataPointerEXT(0, pr, (IntPtr)src, sizeof(uint) * width * height);
            }

            return texture;
        }

        private void CreateNewTexture2D()
        {
            Utility.Logging.Log.Trace($"creating texture: {_width}x{_height} {_format}");
#if BROWSER_WASM
            int slot = Utility.ZoneLoadDiag.NextTextureSlot();
            if (slot <= 20)
            {
                WasmTrace.W($"[zonediag] t={Utility.ZoneLoadDiag.Ms}ms TextureAtlas#{slot} alloc {_width}x{_height} {_format}");
            }
#endif
            Texture2D texture = new Texture2D(_device, _width, _height, false, _format);
            _textureList.Add(texture);

            _packer?.Dispose();
            _packer = new Packer(_width, _height);
        }

        public void SaveImages(string name)
        {
            for (int i = 0, count = TexturesCount; i < count; ++i)
            {
                var texture = _textureList[i];

                using (var stream = System.IO.File.Create($"atlas/{name}_atlas_{i}.png"))
                {
                    texture.SaveAsPng(stream, texture.Width, texture.Height);
                }
            }
        }

        public void Dispose()
        {
            foreach (Texture2D texture in _textureList)
            {
                if (!texture.IsDisposed)
                {
                    texture.Dispose();
                }
            }

            _packer.Dispose();
            _textureList.Clear();
        }
    }
}
