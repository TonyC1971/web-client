// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public sealed class SoundsLoader : UOFileLoader
    {
        private static readonly char[] _configFileDelimiters = { ' ', ',', '\t' };
        private static readonly Dictionary<int, (string, bool)> _musicData = new Dictionary<int, (string, bool)>();


        public const int MAX_SOUND_DATA_INDEX_COUNT = 0xFFFF;

        private UOFile _file;

        public SoundsLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public override void Load()
        {
            string path = FileManager.GetUOFilePath("soundLegacyMUL.uop");

            if (FileManager.IsUOPInstallation && File.Exists(path))
            {
                _file = new UOFileUop(path, "build/soundlegacymul/{0:D8}.dat");
            }
            else
            {
                path = FileManager.GetUOFilePath("sound.mul");
                string idxpath = FileManager.GetUOFilePath("soundidx.mul");

                if (File.Exists(path) && File.Exists(idxpath))
                {
                    _file = new UOFileMul(path, idxpath);
                }
                else
                {
                    throw new FileNotFoundException("no sounds found");
                }
            }

            _file.FillEntries();

            string def = FileManager.GetUOFilePath("Sound.def");

            if (File.Exists(def))
            {
                using (DefReader reader = new DefReader(def))
                {
                    while (reader.Next())
                    {
                        int index = reader.ReadInt();

                        if (index < 0 || index >= MAX_SOUND_DATA_INDEX_COUNT || index >= _file.Entries.Length || _file.Entries[index].Length > 0)
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

                            if (checkIndex < -1 || checkIndex >= MAX_SOUND_DATA_INDEX_COUNT)
                            {
                                continue;
                            }

                            ref UOFileIndex ind = ref _file.Entries[index];

                            if (checkIndex == -1)
                            {
                                ind = default;
                            }
                            else
                            {
                                ref readonly UOFileIndex outInd = ref _file.Entries[checkIndex];

                                if (outInd.Length == 0)
                                {
                                    continue;
                                }

                                _file.Entries[index] = _file.Entries[checkIndex];
                            }
                        }
                    }
                }
            }

            path = FileManager.GetUOFilePath(FileManager.Version >= ClientVersion.CV_4011C ?  @"Music/Digital/Config.txt" : @"Music/Config.txt");

            // v0.3.33: skip the File.Exists guard. Mono WASM's File.Exists on
            // IDBFS-mounted paths is unreliable (same bug pattern we already
            // fixed in Sound.cs Directory.Exists for the digital folder probe).
            // The manifest mounts music/digital/config.txt into /uo/ and
            // StreamReader CAN open it, but File.Exists returned false → the
            // hardcoded fallback table below kicked in → URLs resolved canonical
            // names like "minoc.mp3" instead of the descriptive "minocpos.mp3"
            // shipped in the operator's UOR pack → 404 chain. Try-catch the
            // StreamReader directly: success means Config.txt loaded; any IO
            // failure (FileNotFoundException, DirectoryNotFoundException,
            // IOException) silently falls into the legacy hardcoded table.
            bool configLoaded = false;
            int parsedLines = 0;
            string failureType = null;
            string failureMessage = null;
            try
            {
                using (var reader = new StreamReader(path))
                {
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            if (TryParseConfigLine(line, out (int index, string name, bool loop) data))
                            {
                                _musicData[data.index] = (data.name, data.loop);
                                configLoaded = true;
                                parsedLines++;
                            }
                        }
                        catch (Exception lineEx)
                        {
                            // v0.3.34: per-line catch — operator's UOR pack
                            // had Config.txt entries that didn't fit the
                            // (index name [,loop]) shape, FormatException
                            // bubbled out of TryParseConfigLine.int.Parse
                            // and aborted the entire config load → fallback
                            // table → wrong filenames → 404. Per-line catch
                            // means one bad row no longer poisons the rest.
                            failureType = lineEx.GetType().Name;
                            failureMessage = lineEx.Message;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // v0.3.34: catch-all so Mono-WASM-IDBFS exceptions outside
                // the FileNotFoundException / DirectoryNotFoundException /
                // IOException trio (e.g. UnauthorizedAccessException,
                // ArgumentException, PlatformNotSupportedException) also
                // fall into the hardcoded table cleanly instead of crashing
                // the loader. Plus diagnostic Console.Error so operators
                // running ?dev=1 can see WHICH exception type is blocking
                // Config.txt parsing on their build.
                failureType = ex.GetType().Name;
                failureMessage = ex.Message;
            }

#if BROWSER_WASM
            // v0.4.40: Console.WriteLine (= console.log on JS side) instead
            // of Console.Error.WriteLine. Boot diagnostic, not an error;
            // tagging [error] in the silencer wrapper polluted external
            // monitoring. console.log shows the message untagged at
            // ?dev=1 and is silenced in production via the no-?dev path.
            Console.WriteLine(
                $"[music-config] path='{path}' parsedLines={parsedLines} configLoaded={configLoaded}" +
                (failureType != null ? $" failure={failureType}: {failureMessage}" : ""));
#endif

            if (!configLoaded)
            {
                _musicData.Add(0, ("oldult01", true));
                _musicData.Add(1, ("create1", false));
                _musicData.Add(2, ("dragflit", false));
                _musicData.Add(3, ("oldult02", true));
                _musicData.Add(4, ("oldult03", true));
                _musicData.Add(5, ("oldult04", true));
                _musicData.Add(6, ("oldult05", true));
                _musicData.Add(7, ("oldult06", true));
                _musicData.Add(8, ("stones2", true));
                _musicData.Add(9, ("britain1", true));
                _musicData.Add(10, ("britain2", true));
                _musicData.Add(11, ("bucsden", true));
                _musicData.Add(12, ("jhelom", false));
                _musicData.Add(13, ("lbcastle", false));
                _musicData.Add(14, ("linelle", false));
                _musicData.Add(15, ("magincia", true));
                _musicData.Add(16, ("minoc", true));
                _musicData.Add(17, ("ocllo", true));
                _musicData.Add(18, ("samlethe", false));
                _musicData.Add(19, ("serpents", true));
                _musicData.Add(20, ("skarabra", true));
                _musicData.Add(21, ("trinsic", true));
                _musicData.Add(22, ("vesper", true));
                _musicData.Add(23, ("wind", true));
                _musicData.Add(24, ("yew", true));
                _musicData.Add(25, ("cave01", false));
                _musicData.Add(26, ("dungeon9", false));
                _musicData.Add(27, ("forest_a", false));
                _musicData.Add(28, ("intown01", false));
                _musicData.Add(29, ("jungle_a", false));
                _musicData.Add(30, ("mountn_a", false));
                _musicData.Add(31, ("plains_a", false));
                _musicData.Add(32, ("sailing", false));
                _musicData.Add(33, ("swamp_a", false));
                _musicData.Add(34, ("tavern01", false));
                _musicData.Add(35, ("tavern02", false));
                _musicData.Add(36, ("tavern03", false));
                _musicData.Add(37, ("tavern04", false));
                _musicData.Add(38, ("combat1", false));
                _musicData.Add(39, ("combat2", false));
                _musicData.Add(40, ("combat3", false));
                _musicData.Add(41, ("approach", false));
                _musicData.Add(42, ("death", false));
                _musicData.Add(43, ("victory", false));
                _musicData.Add(44, ("btcastle", false));
                _musicData.Add(45, ("nujelm", true));
                _musicData.Add(46, ("dungeon2", false));
                _musicData.Add(47, ("cove", true));
                _musicData.Add(48, ("moonglow", true));
                _musicData.Add(49, ("zento", true));
                _musicData.Add(50, ("tokunodungeon", true));
                _musicData.Add(51, ("Taiko", true));
                _musicData.Add(52, ("dreadhornarea", true));
                _musicData.Add(53, ("elfcity", true));
                _musicData.Add(54, ("grizzledungeon", true));
                _musicData.Add(55, ("melisandeslair", true));
                _musicData.Add(56, ("paroxysmuslair", true));
                _musicData.Add(57, ("gwennoconversation", true));
                _musicData.Add(58, ("goodendgame", true));
                _musicData.Add(59, ("goodvsevil", true));
                _musicData.Add(60, ("greatearthserpents", true));
                _musicData.Add(61, ("humanoids_u9", true));
                _musicData.Add(62, ("minocnegative", true));
                _musicData.Add(63, ("paws", true));
                _musicData.Add(64, ("selimsbar", true));
                _musicData.Add(65, ("serpentislecombat_u7", true));
                _musicData.Add(66, ("valoriaships", true));
            }
        }

        public unsafe bool TryGetSound(int sound, out byte[] data, out string name)
        {
            data = null;
            name = null;

            if (sound < 0)
            {
                return false;
            }

            ref var entry = ref _file.GetValidRefEntry(sound);
            if (entry.Length <= 0)
                return false;

            _file.Seek(entry.Offset, SeekOrigin.Begin);

            Span<byte> buf = stackalloc byte[40];
            _file.Read(buf);

            name = Encoding.UTF8.GetString(buf);
            data = new byte[entry.Length - 40];
            _file.Read(data);

            return true;
        }

        /// <summary>
        ///     Attempts to parse a line from UO's music Config.txt.
        /// </summary>
        /// <param name="line">A line from the file.</param>
        /// <param name="?">If successful, contains a tuple with these fields: int songIndex, string songName, bool doesLoop</param>
        /// <returns>true if line could be parsed, false otherwise.</returns>
        private bool TryParseConfigLine(string line, out (int index, string name, bool loop) data)
        {
            data = default;

            string[] splits = line.Split(_configFileDelimiters);

            if (splits.Length < 2 || splits.Length > 3)
            {
                return false;
            }

            data.index = int.Parse(splits[0]);
            // check if name exists as file, ignoring case since UO isn't consistent with file case (necessary for *nix)
            // also, not every filename in Config.txt has a file extension, so let's strip it out just in case.
            data.name = GetTrueFileName(Path.GetFileNameWithoutExtension(splits[1]));
            data.loop = splits.Length == 3 && splits[2] == "loop";

            return true;
        }

        /// <summary>
        ///     Returns true filename from name, ignoring case since UO isn't consistent with file case (necessary for *nix)
        /// </summary>
        /// <param name="name">The filename from the music Config.txt</param>
        /// <returns>a string with the true case sensitive filename</returns>
        private string GetTrueFileName(string name)
        {
#if BROWSER_WASM
            // v0.3.33: under WASM, GetTrueFileName has no useful work to do.
            // Music .mp3 files live in the nginx-served gamefile tree and are
            // NOT mounted into IDBFS. Directory.GetFiles on the lazy IDBFS
            // mount either returns empty (best case) or throws
            // DirectoryNotFoundException (worst case, escaping all the way up
            // through TryParseConfigLine + the outer try-catch in Load → the
            // hardcoded fallback table kicks in → wrong filenames). The
            // actual case-folding for music URLs happens in
            // UOFileManager.GetUOFilePath (which lowercases the entire URL
            // path under BROWSER_WASM), so the case-disambiguation logic
            // below is also redundant. Return the Config.txt name verbatim.
            return name;
#else
            // don't worry about subdirectories, we'll recursively search them all
            string dir = FileManager.BasePath + $"/Music";

            // Enumerate all files in the directory, using the file name as a pattern
            // This will list all case variants of the filename even on file systems that
            // are case sensitive.
            Regex  pattern = new Regex($"^{name}.mp3", RegexOptions.IgnoreCase);
            //string[] fileList = Directory.GetFiles(dir, "*.mp3", SearchOption.AllDirectories).Where(path => pattern.IsMatch(Path.GetFileName(path))).ToArray();
            string[] fileList = Directory.GetFiles(dir, "*.mp3", SearchOption.AllDirectories);
            fileList = Array.FindAll(fileList, path => pattern.IsMatch(Path.GetFileName(path)));

            if (fileList != null && fileList.Length != 0)
            {
                if (fileList.Length > 1)
                {
                    // More than one file with the same name but different case spelling found
                    Log.Warn($"Ambiguous File reference for {name}. More than one file found with different spellings.");
                }

                Log.Debug($"Loading music:\t\t{fileList[0]}");

                return Path.GetFileName(fileList[0]);
            }

            // If we've made it this far, there is no file with that name, regardless of case spelling
            // return name and GetMusic will fail gracefully (play nothing)
            Log.Warn($"No File found known as {name}");
            return name;
#endif
        }

        public bool TryGetMusicData(int index, out string name, out bool doesLoop)
        {
            name = null;
            doesLoop = false;

            if (_musicData.ContainsKey(index))
            {
                name = _musicData[index].Item1;

                doesLoop = _musicData[index].Item2;

                return true;
            }

            return false;
        }

        public override void ClearResources()
        {
            _musicData.Clear();
        }
    }
}
