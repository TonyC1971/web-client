// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClassicUO.Game.GameObjects;
using ClassicUO.Assets;

namespace ClassicUO.Game.Map
{
    internal sealed class Map
    {
        // v0.5.17 R2: changed from static to instance field. Each Map owns
        // its chunk array so (a) different facets can't clobber each other's
        // chunks and (b) the LRU cache in World can hold old Maps alive with
        // their warm chunks intact after a facet swap.
        private Chunk[] _terrainChunks;
        private static readonly bool[] _blockAccessList = new bool[0x1000];
        private readonly LinkedList<int> _usedIndices = new LinkedList<int>();
        private readonly World _world;

#if BROWSER_WASM
        // v0.5.25 F1: persistent chunk snapshot cache. Survives pool eviction
        // so revisiting a chunk skips MapFile.Read + ApplyStretch + StaticFile.Read.
        // Keyed by block index (same scheme as _terrainChunks). Bounded LRU.
        private const int CHUNK_SNAPSHOT_CAP = 1500;
        private readonly Dictionary<int, ChunkSnapshot> _chunkSnapshots = new Dictionary<int, ChunkSnapshot>(CHUNK_SNAPSHOT_CAP);

        public bool TryGetChunkSnapshot(int chunkX, int chunkY, out ChunkSnapshot snap)
        {
            int block = GetBlock(chunkX, chunkY);
            if (block < 0 || block >= BlocksCount)
            {
                snap = null;
                return false;
            }
            return _chunkSnapshots.TryGetValue(block, out snap);
        }

        // UltimaLive rewrote this block's MUL data server-side — the cached
        // MUL-derived snapshot is now STALE and must not seed later rebuilds
        // (the poisoned-snapshot/ghost-walls class). Called by
        // Chunk.ClearForReload (upstream 664bd36b7 port, 2026-07-20).
        public void DropChunkSnapshot(int chunkX, int chunkY)
        {
            int block = GetBlock(chunkX, chunkY);
            if (block < 0 || block >= BlocksCount) return;
            _chunkSnapshots.Remove(block);
        }

        public void SaveChunkSnapshot(int chunkX, int chunkY, ChunkSnapshot snap)
        {
            if (!ChunkSnapshot.Enabled) return;   // v0.8.94 ?snapshot=off
            int block = GetBlock(chunkX, chunkY);
            if (block < 0 || block >= BlocksCount) return;
            snap.LastAccessTime = Time.Ticks;
            _chunkSnapshots[block] = snap;
            // LRU eviction: if over cap, drop the 10 % oldest entries in one pass.
            if (_chunkSnapshots.Count > CHUNK_SNAPSHOT_CAP)
            {
                int evictCount = CHUNK_SNAPSHOT_CAP / 10;
                var oldest = new List<KeyValuePair<int, ChunkSnapshot>>(_chunkSnapshots);
                oldest.Sort((a, b) => a.Value.LastAccessTime.CompareTo(b.Value.LastAccessTime));
                for (int i = 0; i < evictCount && i < oldest.Count; i++)
                {
                    _chunkSnapshots.Remove(oldest[i].Key);
                    ChunkSnapshot.CacheEvictions++;
                }
            }
            // v0.5.26 F2: async fire-and-forget persist to OPFS. The Task
            // is intentionally not awaited — OPFS writes happen in the
            // browser's storage queue without blocking the game loop. If a
            // write fails we just log; the in-memory cache is still valid.
            // v0.8.94: skipped in ?snapshot=mem mode (F1-only A/B).
            if (ChunkSnapshot.PersistEnabled)
            {
                _ = PersistSnapshotAsync(chunkX, chunkY, snap);
            }
        }

