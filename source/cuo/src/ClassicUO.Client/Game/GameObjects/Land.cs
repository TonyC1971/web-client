// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class Land : GameObject
    {
#if BROWSER_WASM
        // v0.5.17 R1.5: simple stack pool for Land objects. QueuedPool<T>
        // requires new() but Land has only a world-param ctor, so we use a
        // Stack<Land> directly. Cap at PREDICTABLE_TILE_COUNT (300*64=19200).
        private static readonly Stack<Land> _pool = new Stack<Land>(Constants.PREDICTABLE_TILE_COUNT);
#endif

        public ref LandTiles TileData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Client.Game.UO.FileManager.TileData.LandData[Graphic];
        }
        public sbyte AverageZ;
        public bool IsStretched;
        public sbyte MinZ;
        public Vector3 NormalTop, NormalRight, NormalLeft, NormalBottom;
        public ushort OriginalGraphic;
        public UltimaBatcher2D.YOffsets YOffsets;

        private Land(World world) : base(world) { }

        public static Land Create(World world, ushort graphic)
        {
            Land land;
#if BROWSER_WASM
            if (_pool.Count > 0)
            {
                land = _pool.Pop();
                land.IsDestroyed = false;
                land.NormalTop = land.NormalRight = land.NormalLeft = land.NormalBottom = default;
                land.YOffsets = default;
                land.MinZ = land.AverageZ = 0;
                land.InChunkMesh = false;
                land.MeshSpriteIndex = -1;
            }
            else
#endif
            {
                land = new Land(world);
            }
            land.AlphaHue = 0xFF;
            land.Graphic = graphic;
            land.OriginalGraphic = graphic;
            land.IsStretched = land.TileData.TexID == 0 && land.TileData.IsWet;
            land.AllowedToDraw = graphic > 2;
            land.UpdateGraphicBySeason();

            return land;
        }

        public override void Destroy()
        {
            if (IsDestroyed)
            {
                return;
            }

            base.Destroy();
#if BROWSER_WASM
            if (_pool.Count < Constants.PREDICTABLE_TILE_COUNT)
                _pool.Push(this);
#endif
        }

        public override void UpdateGraphicBySeason()
        {
            Graphic = SeasonManager.GetLandSeasonGraphic(World.Season, OriginalGraphic);
            AllowedToDraw = Graphic > 2;
        }

        public int CalculateCurrentAverageZ(int direction)
        {
            int result = GetDirectionZ(((byte) (direction >> 1) + 1) & 3);

            if ((direction & 1) != 0)
            {
                return result;
            }

            return (result + GetDirectionZ(direction >> 1)) >> 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetDirectionZ(int direction)
        {
            switch (direction)
            {
                case 1: return YOffsets.Right >> 2;
                case 2: return YOffsets.Bottom >> 2;
                case 3: return YOffsets.Left >> 2;
                default: return Z;
            }
        }

        public void ApplyStretch(Map.Map map, int x, int y, sbyte z)
        {
            ApplyStretchCore(map, x, y, z);
        }

#if BROWSER_WASM
        // v0.5.24 (F3-prep): fast-path ApplyStretch when the caller already
        // has the current chunk's MapBlock in scope (Chunk.LoadLandOnly).
        // For tiles at local position (lx, ly) where 1 <= lx,ly <= 6, ALL 11
        // neighbour reads (at offsets [-1..+2] from x and y) lie inside this
        // chunk's MapBlock — we can read MapCells.Cells directly instead of
        // routing through map.GetTileZ (lookup loop in 8-entry cache + branch
        // + index calc per call). Per-call savings are sub-µs but at 11
        // calls × 36 interior tiles × 49 chunks per heavy fill = ~19 400
        // GetTileZ calls collapsed to ~19 400 direct array accesses.
        //
        // Edge tiles (28 of 64 per chunk) keep the slow path because at
        // least one of their neighbours crosses into an adjacent chunk's
        // MapBlock (which lives in the cache, still fast).
        public void ApplyStretchInChunk(Map.Map map, int lx, int ly, int worldX, int worldY, sbyte z, ref ClassicUO.Assets.MapBlock currentBlock)
        {
            if (IsStretched || Client.Game.UO.FileManager.Texmaps.File.GetValidRefEntry(TileData.TexID).Length <= 0)
            {
                IsStretched = false;
                AverageZ = z;
                MinZ = z;
                return;
            }

            // Bounds check: interior tile has all 11 neighbour reads inside
            // [0..7]² of the current chunk. Reads cover (lx-1..lx+2, ly-1..ly+2),
            // so we need lx-1 >= 0 AND lx+2 <= 7 → lx in [1..5] (5 values).
            // Same for ly. = 25 interior tiles per chunk (5×5), 39 edge tiles.
            bool allInterior = (uint)(lx - 1) < 5u && (uint)(ly - 1) < 5u;
            if (!allInterior)
            {
                // Edge tile — fall back to slow path (cross-chunk reads).
                ApplyStretchCore(map, worldX, worldY, z);
                return;
            }

            // Fast-path: all reads from currentBlock.Cells. Inline the same
            // logic as ApplyStretchCore but with direct array indexing.
            sbyte zTop = z;
            sbyte zRight  = currentBlock.Cells[(ly << 3) + (lx + 1)].Z;
            sbyte zLeft   = currentBlock.Cells[((ly + 1) << 3) + lx].Z;
            sbyte zBottom = currentBlock.Cells[((ly + 1) << 3) + (lx + 1)].Z;

            YOffsets.Top = zTop * 4;
            YOffsets.Right = zRight * 4;
            YOffsets.Left = zLeft * 4;
            YOffsets.Bottom = zBottom * 4;

            if (Math.Abs(zTop - zBottom) <= Math.Abs(zLeft - zRight))
                AverageZ = (sbyte)((zTop + zBottom) >> 1);
            else
                AverageZ = (sbyte)((zLeft + zRight) >> 1);

            MinZ = Math.Min(zTop, Math.Min(zRight, Math.Min(zLeft, zBottom)));

            sbyte t10 = currentBlock.Cells[((ly - 1) << 3) + lx].Z;
            sbyte t20 = currentBlock.Cells[((ly - 1) << 3) + (lx + 1)].Z;
            sbyte t01 = currentBlock.Cells[(ly << 3) + (lx - 1)].Z;
            sbyte t21 = zRight;
            sbyte t31 = currentBlock.Cells[(ly << 3) + (lx + 2)].Z;
            sbyte t02 = currentBlock.Cells[((ly + 1) << 3) + (lx - 1)].Z;
            sbyte t12 = zLeft;
            sbyte t22 = zBottom;
            sbyte t32 = currentBlock.Cells[((ly + 1) << 3) + (lx + 2)].Z;
            sbyte t13 = currentBlock.Cells[((ly + 2) << 3) + lx].Z;
            sbyte t23 = currentBlock.Cells[((ly + 2) << 3) + (lx + 1)].Z;

            IsStretched |= CalculateNormal(z, t10, t21, t12, t01, out NormalTop);
            IsStretched |= CalculateNormal(t21, t20, t31, t22, z, out NormalRight);
            IsStretched |= CalculateNormal(t22, t21, t32, t23, t12, out NormalBottom);
            IsStretched |= CalculateNormal(t12, z, t22, t13, t02, out NormalLeft);
        }
#endif

        private void ApplyStretchCore(Map.Map map, int x, int y, sbyte z)
        {
            if (IsStretched || Client.Game.UO.FileManager.Texmaps.File.GetValidRefEntry(TileData.TexID).Length <= 0)
            {
                IsStretched = false;
                AverageZ = z;
                MinZ = z;

                return;
            }

            /*  _____ _____
             * | top | rig |
             * |_____|_____|
             * | lef | bot |
             * |_____|_____|
             */
            sbyte zTop = z;
            sbyte zRight = map.GetTileZ(x + 1, y);
            sbyte zLeft = map.GetTileZ(x, y + 1);
            sbyte zBottom = map.GetTileZ(x + 1, y + 1);

            YOffsets.Top = zTop * 4;
            YOffsets.Right = zRight * 4;
            YOffsets.Left = zLeft * 4;
            YOffsets.Bottom = zBottom * 4;

            if (Math.Abs(zTop - zBottom) <= Math.Abs(zLeft - zRight))
            {
                AverageZ = (sbyte) ((zTop + zBottom) >> 1);
            }
            else
            {
                AverageZ = (sbyte) ((zLeft + zRight) >> 1);
            }

            MinZ = Math.Min(zTop, Math.Min(zRight, Math.Min(zLeft, zBottom)));


            /*  _____ _____ _____ _____
             * |     | t10 | t20 |     |
             * |_____|_____|_____|_____|
             * | t01 |  z  | t21 | t31 |
             * |_____|_____|_____|_____|
             * | t02 | t12 | t22 | t32 |
             * |_____|_____|_____|_____|
             * |     | t13 | t23 |     |
             * |_____|_____|_____|_____|
             */
            sbyte t10 = map.GetTileZ(x, y - 1);
            sbyte t20 = map.GetTileZ(x + 1, y - 1);
            sbyte t01 = map.GetTileZ(x - 1, y);
            sbyte t21 = zRight;
            sbyte t31 = map.GetTileZ(x + 2, y);
            sbyte t02 = map.GetTileZ(x - 1, y + 1);
            sbyte t12 = zLeft;
            sbyte t22 = zBottom;
            sbyte t32 = map.GetTileZ(x + 2, y + 1);
            sbyte t13 = map.GetTileZ(x, y + 2);
            sbyte t23 = map.GetTileZ(x + 1, y + 2);


            IsStretched |= CalculateNormal(z, t10, t21, t12, t01, out NormalTop);
            IsStretched |= CalculateNormal(t21, t20, t31, t22, z, out NormalRight);
            IsStretched |= CalculateNormal(t22, t21, t32, t23, t12, out NormalBottom);
            IsStretched |= CalculateNormal(t12, z, t22, t13, t02, out NormalLeft);
        }

        private static bool CalculateNormal(sbyte tile, sbyte top, sbyte right, sbyte bottom, sbyte left, out Vector3 normal)
        {
            if (tile == top && tile == right && tile == bottom && tile == left)
            {
                normal.X = 0;
                normal.Y = 0;
                normal.Z = 1f;

                return false;
            }

            Vector3 u = new Vector3();
            Vector3 v = new Vector3();
            Vector3 ret = new Vector3();


            // ==========================
            u.X = -22;
            u.Y = -22;
            u.Z = (left - tile) * 4;

            v.X = -22;
            v.Y = 22;
            v.Z = (bottom - tile) * 4;

            Vector3.Cross(ref v, ref u, out ret);
            // ==========================


            // ==========================
            u.X = -22;
            u.Y = 22;
            u.Z = (bottom - tile) * 4;

            v.X = 22;
            v.Y = 22;
            v.Z = (right - tile) * 4;

            Vector3.Cross(ref v, ref u, out normal);
            Vector3.Add(ref ret, ref normal, out ret);
            // ==========================


            // ==========================
            u.X = 22;
            u.Y = 22;
            u.Z = (right - tile) * 4;

            v.X = 22;
            v.Y = -22;
            v.Z = (top - tile) * 4;

            Vector3.Cross(ref v, ref u, out normal);
            Vector3.Add(ref ret, ref normal, out ret);
            // ==========================


            // ==========================
            u.X = 22;
            u.Y = -22;
            u.Z = (top - tile) * 4;

            v.X = -22;
            v.Y = -22;
            v.Z = (left - tile) * 4;

            Vector3.Cross(ref v, ref u, out normal);
            Vector3.Add(ref ret, ref normal, out ret);
            // ==========================


            Vector3.Normalize(ref ret, out normal);

            return true;
        }
    }
}
