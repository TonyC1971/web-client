// SPDX-License-Identifier: BSD-2-Clause

#if BROWSER_WASM
using System;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// v0.5.26 F2: cross-session OPFS persistence of the in-memory chunk
    /// snapshot cache (F1). Snapshots serialize to fixed-layout binary blobs
    /// in OPFS dir `cuo-snapshots`. Boot reads all snapshots back into
    /// Map._chunkSnapshots so the second day of play starts with the cache
    /// already warm (gameplay-normal = 0 cold-loads after first session).
    ///
    /// File layout (little-endian):
    ///   [0..3]   magic "SHOT" = 0x544F4853
    ///   [4..7]   schemaVersion = 1
    ///   [8..11]  mapIndex (u32)
    ///   [12..15] chunkX (u32)
    ///   [16..19] chunkY (u32)
    ///   [20..27] lastAccessTime (i64)
    ///   [28..N]  Lands array (always 64 entries, ~80 bytes each)
    ///   [N..M]   StaticsCount (u32) followed by Statics array
    ///
    /// Filename: `m{mapIndex}_c{chunkX}_{chunkY}.bin` — flat in OPFS dir.
    ///
    /// Eviction policy: none on OPFS side (storage cheap). Memory side
    /// retains the F1 LRU cap of 1500.
    /// </summary>
    internal static partial class ChunkSnapshotPersist
    {
        public const uint MAGIC = 0x544F4853; // 'SHOT' LE
        // v0.8.93: 1 → 2. v2 adds FileStaticCount (capture-time staidx entry
        // count) to the header for hydration self-validation. The bump also
        // deliberately invalidates EVERY v1 snapshot persisted in players'
        // OPFS: v1 blobs can carry chunks captured incomplete in the
        // pre-integrity-verification era (operator's persistent missing-walls
        // bug) and there is no way to validate them retroactively — they
        // regenerate from the now-verified MUL files on first revisit.
        // v0.9.371: 2 → 3. Same layout as v2; the bump wholesale-invalidates
        // every v2 blob because three poison vectors existed while v2 was
        // live and none is detectable retroactively: (a) generational decay —
        // hydrated chunks re-captured on Destroy, so content-level poison
        // with a matching staidx count self-perpetuated; (b) the boot OPFS
        // load compared session-relative Time.Ticks and could clobber a
        // fresh same-session capture with yesterday's file; (c) snapshots
        // were keyed by (map,chunk) only — every shard on the origin shared
        // one pool, so two filesets whose block staidx counts coincided
        // (0 == 0 above all: ocean/void) cross-hydrated each other's terrain.
        // v3 snapshots are written only from complete same-session MUL reads
        // (Chunk.StaticsFromFile) into a per-fileset OPFS namespace.
        // v1.0.17: 3 -> 4. The FOURTH poison vector, and v3's own guard could not see it.
        // StaticsFromFile attests the READ; the capture happens much later, walking the LIVE tile
        // lists and skipping destroyed objects, so a Static removed in between is simply absent
        // from the payload. That snapshot is partial and passes every check: the count is not
        // zero so the poison probe (== 0) never runs, and FileStaticCount still matches the live
        // index. Operator, 2026-08-21: walls missing in one district, correct after clearing the
        // cache, broken again in the next district of the SAME session; `?snapshot=off` makes it
        // disappear entirely. The capture-guard in Chunk.SaveSnapshotToMap stops new ones being
        // written; this bump is what removes the ones already on players' disks, which nothing
        // else can — a partial v3 blob is indistinguishable from a legitimate one.
        public const uint SCHEMA_VERSION = 4;

        // Stats (snapshot at end of session for diagnostics).
        public static long OpfsLoadCount = 0;
        public static long OpfsLoadBytes = 0;
        public static long OpfsWriteCount = 0;
        public static long OpfsWriteBytes = 0;
        public static long OpfsErrors = 0;

        // ── JS interop bridge ─────────────────────────────────────────
        // Each method maps to a globalThis function defined in main.js.
        // All async — return Task because Mercury MT can't run synchronous
        // C#→JS crossings from deputy worker.

        // Note: JSImport source-generator doesn't support Task<byte[]> or
        // Task<string[]> directly. Use explicit JSMarshalAs<Promise<...>>
        // and encode byte buffers as base64 strings (33 % overhead but clean
        // interop boundary; alternative MemoryView async patterns are not
        // supported in Mercury MT deputy context).

        // Mercury MT JSImport source-generator doesn't support array return
        // types in Task<T>. Use newline-joined strings + split in C# instead.

        [JSImport("globalThis.cuoOpfsSnapshotListJoined")]
        internal static partial Task<string> ListSnapshotKeysJoined();

        [JSImport("globalThis.cuoOpfsSnapshotReadB64")]
        internal static partial Task<string> ReadSnapshotB64(string key);

        [JSImport("globalThis.cuoOpfsSnapshotWriteB64")]
        internal static partial Task<bool> WriteSnapshotB64(string key, string b64);

        [JSImport("globalThis.cuoOpfsSnapshotDelete")]
        internal static partial Task<bool> DeleteSnapshot(string key);

        public static async Task<string[]> ListSnapshotKeys()
        {
            string joined = await ListSnapshotKeysJoined();
            if (string.IsNullOrEmpty(joined)) return Array.Empty<string>();
            return joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        // Wrappers for callers — handle base64 encoding/decoding.
        public static async Task<byte[]> ReadSnapshot(string key)
        {
            string b64 = await ReadSnapshotB64(key);
            if (string.IsNullOrEmpty(b64)) return null;
            try { return Convert.FromBase64String(b64); }
            catch { return null; }
        }

        public static Task WriteSnapshot(string key, byte[] data)
        {
            string b64 = Convert.ToBase64String(data);
            return WriteSnapshotB64(key, b64);
        }

        // ── Serialisation ─────────────────────────────────────────────

        public static byte[] Serialize(int mapIndex, int chunkX, int chunkY, ChunkSnapshot snap)
        {
            // Compute exact size up front:
            //   header = 32 bytes (magic + version + map + cx + cy + lastAccess
            //            + fileStaticCount [v2])
            //   lands = 64 × per-Land size
            //   statics block = 4 (count) + StaticsCount × per-Static size
            const int HEADER = 32;
            // Per-Land = graphic(2) + z(1) + isStretched(1) + averageZ(1) + minZ(1)
            //          + yoff(4×4=16) + normals(4 × 3×4=48) = 72 (with 2 padding)
            const int LAND_BYTES = 72;
            // Per-Static = graphic(2) + hue(2) + localX(1) + localY(1) + z(1) + pad(1) = 8
            const int STATIC_BYTES = 8;

            int landsSize = 64 * LAND_BYTES;
            int staticsSize = 4 + snap.StaticsCount * STATIC_BYTES;
            byte[] buf = new byte[HEADER + landsSize + staticsSize];

            using var ms = new MemoryStream(buf, writable: true);
            using var bw = new BinaryWriter(ms);
            bw.Write(MAGIC);
            bw.Write(SCHEMA_VERSION);
            bw.Write((uint)mapIndex);
            bw.Write((uint)chunkX);
            bw.Write((uint)chunkY);
            bw.Write(snap.LastAccessTime);
            bw.Write(snap.FileStaticCount); // v2
            // Lands (64 always, packed in 8×8 order)
            for (int i = 0; i < 64; i++)
            {
                ref var l = ref snap.Lands[i];
                bw.Write(l.Graphic);
                bw.Write(l.Z);
                bw.Write(l.IsStretched ? (byte)1 : (byte)0);
                bw.Write(l.AverageZ);
                bw.Write(l.MinZ);
                // 2 bytes padding to align YOffsets ints to 4
                bw.Write((ushort)0);
                bw.Write(l.YOffsets.Top);
                bw.Write(l.YOffsets.Right);
                bw.Write(l.YOffsets.Left);
                bw.Write(l.YOffsets.Bottom);
                WriteVec3(bw, l.NormalTop);
                WriteVec3(bw, l.NormalRight);
                WriteVec3(bw, l.NormalLeft);
                WriteVec3(bw, l.NormalBottom);
            }
            // Statics
            bw.Write((uint)snap.StaticsCount);
            for (int i = 0; i < snap.StaticsCount; i++)
            {
                ref var s = ref snap.Statics[i];
                bw.Write(s.Graphic);
                bw.Write(s.Hue);
                bw.Write(s.LocalX);
                bw.Write(s.LocalY);
                bw.Write(s.Z);
                bw.Write((byte)0); // padding
            }
            return buf;
        }

        public static bool TryDeserialize(byte[] buf, out int mapIndex, out int chunkX, out int chunkY, out ChunkSnapshot snap)
        {
            mapIndex = 0; chunkX = 0; chunkY = 0; snap = null;
            if (buf == null || buf.Length < 32) return false;

            using var ms = new MemoryStream(buf, writable: false);
            using var br = new BinaryReader(ms);
            uint magic = br.ReadUInt32();
            if (magic != MAGIC) return false;
            uint version = br.ReadUInt32();
            if (version != SCHEMA_VERSION) return false;
            mapIndex = (int)br.ReadUInt32();
            chunkX = (int)br.ReadUInt32();
            chunkY = (int)br.ReadUInt32();
            long lastAccess = br.ReadInt64();
            int fileStaticCount = br.ReadInt32(); // v2

            snap = new ChunkSnapshot { LastAccessTime = lastAccess, FileStaticCount = fileStaticCount };
            for (int i = 0; i < 64; i++)
            {
                ref var l = ref snap.Lands[i];
                l.Graphic = br.ReadUInt16();
                l.Z = br.ReadSByte();
                l.IsStretched = br.ReadByte() != 0;
                l.AverageZ = br.ReadSByte();
                l.MinZ = br.ReadSByte();
                br.ReadUInt16(); // padding
                l.YOffsets.Top = br.ReadInt32();
                l.YOffsets.Right = br.ReadInt32();
                l.YOffsets.Left = br.ReadInt32();
                l.YOffsets.Bottom = br.ReadInt32();
                l.NormalTop = ReadVec3(br);
                l.NormalRight = ReadVec3(br);
                l.NormalLeft = ReadVec3(br);
                l.NormalBottom = ReadVec3(br);
            }
            int count = (int)br.ReadUInt32();
            if (count < 0 || count > 4096) return false; // sanity bound
            snap.EnsureStaticsCapacity(count);
            snap.StaticsCount = count;
            for (int i = 0; i < count; i++)
            {
                ref var s = ref snap.Statics[i];
                s.Graphic = br.ReadUInt16();
                s.Hue = br.ReadUInt16();
                s.LocalX = br.ReadByte();
                s.LocalY = br.ReadByte();
                s.Z = br.ReadSByte();
                br.ReadByte(); // padding
            }
            return true;
        }

        private static void WriteVec3(BinaryWriter bw, Vector3 v)
        {
            bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z);
        }

        private static Vector3 ReadVec3(BinaryReader br)
        {
            return new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }

        public static string KeyFor(int mapIndex, int chunkX, int chunkY)
        {
            return $"m{mapIndex}_c{chunkX}_{chunkY}.bin";
        }

        public static bool TryParseKey(string key, out int mapIndex, out int chunkX, out int chunkY)
        {
            mapIndex = 0; chunkX = 0; chunkY = 0;
            if (string.IsNullOrEmpty(key) || !key.StartsWith("m") || !key.EndsWith(".bin")) return false;
            int u1 = key.IndexOf('_'); if (u1 < 0) return false;
            int u2 = key.IndexOf('_', u1 + 1); if (u2 < 0) return false;
            int dot = key.IndexOf('.', u2 + 1); if (dot < 0) return false;
            if (!int.TryParse(key.AsSpan(1, u1 - 1), out mapIndex)) return false;
            if (key[u1 + 1] != 'c') return false;
            if (!int.TryParse(key.AsSpan(u1 + 2, u2 - u1 - 2), out chunkX)) return false;
            if (!int.TryParse(key.AsSpan(u2 + 1, dot - u2 - 1), out chunkY)) return false;
            return true;
        }
    }
}
#endif
