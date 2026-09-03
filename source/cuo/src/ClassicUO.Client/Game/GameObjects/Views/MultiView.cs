// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Multi
    {
        private int _canBeTransparent;
        public bool IsHousePreview;

        public override bool TransparentTest(int z)
        {
            bool r = true;

            if (Z <= z - ItemData.Height)
            {
                r = false;
            }
            else if (z < Z && (_canBeTransparent & 0xFF) == 0)
            {
                r = false;
            }

            return r;
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (!AllowedToDraw || IsDestroyed)
            {
                return false;
            }

            ushort hue = Hue;

            if (State != 0)
            {
                if ((State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER) != 0)
                {
                    return false;
                }

                if ((State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_INCORRECT_PLACE) != 0)
                {
                    hue = 0x002B;
                }

                if ((State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_TRANSPARENT) != 0)
                {
                    AlphaHue = 192;
                    depth -= 0.01f;
                }
                // 🚨 NOTHING RESTORES AlphaHue HERE ANY MORE, AND THAT IS THE FIX.
                //
                // This used to carry `else if (AlphaHue != 0xFF) AlphaHue = 0xFF;` for bug O25:
                // leaving house-design mode clears CHMOF_TRANSPARENT, and the 192 it had applied
                // was never undone, so walls and roof stayed translucent until a reload. Correct
                // intent, wrong place — it ran on EVERY draw of EVERY component of EVERY house,
                // and it could not tell "design mode just ended" from "a fade is in progress".
                //
                // The roof-hiding fade is exactly such a fade. Standing inside a player-built
                // house, ProcessAlpha dropped the roof from 255 to 230 and this line put it back
                // to 255 on the same frame, so the roof never reached 0, never got culled, and
                // never disappeared. Measured on 2026-09-03: the fade ran 8624 times per second
                // with the alpha flat at 255. Static buildings were unaffected because they are
                // Statics and never come through MultiView, and TazUO was unaffected because it
                // never had this branch — which is why the bug looked like a custom-house bug.
                //
                // O25 is now undone where the flag is cleared instead: House.ClearCustomHouseComponents.
            }

            ushort graphic = Graphic;
            bool partial = ItemData.IsPartialHue;

            Profile currentProfile = ProfileManager.CurrentProfile;

            if (currentProfile.HighlightGameObjects && SelectedObject.Object == this)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (currentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
                partial = false;
            }
            else if (World.Player.IsDead && currentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
                partial = false;
            }

            bool cot = TransparentTest(World.Player.Z + 5);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, AlphaHue / 255f, circletrans: cot);

            if (IsHousePreview)
            {
                hueVec.Z *= 0.5f;
            }

            posX += (int)Offset.X;
            posY += (int)(Offset.Y + Offset.Z);

            DrawStaticAnimated(batcher, graphic, posX, posY, hueVec, false, depth);

            if (ItemData.IsLight && !InChunkMesh)
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            if (
                !(
                    SelectedObject.Object == this
                    || IsHousePreview
                    || FoliageIndex != -1
                        && Client.Game.GetScene<GameScene>().FoliageIndex == FoliageIndex
                )
            )
            {
                if (State != 0)
                {
                    if (
                        (
                            State
                            & (
                                CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_IGNORE_IN_RENDER
                                | CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW
                            )
                        ) != 0
                    )
                    {
                        return false;
                    }
                }

                ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(Graphic + 0x4000);

                Point position = RealScreenPosition;
                position.X -= index.Width;
                position.Y -= index.Height;

                return Client.Game.UO.Arts.PixelCheck(
                    Graphic,
                    SelectedObject.TranslatedMousePositionByViewport.X - position.X,
                    SelectedObject.TranslatedMousePositionByViewport.Y - position.Y
                );
            }

            return false;
        }
    }
}
