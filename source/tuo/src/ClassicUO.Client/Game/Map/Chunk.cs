// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClassicUO.Game.Map
{
    public sealed class Chunk
    {
        //private static readonly QueuedPool<Chunk> _pool = new QueuedPool<Chunk>
        //(
        //    Constants.PREDICTABLE_CHUNKS,
        //    c =>
        //    {
        //        c.LastAccessTime = Time.Ticks + Constants.CLEAR_TEXTURES_DELAY;
        //        c.IsDestroyed = false;
        //    }
        //);

        private readonly World _world;

        public Chunk(World world)
        {
            _world = world;
        }

        public GameObject[,] Tiles { get; } = new GameObject[8, 8];
        public bool IsDestroyed;
        public volatile bool IsLoading;
        public long LastAccessTime;
        public LinkedListNode<int> Node;


        public int X;
        public int Y;

#if BROWSER_WASM
        // ── Chunk-mesh renderer (ported from CUO v0.5.x perf track) ──────────
        // Batched per-texture sprite mesh for this chunk's land + statics, built
        // lazily and re-drawn cheaply each frame instead of re-pushing every
        // tile to the immediate-mode queue. This is the core TUO-on-Memento lag
        // fix: CUO has it (60fps on Memento), TUO drew per-tile (tirones).
        public readonly ChunkMesh Mesh = new ChunkMesh();
        public bool StaticsLoaded;
        public sbyte MaxContentZ = sbyte.MinValue;

        // Per-chunk replay cache: on a STABLE+CLEAN chunk (in viewport last fill,
        // mesh not dirty, no alpha tick) the render loop can SKIP the 8x8 x
        // linked-list walk and replay these cached contributions.
        public bool CachedContribsValid;
        public readonly List<GameObject> CachedMeshContribs = new List<GameObject>();
        public readonly List<GameObject> CachedSlowPathContribs = new List<GameObject>();
        public readonly List<bool> CachedSlowPathTransparent = new List<bool>();
        public readonly List<GameObject> CachedLightObjects = new List<GameObject>();
#endif


        public static Chunk Create(World world, int x, int y, bool isAsync = false)
        {
            var c = new Chunk(world); // _pool.GetOne();
            c.LastAccessTime = Time.Ticks + Constants.CLEAR_TEXTURES_DELAY;
            c.X = x;
            c.Y = y;
            c.IsLoading = isAsync;

            return c;
        }


        public unsafe void Load(int index, bool updateWorldMap = false)
        {
            IsLoading = true;
            IsDestroyed = false;

#if BROWSER_WASM
            // [load] instrumentation (2026-06-01): time every chunk load and
            // emit ONE line per slow load so the Memento "tirones" can be
            // attributed to specific chunk fills while roaming with ?dev=2.
            // The verbose per-step [chunk-debug] traces below are bring-up
            // diagnostics for the (now-closed) InlineArray AOT trap; they cost
            // ~16 string-interp + Console.WriteLine (C#→JS) crossings PER chunk
            // load, always-on, which is itself a movement-lag suspect. Gate
            // them behind CUOEnviroment.Debug (off in normal play, on via the
            // in-game debug toggle / -debug arg) so the interpolation never
            // runs unless explicitly enabled.
            bool _dbg = CUOEnviroment.Debug;
            long _loadStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_dbg)
                ClassicUO.Utility.Logging.Log.Trace($"[chunk-debug] CL0: Load index={index} XY=({X},{Y})");
#endif
            try
            {
                Map map = _world.Map;

                // v0.8.2: bounds-check X,Y against the current map's block dims
                // BEFORE indexing. A stale async load (queued for the map at
                // enqueue time) can run after a teleport/facet-swap to a smaller
                // map; Maps.GetIndex then does x*height+y into BlockData[map] and
                // overruns → ArgumentOutOfRangeException on the Mercury-MT
                // thread-pool worker, which surfaces as an [unobserved-task] error
                // and (if caught on the worker) an [object WebAssembly.Exception]
                // trap. Prevent the throw: drop the stale load (it's for a
                // different facet; the main thread re-requests for the current map
                // on the next GetChunk). CUO never hits this — synchronous chunk load.
                var _maps = Client.Game.UO.FileManager.Maps;
                int _sm = index;
                _maps.SanitizeMapIndex(ref _sm);
                if (X < 0 || Y < 0 || X >= _maps.MapBlocksSize[_sm, 0] || Y >= _maps.MapBlocksSize[_sm, 1])
                {
                    IsLoading = false;
                    return;
                }

                ref IndexMap im = ref GetIndex(index);

                if (!im.IsValid())
                {
#if BROWSER_WASM
                    if (_dbg)
                        ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL1: IndexMap invalid → return");
#endif
                    return;
                }

#if BROWSER_WASM
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace($"[chunk-debug] CL2: pre-MapFile.ReadAt MapAddress={im.MapAddress}");
#endif
#if BROWSER_WASM
                // v0.7.9 iter 57 ROOT-FIX: read the MapBlock as raw bytes
                // into a fixed-size buffer + MemoryMarshal.Read, instead
                // of FileReader.Read<MapBlock>() which does
                // `new Span<byte>(&v, sizeof(T))` over a stack local.
                // For MapBlock (a 196-byte struct whose `Cells` is an
                // `[InlineArray(64)]`), the &v+sizeof path traps with
                // "memory access out of bounds" between CL2 and CL3 under
                // .NET 10 Mercury MT AOT — the AOT mis-sizes the stack
                // slot for the InlineArray local, so writing sizeof(T)
                // bytes through &v overruns the stack. Reading into an
                // explicit byte buffer (hardcoded 196 = 4 Header + 64×3
                // Cells, bypassing any sizeof miscompile) and then
                // MemoryMarshal.Read is the AOT-safe path.
                const int MAPBLOCK_BYTES = 196;
                Span<byte> _mbBuf = stackalloc byte[MAPBLOCK_BYTES];
                im.MapFile.ReadAt((long)im.MapAddress, _mbBuf);
                MapBlock block = MemoryMarshal.Read<MapBlock>(_mbBuf);
#else
                MapBlock block = im.MapFile.ReadAt<MapBlock>((long)im.MapAddress);
#endif
#if BROWSER_WASM
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace($"[chunk-debug] CL3: post-MapFile.ReadAt (sizeof check {Unsafe.SizeOf<MapBlock>()})");
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL3a: pre-block.Cells");
#endif
                MapCellsArray cells = block.Cells;
#if BROWSER_WASM
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL3b: post-block.Cells");
#endif
                int bx = X << 3;
                int by = Y << 3;
#if BROWSER_WASM
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL3c: bx/by computed");
#endif

                // v0.7.9 iter 50: move the worldMap-only allocations + Hues
                // getter into the `if (updateWorldMap)` block. The first
                // Chunk.Load call on browser-wasm was allocating two
                // 64-element arrays and dereferencing the Hues lazy
                // getter even when updateWorldMap=false, which is the
                // standard case at game entry. The allocations
                // triggered a Mercury MT GC safepoint that raced with
                // the BG ReceiveLoopAsync worker — "WAITING for 1
                // threads, got 0 suspended" → worker error → trap.
                // CUO's LoadLandOnly never allocates these; we mirror
                // that pattern surgically.
                uint[] bufferBlock = null;
                sbyte[] bufferBlockZ = null;
                HuesLoader huesLoader = null;
                if (updateWorldMap)
                {
                    bufferBlock = new uint[64];
                    bufferBlockZ = new sbyte[64];
                    huesLoader = Client.Game.UO.FileManager.Hues;
                }
#if BROWSER_WASM
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace($"[chunk-debug] CL3d: post-conditional-alloc updateWorldMap={updateWorldMap}");
                if (_dbg)
                    ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL4: entering tile loop");
#endif
                for (int y = 0; y < 8; ++y)
                {
                    int pos = y << 3;
                    ushort tileY = (ushort)(by + y);

                    for (int x = 0; x < 8; ++x, ++pos)
                    {
                        ushort tileID = (ushort)(cells[pos].TileID & 0x3FFF);

                        sbyte z = cells[pos].Z;

                        var land = Land.Create(_world, tileID);

                        ushort tileX = (ushort)(bx + x);

#if BROWSER_WASM
                        if (_dbg && x == 0 && y == 0)
                        {
                            ClassicUO.Utility.Logging.Log.Trace($"[chunk-debug] CL5: first iter (x,y)=(0,0) tile=0x{tileID:X4} xyz=({tileX},{tileY},{z})");
                            ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL5a: pre-ApplyStretch");
                        }
#endif
                        land.ApplyStretch(map, tileX, tileY, z);
#if BROWSER_WASM
                        if (_dbg && x == 0 && y == 0)
                            ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL5b: post-ApplyStretch pre-Land XYZ assigns");
#endif
                        land.X = tileX;
                        land.Y = tileY;
                        land.Z = z;
                        land.UpdateScreenPosition();
#if BROWSER_WASM
                        if (_dbg && x == 0 && y == 0)
                            ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL5c: post-Land.UpdateScreenPosition pre-TileMarker");
#endif

                        if (TileMarkerManager.Instance.IsTileMarked(land.X, land.Y, map.Index, out ushort hue))
                            land.Hue = hue;
#if BROWSER_WASM
                        if (_dbg && x == 0 && y == 0)
                            ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL5d: post-TileMarker pre-AddGameObject");
#endif

                        AddGameObject(land, x, y);
#if BROWSER_WASM
                        if (_dbg && x == 0 && y == 0)
                            ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL5e: post-AddGameObject (first tile done)");
                        if (_dbg && x == 7 && y == 7)
                            ClassicUO.Utility.Logging.Log.Trace("[chunk-debug] CL5z: last tile (7,7) done");
#endif

                        if (updateWorldMap)
                        {
                            ushort color = (ushort)(0x8000 | huesLoader.GetRadarColorData(tileID & 0x3FFF));

                            int blockIndex = y * 8 + x;
                            bufferBlock[blockIndex] = HuesHelper.Color16To32(color) | 0xFF_00_00_00;
                            bufferBlockZ[blockIndex] = z;
                        }
                    }
                }

                //If Ultima Live is on, the statics of the first map block explored could be saved to StaticAdress 0, because the static file could be empty, so we can't check StaticAdress != 0
                if (im.StaticAddress >= 0 && im.StaticCount > 0)
                {
                    StaticsBlock[] staticsBlockBuffer = ArrayPool<StaticsBlock>.Shared.Rent((int)im.StaticCount);
                    Span<StaticsBlock> staticsSpan = staticsBlockBuffer.AsSpan(0, (int)im.StaticCount);
                    im.StaticFile.ReadAt((long)im.StaticAddress, MemoryMarshal.AsBytes(staticsSpan));

                    foreach (ref StaticsBlock sb in staticsSpan)
                    {
                        if (sb.Color != 0 && sb.Color != 0xFFFF)
                        {
                            int pos = (sb.Y << 3) + sb.X;

                            if (pos >= 64)
                            {
                                continue;
                            }

                            var staticObject = Static.Create(_world, sb.Color, sb.Hue, pos);
                            staticObject.X = (ushort)(bx + sb.X);
                            staticObject.Y = (ushort)(by + sb.Y);
                            staticObject.Z = sb.Z;
                            staticObject.UpdateScreenPosition();

                            if (TileMarkerManager.Instance.IsTileMarked(staticObject.X, staticObject.Y, map.Index, out ushort hue))
                                staticObject.Hue = hue;

                            AddGameObject(staticObject, sb.X, sb.Y);

                            if (updateWorldMap)
                            {
                                int blockIndex = (sb.Y << 3) + sb.X;
                                if (GameObject.CanBeDrawn(_world, sb.Color) && sb.Z >= bufferBlockZ[blockIndex])
                                {
                                    ushort color = (ushort)(0x8000 | (sb.Hue != 0 ? huesLoader.GetColor16(16384, sb.Hue) : huesLoader.GetRadarColorData(sb.Color + 0x4000)));

                                    bufferBlock[blockIndex] = HuesHelper.Color16To32(color) | 0xFF_00_00_00;
                                    bufferBlockZ[blockIndex] = sb.Z;
                                }
                            }
                        }
                    }

                    ArrayPool<StaticsBlock>.Shared.Return(staticsBlockBuffer);
                }

                if (updateWorldMap)
                {
                    const float MAG_0 = 80f / 100f;
                    const float MAG_1 = 100f / 80f;

                    for (int y = 0; y < 8; ++y)
                    {
                        for (int x = 0; x < 8; ++x)
                        {
                            int blockCurrent = y * 8 + x;
                            int blockNext = (y + 1) * 8 + x;

                            //Reached last line, nothing to compare with
                            if (y == 7)
                            {
                                break;
                            }

                            sbyte z0 = bufferBlockZ[++blockCurrent];
                            sbyte z1 = bufferBlockZ[blockNext];

                            if (z0 == z1)
                            {
                                continue;
                            }

                            ref uint cc = ref bufferBlock[blockCurrent];

                            if (cc == 0)
                            {
                                continue;
                            }

                            byte r = (byte)(cc & 0xFF);
                            byte g = (byte)((cc >> 8) & 0xFF);
                            byte b = (byte)((cc >> 16) & 0xFF);
                            byte a = (byte)((cc >> 24) & 0xFF);

                            if (r != 0 || g != 0 || b != 0)
                            {
                                if (z0 < z1)
                                {
                                    r = (byte)Math.Min(0xFF, r * MAG_0);
                                    g = (byte)Math.Min(0xFF, g * MAG_0);
                                    b = (byte)Math.Min(0xFF, b * MAG_0);
                                }
                                else
                                {
                                    r = (byte)Math.Min(0xFF, r * MAG_1);
                                    g = (byte)Math.Min(0xFF, g * MAG_1);
                                    b = (byte)Math.Min(0xFF, b * MAG_1);
                                }

                                cc = (uint)(r | (g << 8) | (b << 16) | (a << 24));
                            }
                        }
                    }

                    UIManager.GetGump<WorldMapGump>()?.UpdateWorldMapChunk(X, Y, bufferBlock);
                }
            }
            finally
            {
                IsLoading = false;
#if BROWSER_WASM
                // [load] timing — always measured (Stopwatch is cheap), but only
                // EMITTED when the chunk load actually spiked (≥1.5ms). This is the
                // signal the operator's ?dev=2 Memento roam captures: which chunk
                // loads are the tirones. Threshold keeps the common fast loads silent.
                long _loadUs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - _loadStart) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
                if (_loadUs >= 1500)
                    Console.WriteLine($"[load] chunk {X},{Y} idx={index} dt={_loadUs / 1000.0:F2}ms");
#endif
            }
        }


        private ref IndexMap GetIndex(int map)
        {
            Client.Game.UO.FileManager.Maps.SanitizeMapIndex(ref map);

            return ref Client.Game.UO.FileManager.Maps.GetIndex(map, X, Y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject GetHeadObject(int x, int y)
        {
            GameObject obj = Tiles[x, y];

            while (obj?.TPrevious != null)
            {
                obj = obj.TPrevious;
            }

            return obj;
        }

        public void AddGameObject(GameObject obj, int x, int y)
        {
#if BROWSER_WASM
            // Dirty the chunk-mesh when a Land/Static/Multi is added (so it
            // rebuilds next fill — e.g. statics loaded after the initial land
            // build, or a dropped/placed item). No-op for Mobiles/dynamic items.
            Mesh.MarkDirtyIfNeeded(obj);
#endif
            obj.RemoveFromTile();

            short priorityZ = obj.Z;
            sbyte state = -1;

            ushort graphic = obj.Graphic;

            switch (obj)
            {
                case Land tile:

                    if (tile.IsStretched)
                    {
                        priorityZ = (short) (tile.AverageZ - 1);
                    }
                    else
                    {
                        priorityZ--;
                    }

                    priorityZ -= 1;

                    state = 0;

                    break;

                case Mobile _:
                    priorityZ++;

                    break;

                case Item item:

                    if (item.IsCorpse)
                    {
                        priorityZ++;

                        break;
                    }
                    else if (item.IsMulti)
                    {
                        graphic = item.MultiGraphic;
                    }

                    goto default;

                case GameEffect _:
                    priorityZ += 2;

                    break;

                case Multi m:

                    state = 1;

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0)
                    {
                        priorityZ--;

                        break;
                    }

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) != 0)
                    {
                        state = 2;
                        priorityZ++;
                    }
                    //else if ((m.ItemData.Flags & TileFlag.StairRight) != 0)
                    //{
                    //    priorityZ++;
                    //}

                    //if (m.IsMovable)
                    //{
                    //    priorityZ += 1;
                    //}

                    goto default;

                default:
                    ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];

                    if (data.IsBackground)
                    {
                        priorityZ--;
                    }

                    //if (data.IsSurface)
                    //{
                    //    priorityZ--;
                    //}

                    if (data.Height != 0)
                    {
                        priorityZ++;
                    }

                    if (data.IsMultiMovable)
                    {
                        priorityZ++;
                    }

                    break;
            }

            obj.PriorityZ = priorityZ;

            if (Tiles[x, y] == null)
            {
                Tiles[x, y] = obj;
                obj.TPrevious = null;
                obj.TNext = null;

                return;
            }


            GameObject o = Tiles[x, y];

            if (o == obj)
            {
                if (o.Previous != null)
                {
                    o = (GameObject) o.Previous;
                }
                else if (o.Next != null)
                {
                    o = (GameObject) o.Next;
                }
                else
                {
                    return;
                }
            }

            while (o?.TPrevious != null)
            {
                o = o.TPrevious;
            }

            GameObject found = null;
            GameObject start = o;

            while (o != null)
            {
                int testPriorityZ = o.PriorityZ;

                if (testPriorityZ > priorityZ || testPriorityZ == priorityZ && (state == 0 || state == 1 && !(o is Land)))
                {
                    break;
                }

                found = o;
                o = o.TNext;
            }

            if (found != null)
            {
                obj.TPrevious = found;
                GameObject next = found.TNext;
                obj.TNext = next;
                found.TNext = obj;

                if (next != null)
                {
                    next.TPrevious = obj;
                }
            }
            else if (start != null)
            {
                obj.TNext = start;
                start.TPrevious = obj;
                obj.TPrevious = null;
            }
        }

        public void RemoveGameObject(GameObject obj, int x, int y)
        {
#if BROWSER_WASM
            // Dirty the chunk-mesh when a Land/Static/Multi is removed (chopped
            // tree, picked-up item, etc.) so the next fill rebuilds without it.
            Mesh.MarkDirtyIfNeeded(obj);
#endif
            ref GameObject firstNode = ref Tiles[x, y];

            if (firstNode == null || obj == null)
            {
                return;
            }

            if (firstNode == obj)
            {
                firstNode = obj.TNext;
            }

            if (obj.TNext != null)
            {
                obj.TNext.TPrevious = obj.TPrevious;
            }

            if (obj.TPrevious != null)
            {
                obj.TPrevious.TNext = obj.TNext;
            }

            obj.TPrevious = null;
            obj.TNext = null;
        }


        public void Destroy()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }

            if (Node.Next != null || Node.Previous != null)
            {
                Node.List?.Remove(Node);
            }

