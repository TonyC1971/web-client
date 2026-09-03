// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public sealed class MultiLoader : UOFileLoader
    {
        public const int MAX_MULTI_DATA_INDEX_COUNT = 0x2200;

        public MultiLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        internal UOFile File { get; private set; }

        public override unsafe void Load()
        {
            string uopPath = FileManager.GetUOFilePath("MultiCollection.uop");

            if (FileManager.IsUOPInstallation && System.IO.File.Exists(uopPath))
            {
                File = new UOFileUop(uopPath, "build/multicollection/{0:D6}.bin");
            }
            else
            {
                string path = FileManager.GetUOFilePath("multi.mul");
                string pathidx = FileManager.GetUOFilePath("multi.idx");

                if (System.IO.File.Exists(path) && System.IO.File.Exists(pathidx))
                {
                    File = new UOFileMul(path, pathidx);
                }
            }

            if (File == null)
            {
                throw new System.IO.FileNotFoundException(
                    "Multi files not found. Expected 'MultiCollection.uop' or " +
                    "'multi.mul' + 'multi.idx' in the UO data directory: " +
                    $"{FileManager.BasePath}");
            }

            File.FillEntries();
        }

        public List<MultiInfo> GetMultis(uint idx)
        {
            var list = new List<MultiInfo>();

            UOFile file = File;
            ref UOFileIndex entry = ref file.GetValidRefEntry((int)idx);

            if (entry.File != null)
                file = entry.File;

            file.Seek(entry.Offset, System.IO.SeekOrigin.Begin);

            byte[] buf = new byte[entry.Length];
            file.Read(buf);

            var reader = new StackDataReader(buf);
            if (entry.CompressionFlag >= CompressionType.Zlib)
            {
                byte[] dbuf = new byte[entry.DecompressedLength];
                ZLib.ZLibError result = ZLib.Decompress(buf, dbuf);
                reader = new StackDataReader(dbuf);

                reader.Skip(sizeof(uint));

                int count = reader.ReadInt32LE();

                for (int i = 0; i < count; ++i)
                {
                    // Guard: a corrupt in-file count must not read past the decompressed buffer.
                    if (reader.Position + Unsafe.SizeOf<MultiBlockNew>() > reader.Length)
                    {
                        break;
                    }
                    MultiBlockNew block = reader.Read<MultiBlockNew>();

                    if (block.Unknown != 0)
                    {
                        reader.Skip((int)(block.Unknown * sizeof(uint)));
                    }

                    list.Add(new ()
                    {
                        ID = block.ID,
                        X = block.X,
                        Y = block.Y,
                        Z = block.Z,
                        IsVisible = block.Flags == 0 || block.Flags == 0x100
                    });
                }
            }
            else
            {
                // Size-signature hardening (audit 2026-07-05, mirror of the mini fix): the MUL record size
                // was chosen purely from the DECLARED client version — the "version lies about the fileset
                // era" root cause the TileDataLoader size-signature fix defeats. Trust the FILE when exactly
                // one record size (12 old / 16 new) divides its length; keep the version gate as the
                // tie-breaker (both fit only when Length % 48 == 0). Plus a per-record bounds guard so a
                // still-skewed size/count can never over-read the buffer (GetMultis has no try/catch).
                int oldSize = Unsafe.SizeOf<MultiBlock>();          // 12 (pre-7.0.9.0)
                int newSize = Unsafe.SizeOf<MultiBlockNew>() + 2;   // 16 (7.0.9.0+)
                bool oldFits = entry.Length % oldSize == 0;
                bool newFits = entry.Length % newSize == 0;
                int size = FileManager.Version >= ClientVersion.CV_7090 ? newSize : oldSize;
                if (oldFits != newFits)
                {
                    size = oldFits ? oldSize : newSize;
                }
                int count = entry.Length / size;

                for (int i = 0; i < count; ++i)
                {
                    if (reader.Position + Unsafe.SizeOf<MultiBlock>() > reader.Length)
                    {
                        break;
                    }
                    MultiBlock block = reader.Read<MultiBlock>();
                    reader.Skip(size - Unsafe.SizeOf<MultiBlock>());

                    list.Add(new ()
                    {
                        ID = block.ID,
                        X = block.X,
                        Y = block.Y,
                        Z = block.Z,
                        IsVisible = block.Flags != 0
                    });
                }
            }

            return list;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct MultiBlock
        {
            public ushort ID;
            public short X;
            public short Y;
            public short Z;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct MultiBlockNew
        {
            public ushort ID;
            public short X;
            public short Y;
            public short Z;
            public ushort Flags;
            public uint Unknown;
        }
    }

    public struct MultiInfo
    {
        public ushort ID;
        public short X;
        public short Y;
        public short Z;
        public bool IsVisible;
    }
}