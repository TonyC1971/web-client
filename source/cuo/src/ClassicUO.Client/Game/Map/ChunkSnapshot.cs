// SPDX-License-Identifier: BSD-2-Clause

#if BROWSER_WASM
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// v0.5.25 F1: In-memory cache of MUL-derived chunk content (Lands +
    /// StaticFile Statics) keyed by (chunkX, chunkY) within a Map. Survives
    /// chunk-pool eviction so that when the player revisits a previously
    /// loaded chunk, we skip MapFile.Read + ApplyStretch + StaticFile.Read
    /// (the IDBFS-backed I/O that dominates getChunk2 cost in v0.5.24).
    ///
    /// The cached data is MUL-immutable: Lands and StaticFile Statics derive
    /// from the read-only map/statics files. Items, Mobiles, Multis, and
    /// server-pushed dynamic statics are NOT cached — they stream from the
    /// server on demand. No invalidation logic needed for normal gameplay;
    /// the snapshot is a deterministic function of the (mapIndex, chunkX,
    /// chunkY) tuple.
    ///
    /// Per-chunk memory: ~64 Lands × ~80 bytes + ~150 Statics × 8 bytes
    /// ≈ 6 KB. Default LRU cap 1500 chunks ≈ 9 MB. Cheap.
    /// </summary>
    internal struct LandSnapshot
    {
        public ushort Graphic;
        public sbyte Z;
        public bool IsStretched;
        public sbyte AverageZ;
        public sbyte MinZ;
        public YOffsets YOffsets;
        public Vector3 NormalTop;
        public Vector3 NormalRight;
        public Vector3 NormalLeft;
        public Vector3 NormalBottom;
    }

    internal struct StaticSnapshot
    {
        public ushort Graphic;
        public ushort Hue;
        public byte LocalX;
        public byte LocalY;
        public sbyte Z;
    }

    internal sealed class ChunkSnapshot
    {
        // v0.8.94 A/B switch (operator request): CUO_SNAPSHOT_MODE env var,
        // set by main.js from the `?snapshot=` URL param. Values:
        //   full (DEFAULT) — F1 in-memory + F2 OPFS persist, exactly as today.
        //   mem            — F1 only; OPFS persistence disabled.
        //   off            — snapshot cache fully disabled; every chunk loads
        //                    fresh from the MULs.
        // Default is unchanged behaviour for players; the param exists so the
        // open "does this cache still pay for itself in the MEMFS era?"
        // decision (docs/cuo/CHUNK_SNAPSHOT_CACHE.md §6) can be settled by
        // A/B measurement on the SAME deployed build: run the same walk with
        // ?snapshot=full vs ?snapshot=off and compare [chunk-profile]
        // snapC/snapSaved + [fps].
        public static readonly string Mode = ParseMode();
        public static readonly bool Enabled = Mode != "off";
        public static readonly bool PersistEnabled = Mode == "full";

        private static string ParseMode()
        {
            string m = null;
            try { m = System.Environment.GetEnvironmentVariable("CUO_SNAPSHOT_MODE"); } catch { /* env unavailable */ }
            m = string.IsNullOrEmpty(m) ? "full" : m.Trim().ToLowerInvariant();
            if (m != "off" && m != "mem" && m != "full") m = "full";
            System.Console.WriteLine($"[snap-mode] {m} (F1={(m != "off" ? "on" : "OFF")} F2-persist={(m == "full" ? "on" : "OFF")})");
            return m;
        }

        public readonly LandSnapshot[] Lands = new LandSnapshot[64];
        public StaticSnapshot[] Statics = System.Array.Empty<StaticSnapshot>();
        public int StaticsCount;
        public long LastAccessTime; // LRU eviction order
        // v0.8.93: the authoritative staidx entry count for this block at
        // CAPTURE time. Hydration re-reads the live staidx and REJECTS the
        // snapshot on mismatch (falls back to a fresh MUL read). Catches
        // (a) snapshots captured from an incompletely-loaded chunk against
        // a different-era file (the operator's persistent missing-walls bug:
        // tile(1479,1608) had 0 statics hydrated while statics0.mul holds 3),
        // and (b) snapshots that outlive a shard gamefile update. Schema v2.
        public int FileStaticCount;

        // Stats — global counters reset by GameScene fill-sub.
        public static long CacheHits = 0;
        public static long CacheMisses = 0;
        public static long CacheSavedChunks = 0;
        public static long CacheEvictions = 0;
        // v1.0.17: captures REFUSED because the live tile lists held fewer Statics than the MUL
        // read inserted. Writing one of those is how a partial snapshot enters the cache and then
        // outlives every later check — see the capture-guard in Chunk.SaveSnapshotToMap.
        public static long CaptureSkippedLossy = 0;

        public void EnsureStaticsCapacity(int n)
        {
            if (Statics.Length < n)
            {
                int newSize = Statics.Length > 0 ? Statics.Length * 2 : 16;
                while (newSize < n) newSize *= 2;
                Statics = new StaticSnapshot[newSize];
            }
        }
    }
}
#endif
