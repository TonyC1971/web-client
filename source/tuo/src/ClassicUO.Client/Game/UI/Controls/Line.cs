// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.UI.Controls
{
    public class Line : Control
    {
        private readonly Texture2D _texture;
        private Vector3 _hueVector;

        public Line(int x, int y, int w, int h, uint color)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;

            _texture = SolidColorTextureCache.GetTexture(new Color { PackedValue = color });
            _hueVector = ShaderHueTranslator.GetHueVector(0, false, Alpha);
        }

        public override void AlphaChanged(float oldValue, float newValue)
        {
            base.AlphaChanged(oldValue, newValue);
            _hueVector = ShaderHueTranslator.GetHueVector(0, false, Alpha);
        }

        public override bool Draw(UltimaBatcher2D batcher, int x, int y)
        {
            batcher.Draw
            (
                _texture,
                new Rectangle
                (
                    x,
                    y,
                    Width,
                    Height
                ),
                _hueVector
            );

            return true;
        }
    }
}