        private async Task PersistSnapshotAsync(int chunkX, int chunkY, ChunkSnapshot snap)
        {
            try
            {
                byte[] data = ChunkSnapshotPersist.Serialize(Index, chunkX, chunkY, snap);
                await ChunkSnapshotPersist.WriteSnapshot(ChunkSnapshotPersist.KeyFor(Index, chunkX, chunkY), data);
                ChunkSnapshotPersist.OpfsWriteCount++;
                ChunkSnapshotPersist.OpfsWriteBytes += data.Length;
            }
            catch
            {
                ChunkSnapshotPersist.OpfsErrors++;
            }
        }

        // v0.5.26 F2: boot-time loader. Called once after dotnet runtime is
        // ready; reads every snapshot file from OPFS dir into the in-memory
        // cache. Skips files matching a different mapIndex (each Map only
        // restores its own facet's snapshots).
        public async Task LoadSnapshotsFromOpfsAsync()
        {
            // v0.8.94: in ?snapshot=mem/off A/B modes the persisted snapshots
            // are ignored entirely (the on-disk files are left untouched, so
            // flipping back to full mode restores them).
            if (!ChunkSnapshot.PersistEnabled)
            {
                System.Console.WriteLine($"[opfs-snap] mapIndex={Index} SKIPPED (snapshot mode={ChunkSnapshot.Mode})");
                return;
            }
            try
            {
                string[] keys = await ChunkSnapshotPersist.ListSnapshotKeys();
                if (keys == null || keys.Length == 0) return;
                int loaded = 0;
                foreach (var key in keys)
                {
                    if (!ChunkSnapshotPersist.TryParseKey(key, out int mapIdx, out int cx, out int cy))
                        continue;
                    if (mapIdx != Index) continue;
                    byte[] data = await ChunkSnapshotPersist.ReadSnapshot(key);
                    if (data == null) continue;
                    if (!ChunkSnapshotPersist.TryDeserialize(data, out _, out _, out _, out ChunkSnapshot snap))
                        continue;
                    int block = GetBlock(cx, cy);
                    if (block < 0 || block >= BlocksCount) continue;
                    // v0.9.371: only fill GAPS — an entry already in memory was
                    // captured THIS session from the verified MUL (or validated
                    // on hydrate) and is always fresher than an OPFS file. The
                    // old rule compared LastAccessTime, but Time.Ticks is
                    // session-relative: yesterday's long session (tick ~3.6M)
                    // beat today's early capture (tick ~5K) and CLOBBERED it —
                    // spawn-area chunks loaded before this async pass finished
                    // were exactly the ones randomly resurrecting stale data.
                    if (!_chunkSnapshots.ContainsKey(block))
                    {
                        _chunkSnapshots[block] = snap;
                    }
                    ChunkSnapshotPersist.OpfsLoadCount++;
                    ChunkSnapshotPersist.OpfsLoadBytes += data.Length;
                    loaded++;
                }
                System.Console.WriteLine($"[opfs-snap] mapIndex={Index} loaded={loaded}/{keys.Length} keys");
            }
            catch (System.Exception e)
            {
                ChunkSnapshotPersist.OpfsErrors++;
                System.Console.WriteLine($"[opfs-snap] load failed: {e.Message}");
            }
        }
#endif


        public Map(World world, int index)
        {
            _world = world;
            Index = index;
            BlocksCount = Client.Game.UO.FileManager.Maps.MapBlocksSize[Index, 0] * Client.Game.UO.FileManager.Maps.MapBlocksSize[Index, 1];

            _terrainChunks = new Chunk[BlocksCount];

            ClearBockAccess();
#if BROWSER_WASM
            // v0.5.26 F2: fire-and-forget OPFS load. Snapshots from prior
            // sessions hydrate `_chunkSnapshots` async. If the player walks
            // into a chunk before its OPFS snapshot finishes loading the
            // chunk falls through to LoadLandOnly normally and gets a fresh
            // snapshot via F1. The OPFS-loaded snapshot only wins if the
            // in-memory entry doesn't already have a newer LastAccessTime
            // (LoadSnapshotsFromOpfsAsync enforces that ordering).
            _ = LoadSnapshotsFromOpfsAsync();
#endif
        }

