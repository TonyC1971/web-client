using ClassicUO.Assets;
using ClassicUO.IO.Audio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClassicUO.Renderer.Sounds
{
    public sealed class Sound
    {
        const int MAX_SOUND_DATA_INDEX_COUNT = 0xFFFF;

        private readonly IO.Audio.Sound[] _musics = new IO.Audio.Sound[MAX_SOUND_DATA_INDEX_COUNT];
        private readonly IO.Audio.Sound[] _sounds = new IO.Audio.Sound[MAX_SOUND_DATA_INDEX_COUNT];
        private readonly bool _useDigitalMusicFolder;
        private readonly SoundsLoader _soundsLoader;

        public Sound(SoundsLoader soundsLoader)
        {
            _soundsLoader = soundsLoader;
#if BROWSER_WASM
            // v0.3.32: under WASM the music files live in the nginx-served
            // gamefile tree, NOT in IDBFS. Only .mul / .uop / .def / .txt
            // assets the manifest enumerates get mounted into IDBFS upfront;
            // the multi-megabyte .mp3 tree is fetched on-demand via HTTP.
            // That meant v0.3.31's Directory.Exists case-insensitive probe
            // had nothing to discover (neither "Music/Digital" nor
            // "music/digital" exists in IDBFS) → _useDigitalMusicFolder=false
            // → URL became /server-N/music/<song>.mp3 instead of
            // /server-N/music/digital/<song>.mp3 → every track 404 in prod.
            // The shipped gamefile pack always uses the AOS-era
            // Music/Digital layout (verified on <share> NAS 2026-05-05),
            // so hardcode the flag on WASM. No probing, no 404 chain.
            _useDigitalMusicFolder = true;
#else
            // Desktop probe (case-insensitive FS on Win/macOS, exact match
            // on Linux). Kept for desktop ClassicUO builds that re-use
            // this assembly outside the wasm webclient.
            string basePath = soundsLoader.FileManager.BasePath;
            _useDigitalMusicFolder =
                Directory.Exists(Path.Combine(basePath, "Music", "Digital")) ||
                Directory.Exists(Path.Combine(basePath, "music", "digital"));
#endif
        }

        public IO.Audio.Sound GetSound(int index)
        {
            if (index >= 0 && index < MAX_SOUND_DATA_INDEX_COUNT)
            {
                ref IO.Audio.Sound sound = ref _sounds[index];

                if (sound == null && _soundsLoader.TryGetSound(index, out byte[] data, out string name))
                {
                    sound = new UOSound(name, index, data);
                }

                return sound;
            }

            return null;
        }

        public IO.Audio.Sound GetMusic(int index)
        {
            if (index >= 0 && index < MAX_SOUND_DATA_INDEX_COUNT)
            {
                ref IO.Audio.Sound music = ref _musics[index];

                if (music == null && _soundsLoader.TryGetMusicData(index, out string name, out bool loop))
                {
                    var path = _useDigitalMusicFolder ? $"Music/Digital/{name}" : $"Music/{name}";
                    if (!path.EndsWith(".mp3", StringComparison.InvariantCultureIgnoreCase))
                    {
                        path += ".mp3";
                    }

                    music = new UOMusic(index, name, loop, _soundsLoader.FileManager.GetUOFilePath(path));
                }

                return music;
            }

            return null;
        }
    }
}
