// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public sealed class UOFileManager : IDisposable
    {
        private readonly UOFilesOverrideMap _overrideMap;

        public UOFileManager(ClientVersion clientVersion, string uoPath)
        {
            Version = clientVersion;
            BasePath = uoPath;

            Animations = new AnimationsLoader(this);
            AnimData = new AnimDataLoader(this);
            Arts = new ArtLoader(this);
            Maps = new MapLoader(this);
            Clilocs = new ClilocLoader(this);
            Gumps = new GumpsLoader(this);
            Fonts = new FontsLoader(this);
            Hues = new HuesLoader(this);
            TileData = new TileDataLoader(this);
            Multis = new MultiLoader(this);
            Skills = new SkillsLoader(this);
            Texmaps = new TexmapsLoader(this);
            Speeches = new SpeechesLoader(this);
            Lights = new LightsLoader(this);
            Sounds = new SoundsLoader(this);
            MultiMaps = new MultiMapLoader(this);
            Verdata = new VerdataLoader(this);
            Professions = new ProfessionLoader(this);
            TileArt = new TileArtLoader(this);
            StringDictionary = new StringDictionaryLoader(this);

            _overrideMap = new UOFilesOverrideMap();
        }

        public ClientVersion Version { get; }
        public string BasePath { get; }
        public bool IsUOPInstallation { get; private set; }

        public AnimationsLoader Animations { get; }
        public AnimDataLoader AnimData { get; }
        public ArtLoader Arts { get; }
        public MapLoader Maps { get; set; }
        public ClilocLoader Clilocs { get; }
        public GumpsLoader Gumps { get; }
        public FontsLoader Fonts { get; }
        public HuesLoader Hues { get; }
        public TileDataLoader TileData { get; }
        public MultiLoader Multis { get; }
        public SkillsLoader Skills { get; }
        public TexmapsLoader Texmaps { get; }
        public SpeechesLoader Speeches { get; }
        public LightsLoader Lights { get; }
        public SoundsLoader Sounds { get; }
        public MultiMapLoader MultiMaps { get; }
        public VerdataLoader Verdata { get; }
        public ProfessionLoader Professions { get; }
        public TileArtLoader TileArt { get; }
        public StringDictionaryLoader StringDictionary { get; }



        public void Dispose()
        {
            Animations.Dispose();
            AnimData.Dispose();
            Arts.Dispose();
            Maps.Dispose();
            Clilocs.Dispose();
            Gumps.Dispose();
            Fonts.Dispose();
            Hues.Dispose();
            TileData.Dispose();
            Multis.Dispose();
            Skills.Dispose();
            Texmaps.Dispose();
            Speeches.Dispose();
            Lights.Dispose();
            Sounds.Dispose();
            MultiMaps.Dispose();
            Verdata.Dispose();
            Professions.Dispose();
            TileArt.Dispose();
            StringDictionary.Dispose();
        }

        public string GetUOFilePath(string file)
        {
#if BROWSER_WASM
            // v0.7.9: every browser-side asset request goes through nginx
            // serving from a Linux fs. Both the writer side (operators
            // syncing .mul trees from Windows) and the reader side
            // (this method building paths from mixed-case literal strings
            // sprinkled across the loaders — "Anim1.def", "MainMisc.uop",
            // "Skills.idx", "Prof.txt", "Citytext.enu", etc.) historically
            // disagreed. The deploy pipeline standardises files lowercase
            // server-side, and the MEMFS /uo/ that Emscripten mounts is
            // case-sensitive — so we lowercase here to match. Mirrors the
            // CUO branch in source/cuo/src/ClassicUO.Assets/UOFileManager.cs.
            // The override-map keys are already lowercase (UOFilesOverrideMap.Load
            // normalises segments[0] to lower on insert), so pass `file`
            // straight through without re-lowercasing per call.
            file = file.ToLowerInvariant();
            if (!_overrideMap.TryGetValue(file, out string uoFilePath))
#else
            if (!_overrideMap.TryGetValue(file.ToLowerInvariant(), out string uoFilePath))
#endif
            {
                uoFilePath = Path.Combine(BasePath, file);
            }

#if !BROWSER_WASM
            //If the file with the given name doesn't exist, check for it with alternative casing if not on windows
            if (!PlatformHelper.IsWindows && !File.Exists(uoFilePath))
            {
                var finfo = new FileInfo(uoFilePath);
                string dir = Path.GetFullPath(finfo.DirectoryName ?? BasePath);

                if (Directory.Exists(dir))
                {
                    string[] files = Directory.GetFiles(dir);
                    int matches = 0;

                    foreach (string f in files)
                    {
                        if (string.Equals(f, uoFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            matches++;
                            uoFilePath = f;
                        }
                    }

                    if (matches > 1)
                    {
                        Log.Warn($"Multiple files with ambiguous case found for {file}, using {Path.GetFileName(uoFilePath)}. Check your data directory for duplicate files.");
                    }
                }
            }
#else
            // v0.7.9: On the WASM build the file system is fully under
            // our control (server-side asset-worker symlinks lowercase
            // & Cap-first to the canonical bytes; nginx mounts MEMFS
            // /uo/ accordingly). The case-insensitive Directory.GetFiles
            // fallback iterates THE ENTIRE GAMEFILE TREE for every
            // missing file (memento has ~91 files; 12 missing-anim
            // probes => 12 × N file string compares × Mercury MT
            // alloc overhead). In practice this turned a ~5ms operation
            // into an effective infinite hang during AnimationsLoader.Load.
            // Mirrors source/cuo/ — but CUO's wasm operators standardised
            // server-side on lowercase so the fallback simply never trips;
            // TUO needs the explicit short-circuit to avoid the Mercury
            // MT slowness in the loop body.
#endif

            return uoFilePath;
        }

        public void Load(bool useVerdata, string lang, string mapsLayouts = "")
        {
            var stopwatch = Stopwatch.StartNew();

            Log.Trace("[fm-debug] FM1: pre-_overrideMap.Load");
            _overrideMap.Load(); // need to load this first so that it manages can perform the file overrides if needed

            Log.Trace("[fm-debug] FM2: pre-IsUOPInstallation check");
            IsUOPInstallation = Version >= ClientVersion.CV_7000 && File.Exists(GetUOFilePath("MainMisc.uop"));
            Log.Trace($"[fm-debug] FM3: IsUOPInstallation={IsUOPInstallation}");

            Maps.MapsLayouts = mapsLayouts;

#if BROWSER_WASM
            // wasm: font loading stays SYNCHRONOUS. Upstream v5.3 moved TrueTypeLoader
            // onto a background Task; under Mercury MT that races the MEMFS/IDBFS mount
            // every other loader depends on. Empty array keeps the Task.WaitAll below valid.
            TrueTypeLoader.Instance.Load();
            Task[] asyncedLoading = [];
#else
            Task[] asyncedLoading = [Task.Factory.StartNew(TrueTypeLoader.Instance.Load)];
#endif

            Log.Trace("[fm-debug] FM4: pre-Animations.Load"); Animations.Load();
            Log.Trace("[fm-debug] FM5: pre-AnimData.Load"); AnimData.Load();
            Log.Trace("[fm-debug] FM6: pre-Arts.Load"); Arts.Load();
            Log.Trace("[fm-debug] FM7: pre-Maps.Load"); Maps.Load();
            Log.Trace("[fm-debug] FM8: pre-Clilocs.Load"); Clilocs.Load(lang);
            Log.Trace("[fm-debug] FM9: pre-Gumps.Load"); Gumps.Load();
            Log.Trace("[fm-debug] FM10: pre-Fonts.Load"); Fonts.Load();
            Log.Trace("[fm-debug] FM11: pre-Hues.Load"); Hues.Load();
            Log.Trace("[fm-debug] FM12: pre-TileData.Load"); TileData.Load();
            Log.Trace("[fm-debug] FM13: pre-Multis.Load"); Multis.Load();
            Log.Trace("[fm-debug] FM14: pre-Skills.Load"); Skills.Load();
            Log.Trace("[fm-debug] FM15: pre-Professions.Load"); Professions.Load();
            Log.Trace("[fm-debug] FM16: pre-Texmaps.Load"); Texmaps.Load();
            Log.Trace("[fm-debug] FM17: pre-Speeches.Load"); Speeches.Load();
            Log.Trace("[fm-debug] FM18: pre-Lights.Load"); Lights.Load();
            Log.Trace("[fm-debug] FM19: pre-Sounds.Load"); Sounds.Load();
            Log.Trace("[fm-debug] FM20: pre-MultiMaps.Load"); MultiMaps.Load();
            Log.Trace("[fm-debug] FM21: pre-TileArt.Load"); TileArt.Load();
            Log.Trace("[fm-debug] FM22: pre-StringDictionary.Load"); StringDictionary.Load();

            Log.Trace("[fm-debug] FM23: pre-PNGLoader.Instance.Load"); PNGLoader.Instance.Load(BasePath);
            Log.Trace("[fm-debug] FM24: pre-TrueTypeLoader.Instance.Load"); TrueTypeLoader.Instance.Load();
            Log.Trace("[fm-debug] FM25: all loaders done");

            ReadArtDefFile();

            UOFileMul verdata = Verdata.File;
            bool forceVerdata = Version < ClientVersion.CV_500A || verdata != null && verdata.Length != 0 && Verdata.Patches.Length != 0;

            if (!useVerdata && forceVerdata) useVerdata = true;

            Log.Trace($"Use verdata.mul: {(useVerdata ? "Yes" : "No")}");

            if (useVerdata)
            {
                if (verdata != null && Verdata.Patches.Length != 0)
                {
                    Log.Info(">> PATCHING WITH VERDATA.MUL");

                    byte[] buf = new byte[256];
                    Span<VerdataHuesGroup> group = stackalloc VerdataHuesGroup[1];

                    for (int i = 0; i < Verdata.Patches.Length; i++)
                    {
                        ref UOFileIndex5D vh = ref Verdata.Patches[i];
                        Log.Info($">>> patching  FileID: {vh.FileID}  -  BlockID: {vh.BlockID}");

                        if (vh.FileID == 0)
                        {
                            Maps.PatchMapBlock(verdata, vh.BlockID, vh.Position);
                        }
                        else if (vh.FileID == 2)
                        {
                            Maps.PatchStaticBlock(verdata, vh.BlockID, vh.Position, vh.Length);
                        }
                        else if (vh.FileID == 4)
                        {
                            if (vh.BlockID < Arts.File.Entries.Length)
                            {
                                Arts.File.Entries[vh.BlockID] = new UOFileIndex
                                (
                                    verdata,
                                    vh.Position,
                                    (int)vh.Length,
                                    0
                                );
                            }
                        }
                        else if (vh.FileID == 12)
                        {
                            Gumps.File.Entries[vh.BlockID] = new UOFileIndex
                            (
                                verdata,
                                vh.Position,
                                (int)vh.Length,
                                0,
                                0,
                                (short)(vh.GumpData >> 16),
                                (short)(vh.GumpData & 0xFFFF)
                            );
                        }
                        else if (vh.FileID == 14 && vh.BlockID < Multis.File.Entries.Length)
                        {
                            Multis.File.Entries[vh.BlockID] = new UOFileIndex
                            (
                                verdata,
                                vh.Position,
                                (int)vh.Length,
                                0
                            );
                        }
                        else if (vh.FileID == 16 && vh.BlockID < Skills.SkillsCount)
                        {
                            SkillEntry skill = Skills.Skills[(int)vh.BlockID];

                            if (skill != null)
                            {
                                skill.HasAction = verdata.ReadUInt8() != 0;
                                if (buf.Length < vh.Length)
                                    buf = new byte[vh.Length];

                                skill.Name = Encoding.ASCII.GetString(buf.AsSpan(0, (int)(vh.Length - 1)));
                            }
                        }
                        else if (vh.FileID == 30)
                        {
                            verdata.Seek(vh.Position, SeekOrigin.Begin);

                            if (vh.Length == 836)
                            {
                                int offset = (int)(vh.BlockID * 32);

                                if (offset + 32 > TileData.LandData.Length)
                                {
                                    continue;
                                }

                                verdata.ReadUInt32();

                                for (int j = 0; j < 32; j++)
                                {
                                    ulong flags;

                                    if (Version < ClientVersion.CV_7090)
                                    {
                                        flags = verdata.ReadUInt32();
                                    }
                                    else
                                    {
                                        flags = verdata.ReadUInt64();
                                    }

                                    ushort textId = verdata.ReadUInt16();
                                    string str = Encoding.ASCII.GetString(buf.AsSpan(0, 20));
                                    TileData.LandData[offset + j] = new LandTiles(flags, textId, str);
                                }
                            }
                            else if (vh.Length == 1188)
                            {
                                int offset = (int)((vh.BlockID - 0x0200) * 32);

                                if (offset + 32 > TileData.StaticData.Length)
                                {
                                    continue;
                                }

                                verdata.ReadUInt32();

                                for (int j = 0; j < 32; j++)
                                {
                                    ulong flags;

                                    if (Version < ClientVersion.CV_7090)
                                    {
                                        flags = verdata.ReadUInt32();
                                    }
                                    else
                                    {
                                        flags = verdata.ReadUInt64();
                                    }

                                    byte weight = verdata.ReadUInt8();
                                    byte layer = verdata.ReadUInt8();
                                    int count = verdata.ReadInt32();
                                    ushort animId = verdata.ReadUInt16();
                                    ushort hue = verdata.ReadUInt16();
                                    ushort lightIdx = verdata.ReadUInt16();
                                    byte height = verdata.ReadUInt8();
                                    string str = Encoding.ASCII.GetString(buf.AsSpan(0, 20));

                                    TileData.StaticData[offset + j] = new StaticTiles
                                    (
                                        flags,
                                        weight,
                                        layer,
                                        count,
                                        animId,
                                        hue,
                                        lightIdx,
                                        height,
                                        str
                                    );
                                }
                            }
                        }
                        else if (vh.FileID == 32)
                        {
                            if (vh.BlockID < Hues.HuesCount)
                            {
                                verdata.Seek(vh.Position, SeekOrigin.Begin);
                                verdata.Read(MemoryMarshal.AsBytes(group));

                                HuesGroup[] hues = Hues.HuesRange;
                                hues[vh.BlockID].Header = group[0].Header;

                                for (int j = 0; j < 8; j++)
                                {
                                    hues[vh.BlockID].Entries[j].ColorTable = group[0].Entries[j].ColorTable;
                                }
                            }
                        }
                        else if (vh.FileID != 5 && vh.FileID != 6)
                        {
                            Log.Warn($"Unused verdata block\tFileID: {vh.FileID}\tBlockID: {vh.BlockID}");
                        }
                    }

                    Log.Info("<< PATCHED.");
                }
            }

            Task.WaitAll(asyncedLoading);

            stopwatch.Stop();
            Log.Trace($"Files loaded in: {stopwatch.ElapsedMilliseconds} ms!");
        }

        private void ReadArtDefFile()
        {
            string pathdef = GetUOFilePath("art.def");

            if (!File.Exists(pathdef))
            {
                return;
            }

            using (var reader = new DefReader(pathdef, 1))
            {
                while (reader.Next())
                {
                    int index = reader.ReadInt();

                    if (index < 0 || index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + TileData.StaticData.Length)
                    {
                        continue;
                    }

                    int[] group = reader.ReadGroup();

                    if (group == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < group.Length; i++)
                    {
                        int checkIndex = group[i];

                        if (checkIndex < 0 || checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT + TileData.StaticData.Length)
                        {
                            continue;
                        }

                        if (index < Arts.File.Entries.Length && checkIndex < Arts.File.Entries.Length)
                        {
                            ref UOFileIndex currentEntry = ref Arts.File.GetValidRefEntry(index);
                            ref UOFileIndex checkEntry = ref Arts.File.GetValidRefEntry(checkIndex);

                            if (currentEntry.Equals(UOFileIndex.Invalid) && !checkEntry.Equals(UOFileIndex.Invalid))
                            {
                                Arts.File.Entries[index] = Arts.File.Entries[checkIndex];
                            }
                        }

                        if (index < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            checkIndex < ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            checkIndex < TileData.LandData.Length &&
                            index < TileData.LandData.Length &&
                            !TileData.LandData[checkIndex].Equals(default) &&
                            TileData.LandData[index].Equals(default))
                        {
                            TileData.LandData[index] = TileData.LandData[checkIndex];

                            break;
                        }

                        if (index >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT && checkIndex >= ArtLoader.MAX_LAND_DATA_INDEX_COUNT &&
                            index < TileData.StaticData.Length && checkIndex < TileData.StaticData.Length &&
                            TileData.StaticData[index].Equals(default) && !TileData.StaticData[checkIndex].Equals(default))
                        {
                            TileData.StaticData[index] = TileData.StaticData[checkIndex];

                            break;
                        }
                    }
                }
            }
        }
    }
}