        public readonly int BlocksCount;
        public readonly int Index;


        public Chunk GetChunk(int block)
        {
            if (block >= 0 && block < BlocksCount)
            {
                return _terrainChunks[block];
            }

            return null;
        }

        public Chunk GetChunk(int x, int y, bool load = true)
        {
            if (x < 0 || y < 0)
            {
                return null;
            }

            int cellX = x >> 3;
            int cellY = y >> 3;

            return GetChunk2(cellX, cellY, load);
        }

#if BROWSER_WASM
        // v0.4.6 chunk.Load is the dominant per-fill cost in dense areas
        // (Britain ~3-5 ms × 7 fresh chunks per chunk-cross = 24-35 ms).
        // Caller checks HasChunkLoaded(chunkX, chunkY) and skips fresh
        // positions if its load budget is exhausted, deferring them to a
        // future fill. Result: at most N fresh chunks loaded per frame.
        public bool HasChunkLoaded(int chunkX, int chunkY)
        {
            int block = GetBlock(chunkX, chunkY);
            if (block < 0 || block >= BlocksCount) return false;
            return _terrainChunks[block] != null && !_terrainChunks[block].IsDestroyed;
        }
#endif

        // v0.5.17 R3: landOnly=true loads only land tiles (fast MapBlock read,
        // no StaticFile I/O). Use during teleport burst fills to avoid the
        // ~100ms/chunk IDBFS statics penalty. Statics are loaded later by
        // the catchup pass (GameScene calls chunk.LoadStaticsOnly per fill).
        public Chunk GetChunk2(int chunkX, int chunkY, bool load = true, bool landOnly = false)
        {
            int block = GetBlock(chunkX, chunkY);

            if (block >= BlocksCount)
            {
                return null;
            }

            ref Chunk chunk = ref _terrainChunks[block];

            if (chunk == null)
            {
                if (!load)
                {
                    return null;
                }

                LinkedListNode<int> node = _usedIndices.AddLast(block);
                chunk = Chunk.Create(_world, chunkX, chunkY);
#if BROWSER_WASM
                // v0.5.25 F1: try snapshot cache first. On hit, skip MUL reads
                // entirely and hydrate from cached Lands + Statics. Honour
                // landOnly: even if snapshot has Statics, defer them to
                // catchup if caller asked for land-only (mid-burst).
                if (ChunkSnapshot.Enabled && !landOnly && _chunkSnapshots.TryGetValue(block, out var snapHit))
                {
                    chunk.LoadFromSnapshot(snapHit, Index);
                }
                else
                {
                    ChunkSnapshot.CacheMisses++;
                    if (landOnly)
                        chunk.LoadLandOnly(Index);
                    else
                        chunk.Load(Index);
                }
#else
                if (landOnly)
                    chunk.LoadLandOnly(Index);
                else
                    chunk.Load(Index);
#endif
                chunk.Node = node;
            }
            else if (chunk.IsDestroyed)
            {
                // make sure node is clear
                if (chunk.Node != null && (chunk.Node.Previous != null || chunk.Node.Next != null))
                {
                    chunk.Node.List?.Remove(chunk.Node);
                }

                LinkedListNode<int> node = _usedIndices.AddLast(block);
                chunk.X = chunkX;
                chunk.Y = chunkY;
#if BROWSER_WASM
                if (ChunkSnapshot.Enabled && !landOnly && _chunkSnapshots.TryGetValue(block, out var snapHit2))
                {
                    chunk.LoadFromSnapshot(snapHit2, Index);
                }
                else
                {
                    ChunkSnapshot.CacheMisses++;
                    if (landOnly)
                        chunk.LoadLandOnly(Index);
                    else
                        chunk.Load(Index);
                }
#else
                if (landOnly)
                    chunk.LoadLandOnly(Index);
                else
                    chunk.Load(Index);
#endif
                chunk.Node = node;
            }

            chunk.LastAccessTime = Time.Ticks;

            return chunk;
        }


