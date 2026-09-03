// SPDX-License-Identifier: BSD-2-Clause

using Microsoft.Xna.Framework.Audio;
using System;
#if BROWSER_WASM
using System.Runtime.InteropServices;
#endif
using static System.String;

namespace ClassicUO.IO.Audio
{
    public abstract class Sound : IComparable<Sound>, IDisposable
    {
        private uint _lastPlayedTime;
        private string m_Name;
        private float m_volume = 1.0f;
        private float m_volumeFactor;

#if BROWSER_WASM
        // FAudio not linked — route through Web Audio API bridge
        // (main.js __wasm_play_pcm / __wasm_play_music / etc. + the
        // EM_ASM shims at the bottom of source/webclient/native-shims/SDL3.c).
        // _wasmHandle > 0 means JS holds an active AudioBufferSource
        // for us; 0 means failed / stopped.
        [DllImport("SDL3", EntryPoint = "wasm_play_pcm", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int wasm_play_pcm(byte* data, int len, float volume,
                                                       int sampleRate, int channels, int loop);
        [DllImport("SDL3", EntryPoint = "wasm_play_music", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int wasm_play_music(byte* data, int len, float volume, int loop);
        [DllImport("SDL3", EntryPoint = "wasm_play_music_url", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi)]
        private static extern int wasm_play_music_url([MarshalAs(UnmanagedType.LPUTF8Str)] string url, float volume, int loop);
        [DllImport("SDL3", EntryPoint = "wasm_stop_sound", CallingConvention = CallingConvention.Cdecl)]
        private static extern void wasm_stop_sound(int handle);
        [DllImport("SDL3", EntryPoint = "wasm_set_sound_volume", CallingConvention = CallingConvention.Cdecl)]
        private static extern void wasm_set_sound_volume(int handle, float volume);

        protected int _wasmHandle;
#endif

        protected Sound(string name, int index)
        {
            Name = name;
            Index = index;
        }

        public string Name
        {
            get => m_Name;
            private set
            {
                if (!IsNullOrEmpty(value))
                {
                    m_Name = value.Replace(".mp3", "");
                }
                else
                {
                    m_Name = Empty;
                }
            }
        }

        public int Index { get; }
        public double DurationTime { get; private set; }

        public float Volume
        {
            get => m_volume;
            set
            {
                if (value < 0.0f)
                {
                    value = 0f;
                }
                else if (value > 1f)
                {
                    value = 1f;
                }

                m_volume = value;

                float instanceVolume = Math.Max(value - VolumeFactor, 0.0f);

#if BROWSER_WASM
                if (_wasmHandle > 0)
                {
                    wasm_set_sound_volume(_wasmHandle, instanceVolume);
                }
#else
                if (SoundInstance != null && !SoundInstance.IsDisposed)
                {
                    SoundInstance.Volume = instanceVolume;
                }
#endif
            }
        }

        public float VolumeFactor
        {
            get => m_volumeFactor;
            set
            {
                m_volumeFactor = value;
                Volume = m_volume;
            }
        }

#if BROWSER_WASM
        public bool IsPlaying(uint curTime) => _wasmHandle > 0 && DurationTime > curTime;
#else
        public bool IsPlaying(uint curTime) => SoundInstance != null && SoundInstance.State == SoundState.Playing && DurationTime > curTime;
#endif

        public int CompareTo(Sound other)
        {
            return other == null ? -1 : Index.CompareTo(other.Index);
        }

        public void Dispose()
        {
#if BROWSER_WASM
            if (_wasmHandle > 0)
            {
                wasm_stop_sound(_wasmHandle);
                _wasmHandle = 0;
            }
#else
            if (SoundInstance != null)
            {
                SoundInstance.BufferNeeded -= OnBufferNeeded;

                if (!SoundInstance.IsDisposed)
                {
                    SoundInstance.Stop();
                    SoundInstance.Dispose();
                }

                SoundInstance = null;
            }
#endif
        }

        protected DynamicSoundEffectInstance SoundInstance;
        protected AudioChannels Channels = AudioChannels.Mono;
        protected uint Delay = 250;

        protected int Frequency = 22050;

        protected abstract ArraySegment<byte> GetBuffer();
        protected abstract void OnBufferNeeded(object sender, EventArgs e);

        protected virtual void AfterStop()
        {
        }

        protected virtual void BeforePlay()
        {
        }

#if BROWSER_WASM
        // Subclasses override to tell the wasm audio path whether to
        // use the raw-PCM route (UOSound: 16-bit signed mono at 22050
        // Hz, already a valid AudioBuffer payload) or the
        // decode-audio-data route (UOMusic: compressed MP3 file
        // bytes).
        protected virtual bool WasmIsCompressed => false;
        protected virtual bool WasmLoop => false;

        // If non-null, Sound.Play routes to wasm_play_music_url with
        // this URL instead of reading GetBuffer(). Used by UOMusic —
        // the MP3 files aren't pre-mounted into MEMFS so we have to
        // fetch them via HTTP from the `gamefiles/` junction.
        protected virtual string WasmUrl => null;
#endif

        /// <summary>
        ///     Plays the effect.
        /// </summary>
        /// <param name="asEffect">Set to false for music, true for sound effects.</param>
        public bool Play(uint curTime, float volume = 1.0f, float volumeFactor = 0.0f, bool spamCheck = false)
        {
            if (_lastPlayedTime > curTime)
            {
                return false;
            }

            BeforePlay();

#if BROWSER_WASM
            // Stop any previous play of this Sound instance, then
            // route to the wasm audio bridge (main.js __wasm_play_*).
            if (_wasmHandle > 0)
            {
                wasm_stop_sound(_wasmHandle);
                _wasmHandle = 0;
            }

            _lastPlayedTime = curTime + Delay;
            m_volumeFactor = volumeFactor;
            m_volume = volume;
            float instanceVolume = Math.Max(volume - volumeFactor, 0.0f);

            // URL route — UOMusic's MP3 fetched from the gamefiles/
            // junction via HTTP. Bypasses GetBuffer entirely.
            var url = WasmUrl;
            if (url != null)
            {
                _wasmHandle = wasm_play_music_url(url, instanceVolume, WasmLoop ? 1 : 0);
                WasmTrace.W($"[audio] wasm_play_music_url name={Name} url={url} vol={instanceVolume} loop={WasmLoop} -> handle={_wasmHandle}");
                if (_wasmHandle == 0) return false;
                DurationTime = curTime + 10 * 60 * 1000; // placeholder
                return true;
            }

            var buffer = GetBuffer();
            if (buffer.Count <= 0)
            {
                return false;
            }

            unsafe
            {
                fixed (byte* ptr = &buffer.Array[buffer.Offset])
                {
                    if (WasmIsCompressed)
                    {
                        _wasmHandle = wasm_play_music(ptr, buffer.Count, instanceVolume, WasmLoop ? 1 : 0);
                        WasmTrace.W($"[audio] wasm_play_music name={Name} len={buffer.Count} vol={instanceVolume} loop={WasmLoop} -> handle={_wasmHandle}");
                    }
                    else
                    {
                        _wasmHandle = wasm_play_pcm(ptr, buffer.Count, instanceVolume,
                                                    Frequency, (int)Channels, WasmLoop ? 1 : 0);
                        WasmTrace.W($"[audio] wasm_play_pcm idx={Index} len={buffer.Count} sr={Frequency} ch={(int)Channels} vol={instanceVolume} -> handle={_wasmHandle}");
                    }
                }
            }

            if (_wasmHandle == 0)
            {
                return false;
            }

            // Approximate the sample duration so IsPlaying() keeps
            // returning true until playback finishes. PCM: bytes /
            // (sampleRate * channels * 2). Music: unknown length
            // until decode, so we set a large placeholder and rely
            // on AudioBufferSource.onended to clean up via
            // wasm_stop_sound triggered from the JS side.
            if (WasmIsCompressed)
            {
                DurationTime = curTime + 10 * 60 * 1000; // 10 min placeholder for music
            }
            else
            {
                int bytesPerSecond = Frequency * (int)Channels * 2;
                double ms = (buffer.Count * 1000.0) / Math.Max(bytesPerSecond, 1);
                DurationTime = curTime + ms;
            }
            return true;
#else
            if (SoundInstance != null && !SoundInstance.IsDisposed)
            {
                SoundInstance.Stop();
            }
            else
            {
                SoundInstance = new DynamicSoundEffectInstance(Frequency, Channels);
            }


            var buffer = GetBuffer();

            if (buffer.Count > 0)
            {
                _lastPlayedTime = curTime + Delay;

                SoundInstance.BufferNeeded += OnBufferNeeded;
                SoundInstance.SubmitBuffer(buffer.Array, buffer.Offset, buffer.Count);
                VolumeFactor = volumeFactor;
                Volume = volume;

                DurationTime = curTime + SoundInstance.GetSampleDuration(buffer.Count).TotalMilliseconds;

                SoundInstance.Play();

                return true;
            }

            return false;
#endif
        }

        public void Stop()
        {
#if BROWSER_WASM
            if (_wasmHandle > 0)
            {
                wasm_stop_sound(_wasmHandle);
                _wasmHandle = 0;
            }
#else
            if (SoundInstance != null)
            {
                SoundInstance.BufferNeeded -= OnBufferNeeded;
                SoundInstance.Stop();
            }
#endif

            AfterStop();
        }
    }
}
