// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Utility;
using System.Runtime.CompilerServices;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class Static : GameObject
    {
#if BROWSER_WASM
        // v0.5.17 R1.5: simple stack pool for Static objects (same rationale
        // as Land pool — no new() ctor). Cap at PREDICTABLE_STATICS (38400).
        private static readonly Stack<Static> _pool = new Stack<Static>(Constants.PREDICTABLE_STATICS);
#endif

        public Static(World world) : base(world) { }

        public string Name => ItemData.Name;

        public ushort OriginalGraphic { get; private set; }

        public ref StaticTiles ItemData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];
        }

        public bool IsVegetation;
        public int Index;


        public static Static Create(World world, ushort graphic, ushort hue, int index)
        {
            Static s;
#if BROWSER_WASM
            if (_pool.Count > 0)
            {
                s = _pool.Pop();
                s.IsDestroyed = false;
                s.AlphaHue = 0;
                s.FoliageIndex = -1;
                s.InChunkMesh = false;
                s.MeshSpriteIndex = -1;
            }
            else
#endif
            {
                s = new Static(world);
            }
            s.Graphic = s.OriginalGraphic = graphic;
            s.Hue = hue;
            s.UpdateGraphicBySeason();
            s.Index = index;

            if (s.ItemData.Height > 5 || s.ItemData.Height == 0)
            {
                s._canBeTransparent = 1;
            }
            else if (s.ItemData.IsRoof || s.ItemData.IsSurface && s.ItemData.IsBackground || s.ItemData.IsWall)
            {
                s._canBeTransparent = 1;
            }
            else if (s.ItemData.Height == 5 && s.ItemData.IsSurface && !s.ItemData.IsBackground)
            {
                s._canBeTransparent = 1;
            }
            else
            {
                s._canBeTransparent = 0;
            }

            return s;
        }

        public void SetGraphic(ushort g)
        {
            Graphic = g;
        }

        public void RestoreOriginalGraphic()
        {
            Graphic = OriginalGraphic;
        }

        public override void UpdateGraphicBySeason()
        {
            SetGraphic(SeasonManager.GetSeasonGraphic(World.Season, OriginalGraphic));
            AllowedToDraw = CanBeDrawn(World, Graphic);
            IsVegetation = StaticFilters.IsVegetation(Graphic);
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Destroy();
#if BROWSER_WASM
            if (_pool.Count < Constants.PREDICTABLE_STATICS)
                _pool.Push(this);
#endif
        }
    }
}