        public GameObject GetTile(int x, int y, bool load = true)
        {
            return GetChunk(x, y, load)?.GetHeadObject(x % 8, y % 8);
        }

#if BROWSER_WASM
        // v0.5.21 strategy #5b: GetTileZ MapBlock LRU cache.
        //
        // Bot data v0.5.20 [chunk-profile] showed 99 % of the 4–5 s freeze
        // lives in `getChunk2` (= chunk.LoadLandOnly), and inside it the hot
        // path is Land.ApplyStretch which calls GetTileZ 11× per tile. Per
        // chunk: 64 tiles × 11 = 704 GetTileZ calls. Per heavy fill
        // (49 chunks): ~34 500 GetTileZ calls — each doing a fresh
        // MapFile.Seek + Read<MapBlock> (192 bytes) for a single Z byte.
        //
        // Adjacent tiles in the same chunk share the SAME MapBlock, and most
        // ApplyStretch neighbours are inside the loading chunk's own block
        // (only the 28 edge tiles peek into adjacent blocks). A tiny LRU of
        // the last MapBlocks read collapses ~85 % of those reads.
        //
        // The cache is per-Map (each facet gets its own) and lives for the
        // lifetime of the Map instance. Each entry stores (blockX, blockY)
        // as key and a copy of the MapBlock struct. Eviction is "replace the
        // oldest slot" — round-robin since the access pattern is highly
        // local and a tiny ring is fine.
        private const int Z_BLOCK_CACHE_SIZE = 8;
        private readonly int[] _zBlockCacheX = new int[Z_BLOCK_CACHE_SIZE];
        private readonly int[] _zBlockCacheY = new int[Z_BLOCK_CACHE_SIZE];
        private readonly MapBlock[] _zBlockCacheBlock = new MapBlock[Z_BLOCK_CACHE_SIZE];
        private readonly bool[] _zBlockCacheValid = new bool[Z_BLOCK_CACHE_SIZE];
        private int _zBlockCacheNextSlot;
        public static long ZBlockCacheHits = 0;
        public static long ZBlockCacheMisses = 0;
#endif

        public sbyte GetTileZ(int x, int y)
        {
            if (x < 0 || y < 0)
            {
                return -125;
            }

            int blockX = x >> 3;
            int blockY = y >> 3;
            int mx = x % 8;
            int my = y % 8;

#if BROWSER_WASM
            for (int i = 0; i < Z_BLOCK_CACHE_SIZE; i++)
            {
                if (_zBlockCacheValid[i] && _zBlockCacheX[i] == blockX && _zBlockCacheY[i] == blockY)
                {
                    ZBlockCacheHits++;
                    return _zBlockCacheBlock[i].Cells[(my << 3) + mx].Z;
                }
            }
            ZBlockCacheMisses++;
#endif

            ref var blockIndex = ref GetIndex(blockX, blockY);

            if (!blockIndex.IsValid())
            {
                return -125;
            }

            unsafe
            {
                blockIndex.MapFile.Seek((long)blockIndex.MapAddress, System.IO.SeekOrigin.Begin);
                var block = blockIndex.MapFile.Read<MapBlock>();
#if BROWSER_WASM
                int slot = _zBlockCacheNextSlot;
                _zBlockCacheX[slot] = blockX;
                _zBlockCacheY[slot] = blockY;
                _zBlockCacheBlock[slot] = block;
                _zBlockCacheValid[slot] = true;
                _zBlockCacheNextSlot = (slot + 1) % Z_BLOCK_CACHE_SIZE;
#endif
                return block.Cells[(my << 3) + mx].Z;
            }
        }

