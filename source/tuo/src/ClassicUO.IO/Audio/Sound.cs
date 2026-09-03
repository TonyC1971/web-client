// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Utility.Logging;
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
        // v0.8.7 audio parity: TazUO upstream is FAudio-only
        // (DynamicSoundEffectInstance). FAudio is NOT linked in the wasm
        // build, so route through the Web Audio API bridge instead —
        // ported from the CUO wasm client: native shims wasm_play_* in
        // source/webclient/native-shims/SDL3.c → main.js wireWasmAudio →
        // AudioContext (+ spessasynth/midi-fallback for music). _wasmHandle
        // > 0 means JS holds an active AudioBufferSource for us; 0 = failed.
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
                    string[] extensions = { ".mp3", ".wav" };
                    string result = value;

                    foreach (string ext in extensions)
                    {
                        int index = value.IndexOf(ext, StringComparison.InvariantCultureIgnoreCase);
                        if (index != -1)
                        {
                            result = value.Substring(0, index + ext.Length);
                            break;
                        }
                    }

                    m_Name = result;
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

        public int CompareTo(Sound other) => other == null ? -1 : Index.CompareTo(other.Index);

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
        // Subclasses override to tell the wasm audio path whether to use
        // the raw-PCM route (UOSound: 16-bit signed mono @22050Hz, already
        // a valid AudioBuffer payload) or the decode-audio-data route
        // (UOMusic: compressed MP3 file bytes).
        protected virtual bool WasmIsCompressed => false;
        protected virtual bool WasmLoop => false;

        // If non-null, Sound.Play routes to wasm_play_music_url with this
        // URL instead of reading GetBuffer(). Used by UOMusic — the MP3
        // files aren't pre-mounted into MEMFS so they're fetched via HTTP
        // from the gamefiles/ junction.
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
            // Stop any previous play of this Sound instance, then route to
            // the wasm audio bridge (main.js __wasm_play_*).
            if (_wasmHandle > 0)
            {
                wasm_stop_sound(_wasmHandle);
                _wasmHandle = 0;
            }

            _lastPlayedTime = curTime + Delay;
            m_volumeFactor = volumeFactor;
            m_volume = volume;
            float instanceVolume = Math.Max(volume - volumeFactor, 0.0f);

            // URL route — UOMusic's MP3 fetched from the gamefiles/ junction
            // via HTTP. Bypasses GetBuffer entirely.
            string url = WasmUrl;
            if (url != null)
            {
                _wasmHandle = wasm_play_music_url(url, instanceVolume, WasmLoop ? 1 : 0);
                Log.Trace($"[audio] wasm_play_music_url name={Name} url={url} vol={instanceVolume} loop={WasmLoop} -> handle={_wasmHandle}");
                if (_wasmHandle == 0) return false;
                DurationTime = curTime + 10 * 60 * 1000; // placeholder
                return true;
            }

            ArraySegment<byte> buffer = GetBuffer();
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
                        Log.Trace($"[audio] wasm_play_music name={Name} len={buffer.Count} vol={instanceVolume} loop={WasmLoop} -> handle={_wasmHandle}");
                    }
                    else
                    {
                        _wasmHandle = wasm_play_pcm(ptr, buffer.Count, instanceVolume,
                                                    Frequency, (int)Channels, WasmLoop ? 1 : 0);
                        Log.Trace($"[audio] wasm_play_pcm idx={Index} len={buffer.Count} sr={Frequency} ch={(int)Channels} vol={instanceVolume} -> handle={_wasmHandle}");
                    }
                }
            }

            if (_wasmHandle == 0)
            {
                return false;
            }

            // Approximate sample duration so IsPlaying() stays true until
            // playback finishes. PCM: bytes / (sampleRate*channels*2).
            // Music: unknown until decode, so a large placeholder + JS-side
            // onended → wasm_stop_sound cleans up.
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


            ArraySegment<byte> buffer = GetBuffer();

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

        /// <summary>
        /// Submits additional buffers for seamless looping playback.
        /// Should be called after Play() when looping is desired.
        /// </summary>
        /// <param name="bufferCount">Number of additional buffers to submit (recommended: 2-3 for smooth playback)</param>
        public void SubmitAdditionalBuffers(int bufferCount)
        {
            if (SoundInstance != null && !SoundInstance.IsDisposed && SoundInstance.State == SoundState.Playing)
            {
                var buffer = GetBuffer();
                if (buffer.Count > 0)
                {
                    for (int i = 0; i < bufferCount; i++)
                    {
                        SoundInstance.SubmitBuffer(buffer.Array, buffer.Offset, buffer.Count);
                    }
                }
            }
        }
    }
}
