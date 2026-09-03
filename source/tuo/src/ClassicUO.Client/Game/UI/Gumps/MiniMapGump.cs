// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Runtime.CompilerServices;

namespace ClassicUO.Game.UI.Gumps
{
    public class MiniMapGump : Gump
    {
        struct ColorInfo
        {
            public ushort Color;
            public sbyte Z;
            public bool IsLand;
        }

        private bool _draw;
        private int _lastMap = -1;
        private long _timeMS;
        private bool _useLargeMap;
        private ushort _x, _y;
        private static readonly uint[][] _blankGumpsPixels = new uint[4][];

        const ushort SMALL_MAP_GRAPHIC = 5010;
        const ushort BIG_MAP_GRAPHIC = 5011;

        public MiniMapGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = true;
        }

        public override GumpType GumpType => GumpType.MiniMap;

        public override void Save(XmlTextWriter writer)
        {
            base.Save(writer);
            writer.WriteAttributeString("isminimized", _useLargeMap.ToString());
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);
            _useLargeMap = bool.Parse(xml.GetAttribute("isminimized"));
            CreateMap();
        }

        private void CreateMap()
        {
            ref readonly SpriteInfo gumpInfo = ref Client.Game.UO.Gumps.GetGump(
                _useLargeMap ? BIG_MAP_GRAPHIC : SMALL_MAP_GRAPHIC
            );

            int index = _useLargeMap ? 1 : 0;

            if (_blankGumpsPixels[index] == null)
            {
                int size = gumpInfo.UV.Width * gumpInfo.UV.Height;
                _blankGumpsPixels[index] = new uint[size];
                _blankGumpsPixels[index + 2] = new uint[size];
                gumpInfo.Texture.GetData(0, gumpInfo.UV, _blankGumpsPixels[index], 0, size);

                Array.Copy(_blankGumpsPixels[index], 0, _blankGumpsPixels[index + 2], 0, size);
            }

            Width = gumpInfo.UV.Width;
            Height = gumpInfo.UV.Height;
            CreateMiniMapTexture(gumpInfo.Texture, gumpInfo.UV, true);
        }

        public override void Update()
        {
            if (!World.InGame)
            {
                return;
            }

            if (_lastMap != World.MapIndex)
            {
                CreateMap();
                _lastMap = World.MapIndex;
            }

            if (_timeMS < Time.Ticks)
            {
                _draw = !_draw;
                _timeMS = (long)Time.Ticks + 500;
            }
        }

        public bool ToggleSize(bool? large = null)
        {
            if (large.HasValue)
            {
                _useLargeMap = large.Value;
            }
            else
            {
                _useLargeMap = !_useLargeMap;
            }

            CreateMap();

            return _useLargeMap;
        }

        public override bool Draw(UltimaBatcher2D batcher, int x, int y)
        {
            if (IsDisposed)
            {
                return false;
            }

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            ref readonly SpriteInfo gumpInfo = ref Client.Game.UO.Gumps.GetGump(
                _useLargeMap ? BIG_MAP_GRAPHIC : SMALL_MAP_GRAPHIC
            );

            if (gumpInfo.Texture == null)
            {
                Dispose();

                return false;
            }

            batcher.Draw(gumpInfo.Texture, new Vector2(x, y), gumpInfo.UV, hueVector);

            CreateMiniMapTexture(gumpInfo.Texture, gumpInfo.UV);

            batcher.Draw(gumpInfo.Texture, new Vector2(x, y), gumpInfo.UV, hueVector);

            if (_draw)
            {
                int w = Width >> 1;
                int h = Height >> 1;

                Texture2D mobilesTextureDot = SolidColorTextureCache.GetTexture(Color.Red);

                foreach (Mobile mob in World.Mobiles.Values)
                {
                    if (mob == World.Player)
                    {
                        continue;
                    }

                    int xx = mob.X - World.Player.X;
                    int yy = mob.Y - World.Player.Y;

                    int gx = xx - yy;
                    int gy = xx + yy;

                    hueVector = ShaderHueTranslator.GetHueVector(
                        Notoriety.GetHue(mob.NotorietyFlag)
                    );

                    batcher.Draw(
                        mobilesTextureDot,
                        new Rectangle(x + w + gx, y + h + gy, 2, 2),
                        hueVector
                    );
                }

                //DRAW PLAYER DOT
                hueVector = ShaderHueTranslator.GetHueVector(0);

                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.White),
                    new Rectangle(x + w, y + h, 2, 2),
                    hueVector
                );
            }

            return base.Draw(batcher, x, y);
        }

        public override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                ToggleSize();

                return true;
            }

            return false;
        }

        protected override void UpdateContents() => CreateMap();

        private unsafe void CreateMiniMapTexture(
            Texture2D texture,
            Rectangle bounds,
            bool force = false
        )
        {
            ushort lastX = World.Player.X;
            ushort lastY = World.Player.Y;

            if (_x != lastX || _y != lastY)
            {
                _x = lastX;
                _y = lastY;
            }
            else if (!force)
            {
                return;
            }

            int blockOffsetX = Width >> 2;
            int blockOffsetY = Height >> 2;
            int gumpCenterX = Width >> 1;
            //int gumpCenterY = Height >> 1;

            //0xFF080808 - pixel32
            //0x8421 - pixel16
            int minBlockX = ((lastX - blockOffsetX) >> 3) - 1;
            int minBlockY = ((lastY - blockOffsetY) >> 3) - 1;
            int maxBlockX = ((lastX + blockOffsetX) >> 3) + 1;
            int maxBlockY = ((lastY + blockOffsetY) >> 3) + 1;

            if (minBlockX < 0)
            {
                minBlockX = 0;
            }

            if (minBlockY < 0)
            {
                minBlockY = 0;
            }

            int maxBlockIndex = World.Map.BlocksCount;
            int mapBlockWidth = Client.Game.UO.FileManager.Maps.MapBlocksSize[World.MapIndex, 0];
            int mapBlockHeight = Client.Game.UO.FileManager.Maps.MapBlocksSize[World.MapIndex, 1];
            int index = _useLargeMap ? 1 : 0;

            // v0.8.8 crash fix: maxBlockX/Y were only clamped at the low end
            // (>= 0); on a shard whose player spawns near a map edge (e.g. the
            // RunUO 2.x Memento facet) i/j ran past the map's block dimensions,
            // so GetIndex / ReadAt<MapBlock> read past the mmap'd .mul file and
            // the WASM runtime trapped with "memory access out of bounds" —
            // an unrecoverable trap that try/catch cannot intercept. Clamp the
            // upper bound to the map's real block extent so the read can never
            // leave the file.
            if (maxBlockX >= mapBlockWidth)
            {
                maxBlockX = mapBlockWidth - 1;
            }

            if (maxBlockY >= mapBlockHeight)
            {
                maxBlockY = mapBlockHeight - 1;
            }

            _blankGumpsPixels[index].CopyTo(_blankGumpsPixels[index + 2], 0);

            uint[] data = _blankGumpsPixels[index + 2];

            Span<Point> table = stackalloc Point[2];
            table[0].X = 0;
            table[0].Y = 0;
            table[1].X = 0;
            table[1].Y = 1;

            Span<ColorInfo> staticsZ = stackalloc ColorInfo[64];
            var d = new ColorInfo() { Z = sbyte.MinValue };

            for (int i = minBlockX; i <= maxBlockX; i++)
            {
                int blockIndexOffset = i * mapBlockHeight;

                for (int j = minBlockY; j <= maxBlockY; j++)
                {
                    int blockIndex = blockIndexOffset + j;

                    if (blockIndex >= maxBlockIndex)
                    {
                        break;
                    }

                    ref IndexMap indexMap = ref World.Map.GetIndex(i, j);

                    if (!indexMap.IsValid())
                    {
                        break;
                    }

                    // v0.8.9 crash fix (the REAL one): IndexMap.MapAddress /
                    // StaticAddress are computed in MapLoader.Load purely as
                    // blockIndex * structSize against the HARDCODED MapBlocksSize
                    // table — NEVER validated against the real file length, and
                    // IsValid() only checks MapAddress != ulong.MaxValue. On a
                    // shard whose .mul files are smaller than the standard UO
                    // dimensions (Ultima Memento / RunUO 2.x), a block that is
                    // "in range" per MapBlocksSize still points PAST the end of
                    // the mmap'd file. MMFileReader.ReadAt<T> is a raw
                    // Unsafe.ReadUnaligned with NO bounds check, so that read
                    // traps the WASM runtime with an unrecoverable "memory access
                    // out of bounds" (surfaced as a throw whose unwind itself
                    // traps under Mercury AOT -> black screen on world entry).
                    // Skip any block whose map read would leave its file. The
                    // v0.8.8 MapBlocksSize clamp above is necessary but NOT
                    // sufficient because MapBlocksSize itself overstates a
                    // smaller-than-standard file.
                    if (indexMap.MapFile == null ||
                        indexMap.MapAddress + (ulong)sizeof(MapBlock) > (ulong)indexMap.MapFile.Length)
                    {
                        break;
                    }

                    staticsZ.Fill(d);

                    // v0.8.10 ROOT FIX for the Memento minimap crash-to-black:
                    // the previous `ReadAt<MapBlock>(offset)` is a VIRTUAL
                    // generic method on the base FileReader. A generic-virtual
                    // call cannot be AOT-specialized, so under .NET 10 Mercury
                    // MT it is dispatched through gsharedvt (the crash stack
                    // literally showed `gsharedvt_in_sig … ValueTuple … __this`).
                    // The gsharedvt wrapper has to marshal MapBlock's return
                    // value — a struct whose `Cells` field is an [InlineArray(64)]
                    // of MapCells (196 bytes) — and that InlineArray marshalling
                    // traps the WASM runtime ("memory access out of bounds",
                    // surfaced as a managed throw whose unwind itself traps).
                    // CUO never hit this because it does NOT use ReadAt: it
                    // calls `Seek` + the NON-virtual `Read<MapBlock>()`, which
                    // the AOT specializes per-type and reads bytes straight into
                    // the struct's own stack storage (AOT-safe for InlineArray).
                    // Mirror CUO exactly. (The v0.8.8/v0.8.9 bounds guards above
                    // are kept as cheap defensive checks but were never the
                    // cure — this is a dispatch-mechanism bug, not an OOB.)
                    indexMap.MapFile.Seek((long)indexMap.MapAddress, System.IO.SeekOrigin.Begin);
                    MapCellsArray cells = indexMap.MapFile.Read<MapBlock>().Cells;

                    // Same out-of-file guard for the statics block read.
                    if (indexMap.StaticCount > 0 &&
                        indexMap.StaticFile != null &&
                        indexMap.StaticAddress + (ulong)indexMap.StaticCount * (ulong)sizeof(StaticsBlock) <= (ulong)indexMap.StaticFile.Length)
                    {
                        StaticsBlock[] staticsBuffer = ArrayPool<StaticsBlock>.Shared.Rent((int)indexMap.StaticCount);
                        Span<StaticsBlock> staticsSpan = staticsBuffer.AsSpan(0, (int)indexMap.StaticCount);
                        indexMap.StaticFile.ReadAt((long)indexMap.StaticAddress, MemoryMarshal.AsBytes(staticsSpan));

                        foreach (ref StaticsBlock stblock in staticsSpan)
                        {
                            // v0.8.8 crash fix: a static cell's X/Y must be
                            // within the 8x8 block (0..7). Malformed/old-format
                            // statics on some shards carry out-of-range values
                            // which would index past the 64-element stackalloc
                            // and trap the WASM runtime. Skip those defensively.
                            if (stblock.X >= 8 || stblock.Y >= 8)
                            {
                                continue;
                            }

                            if (stblock.Color > 0 && stblock.Color != 0xFFFF && GameObject.CanBeDrawn(World, stblock.Color))
                            {
                                ref ColorInfo st = ref staticsZ[stblock.Y * 8 + stblock.X];
                                if (st.Z < stblock.Z)
                                {
                                    st.Color = stblock.Hue > 0 ? (ushort)(stblock.Hue + 0x4000) : stblock.Color;
                                    st.Z = stblock.Z;
                                    st.IsLand = stblock.Hue > 0;
                                }
                            }
                        }

                        ArrayPool<StaticsBlock>.Shared.Return(staticsBuffer);
                    }

                    Chunk block = World.Map.GetChunk(blockIndex);
                    int realBlockX = i << 3;
                    int realBlockY = j << 3;

                    for (int x = 0; x < 8; x++)
                    {
                        int px = realBlockX + x - lastX + gumpCenterX;

                        for (int y = 0; y < 8; y++)
                        {
                            ref readonly MapCells cell = ref cells[(y << 3) + x];
                            int color = cell.TileID;
                            bool isLand = true;
                            int z = cell.Z;

                            ref ColorInfo stZ = ref staticsZ[y * 8 + x];
                            if (stZ.Z >= z)
                            {
                                z = stZ.Z;
                                color = stZ.Color;
                                isLand = stZ.IsLand;
                            }

                            if (block != null)
                            {
                                GameObject obj = block.Tiles[x, y];

                                while (obj?.TNext != null)
                                {
                                    obj = obj.TNext;
                                }

                                for (; obj != null; obj = obj.TPrevious)
                                {
                                    if (obj is Multi)
                                    {
                                        if (obj.Hue == 0)
                                        {
                                            color = obj.Graphic;
                                            isLand = false;
                                        }
                                        else
                                        {
                                            color = obj.Hue + 0x4000;
                                        }

                                        break;
                                    }
                                }
                            }

                            if (!isLand)
                            {
                                color += 0x4000;
                            }

                            int tableSize = 2;

                            if (isLand && color > 0x4000)
                            {
                                color = Client.Game.UO.FileManager.Hues.GetHueColorRgba5551(16, (ushort) (color - 0x4000));
                                //                                 color = Client.Game.UO.FileManager.Hues.GetColor16(
                                //     16384,
                                //     (ushort)(color - 0x4000)
                                // ); //28672 is an arbitrary position in hues.mul, is the 14 position in the range
                            }
                            else
                            {
                                color = Client.Game.UO.FileManager.Hues.GetRadarColorData(color);
                            }

                            int py = realBlockY + y - lastY;
                            int gx = px - py;
                            int gy = px + py;

                            CreatePixels(
                                data,
                                0x8000 | color,
                                gx,
                                gy,
                                Width,
                                Height,
                                table,
                                tableSize
                            );
                        }
                    }
                }
            }

            fixed (uint* ptr = data)
            {
                texture.SetDataPointerEXT(0, bounds, (IntPtr)ptr, data.Length * sizeof(uint));
            }
        }

        private unsafe void CreatePixels(
            uint[] data,
            int color,
            int x,
            int y,
            int w,
            int h,
            Span<Point> table,
            int count
        )
        {
            int px = x;
            int py = y;

            for (int i = 0; i < count; i++)
            {
                px += table[i].X;
                py += table[i].Y;

                int gx = px;

                if (gx < 0 || gx >= w)
                {
                    continue;
                }

                int gy = py;

                if (gy < 0 || gy >= h)
                {
                    break;
                }

                int block = gy * w + gx;

                if (data[block] == 0xFF080808)
                {
                    data[block] = HuesHelper.Color16To32((ushort)color) | 0xFF_00_00_00;
                }
            }
        }

        public override bool Contains(int x, int y)
        {
            x -= Offset.X;
            y -= Offset.Y;

            if (x >= 0 && y >= 0 && x < Width && y < Height)
            {
                int index = (_useLargeMap ? 1 : 0) + 2;
                int pos = (y * Width) + x;

                if (pos < _blankGumpsPixels[index].Length)
                {
                    return _blankGumpsPixels[index][pos] != 0;
                }
            }

            return false;
        }
    }
}