#if BROWSER_WASM
            // Free the chunk-mesh GPU vertex buffers when the chunk is destroyed.
            Mesh.Clear();
#endif

            IsDestroyed = true;
            //_pool.ReturnOne(this);
        }

        public void Clear()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    GameObject first = GetHeadObject(x: i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }

            if (Node.Next != null || Node.Previous != null)
            {
                Node.List?.Remove(Node);
            }

            IsDestroyed = true;
        }

        /// <summary>
        /// Clears the chunk's tile objects for an in-place reload (UltimaLive block
        /// update) WITHOUT destroying or unlinking the chunk itself. The chunk stays
        /// owned by the map: its <see cref="Map.Map._terrainChunks"/> slot and
        /// <see cref="Node"/> are left intact and <see cref="IsDestroyed"/> stays false.
        /// Using <see cref="Clear"/>/<see cref="Destroy"/> here would remove
        /// <see cref="Node"/> from the map's used-indices list while the slot still
        /// references this chunk; the reloaded chunk would then no longer be tracked
        /// for cleanup (it could never be garbage collected by ClearUnusedBlocks and
        /// would stay loaded until relog).
        /// </summary>
        public void ClearForReload()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                }
            }
        }

        public bool HasNoExternalData()
        {
            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    for (GameObject obj = GetHeadObject(i, j); obj != null; obj = obj.TNext)
                    {
                        if (!(obj is Land) && !(obj is Static) /*&& !(obj is Multi)*/)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}
