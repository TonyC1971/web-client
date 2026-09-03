// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Audio;
using MP3Sharp;
using System;
#if BROWSER_WASM
using System.IO;
#endif

namespace ClassicUO.IO.Audio
{
    public class UOMusic : Sound
    {
        private const int NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK = 0x8000; // 32768 bytes, about 0.9 seconds
        private bool m_Playing;
        private readonly bool m_Repeat;
        private MP3Stream m_Stream;
        private readonly byte[] m_WaveBuffer = new byte[NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK];
#if BROWSER_WASM
        private byte[] m_Mp3FileBytes;
#endif


        public UOMusic(int index, string name, bool loop, string fileName) : base(name, index)
        {
            m_Repeat = loop;
            m_Playing = false;
            Channels = AudioChannels.Stereo;
            Delay = 0;

            Path = fileName;
        }

        private string Path { get; }

#if BROWSER_WASM
        // Tell Sound.Play's wasm branch to route this payload through
        // decodeAudioData + loop instead of raw PCM submission.
        protected override bool WasmIsCompressed => true;
        protected override bool WasmLoop => m_Repeat;

        // Route via URL instead of in-memory bytes — the MP3 files
        // aren't pre-mounted into MEMFS (they'd bloat the first-
        // visit download by ~200 MB). The `gamefiles/` junction
        // serves the game/ root via HTTP; stripping everything up
        // to and including `/Music/` from the absolute UO path and
        // prepending `gamefiles/Music/` reconstructs a fetchable
        // URL. Case-insensitive because FileManager.GetUOFilePath
        // may return different casings depending on the OS / path
        // walk.
        protected override string WasmUrl
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return null;
                int idx = Path.IndexOf("/Music/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    // Also try '\\Music\\' for Windows-style paths.
                    idx = Path.IndexOf("\\Music\\", StringComparison.OrdinalIgnoreCase);
                }
                if (idx < 0) return null;
                // idx points at the `/` or `\` before Music. Take
                // everything from Music onward + normalise slashes.
                // v0.3.19: lowercase the URL path so deploys can keep a
                // single canonical lowercase tree on disk. The "/Music/"
                // substring + the track filename were Cap-first per
                // UOR-era authoring; lowercasing here lets nginx serve
                // /server-N/music/digital/stones.mp3 directly (no Cap-
                // first symlink). Same rule as UOFileManager.GetUOFilePath.
                return ("gamefiles" + Path.Substring(idx).Replace('\\', '/')).ToLowerInvariant();
            }
        }
#endif

        public void Update()
        {
#if BROWSER_WASM
            // Web Audio handles looping natively on the JS side
            // (AudioBufferSourceNode.loop=true). No chunk-streaming
            // required — the whole MP3 is already decoded into a
            // single AudioBuffer. Update is a no-op.
            return;
#else
            // sanity - if the buffer empties, we will lose our sound effect. Thus we must continually check if it is dead.
            OnBufferNeeded(null, null);
#endif
        }

        protected override ArraySegment<byte> GetBuffer()
        {
#if BROWSER_WASM
            // Wasm path: hand the whole MP3 file bytes to JS, which
            // decodes + loops via Web Audio. The base Sound.Play
            // invokes GetBuffer ONCE and calls wasm_play_music with
            // the returned byte segment; no MP3Sharp PCM streaming.
            if (m_Mp3FileBytes != null && m_Mp3FileBytes.Length > 0)
            {
                return new ArraySegment<byte>(m_Mp3FileBytes);
            }
            return ArraySegment<byte>.Empty;
#else
            try
            {
                if (m_Playing && SoundInstance != null)
                {
                    int bytesReturned = m_Stream.Read(m_WaveBuffer, 0, m_WaveBuffer.Length);

                    if (bytesReturned != NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK)
                    {
                        if (m_Repeat)
                        {
                            m_Stream.Position = 0;
                            m_Stream.Read(m_WaveBuffer, bytesReturned, m_WaveBuffer.Length - bytesReturned);
                        }
                        else
                        {
                            if (bytesReturned == 0)
                            {
                                Stop();
                            }
                        }
                    }

                    return new ArraySegment<byte>(m_WaveBuffer, 0, bytesReturned);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }

            Stop();

            return ArraySegment<byte>.Empty;
#endif
        }

        protected override void OnBufferNeeded(object sender, EventArgs e)
        {
#if BROWSER_WASM
            // Not reached — Web Audio loops natively.
            return;
#else
            if (m_Playing)
            {
                if (SoundInstance == null)
                {
                    Stop();

                    return;
                }

                while (SoundInstance.PendingBufferCount < 3)
                {
                    var buffer = GetBuffer();

                    if (SoundInstance.IsDisposed || buffer.Count == 0)
                    {
                        break;
                    }

                    SoundInstance.SubmitBuffer(buffer.Array, buffer.Offset, buffer.Count);
                }
            }
#endif
        }

        protected override void BeforePlay()
        {
            if (m_Playing)
            {
                Stop();
            }

#if BROWSER_WASM
            // Sound.Play's wasm branch sees WasmUrl != null and
            // routes to wasm_play_music_url; no local MP3 decode
            // or File.ReadAllBytes. Mark playing so GetBuffer isn't
            // called (though even if it is, WasmUrl != null wins
            // in Sound.Play before GetBuffer runs).
            m_Playing = true;
#else
            try
            {
                if (m_Stream != null)
                {
                    m_Stream.Close();
                    m_Stream = null;
                }

                m_Stream = new MP3Stream(Path, NUMBER_OF_PCM_BYTES_TO_READ_PER_CHUNK);
                Frequency = m_Stream.Frequency;

                m_Playing = true;
            }
            catch
            {
                // file in use or access denied.
                m_Playing = false;
            }
#endif
        }

        protected override void AfterStop()
        {
            if (m_Playing)
            {
                m_Playing = false;
#if BROWSER_WASM
                m_Mp3FileBytes = null;
#else
                m_Stream?.Close();
                m_Stream = null;
#endif
            }
        }
    }
}