        public void GetMapZ(int x, int y, out sbyte groundZ, out sbyte staticZ)
        {
            Chunk chunk = GetChunk(x, y);
            //var obj = GetTile(x, y);
            groundZ = staticZ = 0;

            if (chunk == null)
            {
                return;
            }

            GameObject obj = chunk.Tiles[x % 8, y % 8];

            while (obj != null)
            {
                if (obj is Land)
                {
                    groundZ = obj.Z;
                }
                else if (staticZ < obj.Z)
                {
                    staticZ = obj.Z;
                }

                obj = obj.TNext;
            }
        }

        public void ClearBockAccess()
        {
            _blockAccessList.AsSpan().Fill(false);
        }

        public sbyte CalculateNearZ(sbyte defaultZ, int x, int y, int z)
        {
            ref bool access = ref _blockAccessList[(x & 0x3F) + ((y & 0x3F) << 6)];

            if (access)
            {
                return defaultZ;
            }

            access = true;
            Chunk chunk = GetChunk(x, y, false);

            if (chunk != null)
            {
                GameObject obj = chunk.Tiles[x % 8, y % 8];

                for (; obj != null; obj = obj.TNext)
                {
                    if (!(obj is Static) && !(obj is Multi))
                    {
                        continue;
                    }

                    if (obj.Graphic >= Client.Game.UO.FileManager.TileData.StaticData.Length)
                    {
                        continue;
                    }

                    if (!Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic].IsRoof || Math.Abs(z - obj.Z) > 6)
                    {
                        continue;
                    }

                    break;
                }

                if (obj == null)
                {
                    return defaultZ;
                }

                sbyte tileZ = obj.Z;

                if (tileZ < defaultZ)
                {
                    defaultZ = tileZ;
                }

                defaultZ = CalculateNearZ(defaultZ, x - 1, y, tileZ);
                defaultZ = CalculateNearZ(defaultZ, x + 1, y, tileZ);
                defaultZ = CalculateNearZ(defaultZ, x, y - 1, tileZ);
                defaultZ = CalculateNearZ(defaultZ, x, y + 1, tileZ);
            }

            return defaultZ;
        }


        public ref IndexMap GetIndex(int blockX, int blockY)
        {
            int block = GetBlock(blockX, blockY);
            int map = Index;
            Client.Game.UO.FileManager.Maps.SanitizeMapIndex(ref map);
            IndexMap[] list = Client.Game.UO.FileManager.Maps.BlockData[map];

            return ref block >= list.Length ? ref IndexMap.Invalid : ref list[block];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetBlock(int blockX, int blockY)
        {
            return blockX * Client.Game.UO.FileManager.Maps.MapBlocksSize[Index, 1] + blockY;
        }

        public IEnumerable<Chunk> GetUsedChunks()
        {
            foreach (int i in _usedIndices)
            {
                yield return GetChunk(i);
            }
        }


        public void ClearUnusedBlocks()
        {
            int count = 0;
            long ticks = Time.Ticks - Constants.CLEAR_TEXTURES_DELAY;

            LinkedListNode<int> first = _usedIndices.First;

            while (first != null)
            {
                LinkedListNode<int> next = first.Next;

                ref Chunk block = ref _terrainChunks[first.Value];

                if (block != null && block.LastAccessTime < ticks && block.HasNoExternalData())
                {
                    block.Destroy();
                    block = null;

                    if (++count >= Constants.MAX_MAP_OBJECT_REMOVED_BY_GARBAGE_COLLECTOR)
                    {
                        break;
                    }
                }

                first = next;
            }
        }

        public void Destroy()
        {
            LinkedListNode<int> first = _usedIndices.First;

            while (first != null)
            {
                LinkedListNode<int> next = first.Next;
                ref Chunk c = ref _terrainChunks[first.Value];
                c?.Destroy();
                c = null;
                first = next;
            }

            _usedIndices.Clear();
        }
    }
}