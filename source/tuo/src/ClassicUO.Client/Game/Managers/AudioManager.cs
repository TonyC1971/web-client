// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Utility;
using ClassicUO.Configuration;
using ClassicUO.IO.Audio;
using ClassicUO.Assets;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Audio;

namespace ClassicUO.Game.Managers
{
    public sealed class AudioManager
    {
        const float SOUND_DELTA = 250;

        private bool _canReproduceAudio = true;
        private bool _audioDeviceDisconnected = false;
        private uint _lastAudioRecoveryAttempt = 0;
        private const uint AUDIO_RECOVERY_DELAY = 1000; // 1 second delay between recovery attempts
        private readonly LinkedList<UOSound> _currentSounds = new LinkedList<UOSound>();
        private readonly UOMusic[] _currentMusic = { null, null };
        private readonly int[] _currentMusicIndices = { 0, 0 };
        private UOSound _currentAmbient;
        private int _currentAmbientIndex;
        private float _currentAmbientVolume;
        public int LoginMusicIndex { get; private set; }
        public int CurrentAmbientIndex => _currentAmbientIndex;
        public bool HasAmbientSound => _currentAmbient != null;
        public int DeathMusicIndex { get; } = 42;
        private long _nextAudioHealthCheck = 0;

        /// <summary>
        /// Index, Name
        /// </summary>
        public LimitedFIFOCollection<(int, string)> LastPlayedSounds { get; } = new(5);
        public LimitedFIFOCollection<(int, string)> LastPlayedMusic { get; } = new(5);

        public void Initialize()
        {
#if BROWSER_WASM
            // v0.8.7 WASM AUDIO (parity with CUO web): playback now routes
            // through the Web Audio bridge (Sound.cs #if BROWSER_WASM branch →
            // native shims wasm_play_* → main.js wireWasmAudio → AudioContext
            // + spessasynth/midi for music). FAudio is still NOT linked, so we
            // must never touch DynamicSoundEffectInstance / SoundEffect.
            // MasterVolume / the audio-device health-check (all FAudio-backed →
            // P/Invoke trap). So: keep _canReproduceAudio TRUE and let the
            // per-Sound wasm branch do the work, but SKIP all device management
            // — do NOT wire the Activated/Deactivated window handlers (their
            // body touches SoundEffect.MasterVolume; Web Audio's main.js
            // visibilitychange handles focus-mute), and the Update() health
            // check + TryCreateAudioInstance are #if-guarded off below.
            _canReproduceAudio = true;
            LoginMusicIndex = Client.Game.UO.Version switch
            {
                >= ClientVersion.CV_7000 => 78,
                > ClientVersion.CV_308Z => 0,
                _ => 8
            };
            Log.Trace("Audio enabled on BROWSER_WASM via Web Audio bridge (wasm_play_*)");
            return;
#else
            try
            {
                if(!System.Diagnostics.Debugger.IsAttached)
                    new DynamicSoundEffectInstance(0, AudioChannels.Mono).Dispose();
                else //Fix for rider debugging not having audio apparently
                    _canReproduceAudio = false;
            }
            catch (NoAudioHardwareException ex)
            {
                Log.Warn(ex.ToString());
                _canReproduceAudio = false;
            }

            LoginMusicIndex = Client.Game.UO.Version switch
            {
                >= ClientVersion.CV_7000 => 78, // LoginLoop
                > ClientVersion.CV_308Z => 0,
                _ => 8 // stones2
            };

            Client.Game.Activated += OnWindowActivated;
            Client.Game.Deactivated += OnWindowDeactivated;
#endif
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
#if BROWSER_WASM
            // Not wired on wasm (Initialize returns early), but guard anyway:
            // the body touches FAudio SoundEffect.MasterVolume. Web Audio's
            // main.js visibilitychange handles focus-mute.
            return;
#else
            if (!_canReproduceAudio || _audioDeviceDisconnected || ProfileManager.CurrentProfile == null || ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            {
                return;
            }

            try
            {
                SoundEffect.MasterVolume = 0;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to set master volume on window deactivation: {ex.Message}");
                _audioDeviceDisconnected = true;
            }
#endif
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
#if BROWSER_WASM
            // Not wired on wasm (Initialize returns early); guard anyway —
            // body touches FAudio SoundEffect.MasterVolume + TryImmediateFallback.
            return;
#else
            if (!_canReproduceAudio || ProfileManager.CurrentProfile == null || ProfileManager.CurrentProfile.ReproduceSoundsInBackground)
            {
                return;
            }

            if (_audioDeviceDisconnected)
            {
                TryImmediateFallback();
                return;
            }

            try
            {
                SoundEffect.MasterVolume = 1;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to set master volume on window activation: {ex.Message}");
                _audioDeviceDisconnected = true;
                TryImmediateFallback();
            }
#endif
        }

        public void PlaySound(int index, bool skipFilter = false)
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            if (!_canReproduceAudio || _audioDeviceDisconnected || currentProfile == null)
            {
                return;
            }

            // Check if sound is filtered
            if (!skipFilter && SoundFilterManager.Instance.IsSoundFiltered(index))
            {
                return;
            }

            float volume = currentProfile.SoundVolume / SOUND_DELTA;

            if (Client.Game.IsActive)
            {
                if (!currentProfile.ReproduceSoundsInBackground)
                {
                    volume = currentProfile.SoundVolume / SOUND_DELTA;
                }
            }
            else if (!currentProfile.ReproduceSoundsInBackground)
            {
                volume = 0;
            }

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            if (!currentProfile.EnableSound || !Client.Game.IsActive && !currentProfile.ReproduceSoundsInBackground)
            {
                volume = 0;
            }

            var sound = (UOSound) Client.Game.UO.Sounds.GetSound(index);

            if (sound != null)
            {
                // Track last played sound
                LastPlayedSounds.Add((index, sound.Name));

                try
                {
                    if (sound.Play(Time.Ticks, volume))
                    {
                        sound.X = -1;
                        sound.Y = -1;
                        sound.CalculateByDistance = false;

                        _currentSounds.AddLast(sound);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to play sound {index}: {ex.Message}");
                    _audioDeviceDisconnected = true;
                }
            }
        }

        public void PlaySoundWithDistance(World world, int index, int x, int y)
        {
            if (!_canReproduceAudio || _audioDeviceDisconnected || !world.InGame)
            {
                return;
            }

            if (SoundFilterManager.Instance.IsSoundFiltered(index))
            {
                return;
            }

            int distX = Math.Abs(x - world.Player.X);
            int distY = Math.Abs(y - world.Player.Y);
            int distance = Math.Max(distX, distY);

            Profile currentProfile = ProfileManager.CurrentProfile;
            float volume = currentProfile.SoundVolume / SOUND_DELTA;
            float distanceFactor = 0.0f;

            if (distance >= 1)
            {
                float volumeByDist = volume / (world.ClientViewRange + 1);
                distanceFactor = volumeByDist * distance;
            }

            if (distance > world.ClientViewRange)
            {
                volume = 0;
            }

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            if (currentProfile == null || !currentProfile.EnableSound || !Client.Game.IsActive && !currentProfile.ReproduceSoundsInBackground)
            {
                volume = 0;
            }

            var sound = (UOSound)Client.Game.UO.Sounds.GetSound(index);

            if (sound != null)
            {
                // Track last played sound
                LastPlayedSounds.Add((index, sound.Name));

                try
                {
                    if (sound.Play(Time.Ticks, volume, distanceFactor))
                    {
                        sound.X = x;
                        sound.Y = y;
                        sound.CalculateByDistance = true;

                        _currentSounds.AddLast(sound);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to play sound {index} with distance: {ex.Message}");
                    _audioDeviceDisconnected = true;
                }
            }
        }

        public void PlayMusic(int music, bool iswarmode = false, bool is_login = false, bool skipIgnore = false)
        {
            if (!_canReproduceAudio || _audioDeviceDisconnected)
            {
                return;
            }

            if (music >= Constants.MAX_MUSIC_DATA_INDEX_COUNT)
            {
                return;
            }

            if (!skipIgnore && SoundFilterManager.Instance.IsSoundFiltered(music, true))
            {
                return;
            }

            float volume;

            if (is_login)
            {
                volume = Settings.GlobalSettings.LoginMusic ? Settings.GlobalSettings.LoginMusicVolume / SOUND_DELTA : 0;
            }
            else
            {
                Profile currentProfile = ProfileManager.CurrentProfile;

                if (currentProfile == null || !currentProfile.EnableMusic)
                {
                    volume = 0;
                }
                else
                {
                    volume = currentProfile.MusicVolume / SOUND_DELTA;
                }

                if (currentProfile != null && !currentProfile.EnableCombatMusic && iswarmode)
                {
                    return;
                }
            }


            if (volume < -1 || volume > 1f)
            {
                return;
            }

            Sound m = Client.Game.UO.Sounds.GetMusic(music);

            if (m == null && _currentMusic[0] != null)
            {
                StopMusic();
            }
            else if (m != null && (m != _currentMusic[0] || iswarmode))
            {
                StopMusic();

                int idx = iswarmode ? 1 : 0;
                _currentMusicIndices[idx] = music;
                _currentMusic[idx] = (UOMusic) m;

                try
                {
                    _currentMusic[idx].Play(Time.Ticks, volume);
                    LastPlayedMusic.Add((music, m.Name));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to play music {music}: {ex.Message}");
                    _audioDeviceDisconnected = true;
                    _currentMusic[idx] = null;
                    _currentMusicIndices[idx] = 0;
                }
            }
        }

        public void UpdateCurrentMusicVolume(bool isLogin = false)
        {
            if (!_canReproduceAudio || _audioDeviceDisconnected)
            {
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null)
                {
                    float volume;

                    if (isLogin)
                    {
                        volume = Settings.GlobalSettings.LoginMusic ? Settings.GlobalSettings.LoginMusicVolume / SOUND_DELTA : 0;
                    }
                    else
                    {
                        Profile currentProfile = ProfileManager.CurrentProfile;

                        volume = currentProfile == null || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA;
                    }


                    if (volume < -1 || volume > 1f)
                    {
                        return;
                    }

                    try
                    {
                        _currentMusic[i].Volume = i == 0 && _currentMusic[1] != null ? 0 : volume;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Failed to set music volume: {ex.Message}");
                        _audioDeviceDisconnected = true;
                    }
                }
            }
        }

        public void UpdateCurrentSoundsVolume()
        {
            if (!_canReproduceAudio || _audioDeviceDisconnected)
            {
                return;
            }

            Profile currentProfile = ProfileManager.CurrentProfile;

            float volume = currentProfile == null || !currentProfile.EnableSound ? 0 : currentProfile.SoundVolume / SOUND_DELTA;

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            for (LinkedListNode<UOSound> soundNode = _currentSounds.First; soundNode != null; soundNode = soundNode.Next)
            {
                try
                {
                    soundNode.Value.Volume = volume;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to set sound volume: {ex.Message}");
                    _audioDeviceDisconnected = true;
                    break;
                }
            }
        }

        public void StopMusic()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null)
                {
                    _currentMusic[i].Stop();
                    _currentMusic[i].Dispose();
                    _currentMusic[i] = null;
                }
            }
        }

        public void StopWarMusic() => PlayMusic(_currentMusicIndices[0]);

        public void PlayAmbientSound(int index, float volume, bool skipFilter = false)
        {
            if (!_canReproduceAudio || _audioDeviceDisconnected)
            {
                return;
            }

            if (!skipFilter && SoundFilterManager.Instance.IsSoundFiltered(index))
            {
                return;
            }

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            if (_currentAmbientIndex == index && _currentAmbient != null)
            {
                SetAmbientVolume(volume);
                return;
            }

            StopAmbientSound();

            var sound = (UOSound)Client.Game.UO.Sounds.GetSound(index);

            if (sound == null)
            {
                return;
            }

            try
            {
                sound.IsLooping = true;

                if (sound.Play(Time.Ticks, volume, 0.0f))
                {
                    sound.SubmitAdditionalBuffers(2);
                    sound.X = -1;
                    sound.Y = -1;
                    sound.CalculateByDistance = false;

                    _currentAmbient = sound;
                    _currentAmbientIndex = index;
                    _currentAmbientVolume = volume;
                }
                else
                {
                    sound.IsLooping = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to play ambient sound {index}: {ex.Message}");
                _audioDeviceDisconnected = true;
                sound.IsLooping = false;
                _currentAmbient = null;
                _currentAmbientIndex = 0;
                _currentAmbientVolume = 0;
            }
        }

        public void SetAmbientVolume(float volume)
        {
            if (!_canReproduceAudio || _audioDeviceDisconnected || _currentAmbient == null)
            {
                return;
            }

            if (volume < -1 || volume > 1f)
            {
                return;
            }

            try
            {
                _currentAmbientVolume = volume;
                _currentAmbient.Volume = volume;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to set ambient volume: {ex.Message}");
                _audioDeviceDisconnected = true;
                StopAmbientPlayback(clearState: false);
            }
        }

        public void StopAmbientSound() => StopAmbientPlayback(clearState: true);

        private void StopAmbientPlayback(bool clearState)
        {
            if (_currentAmbient != null)
            {
                _currentAmbient.IsLooping = false;
                _currentAmbient.Stop();
                _currentAmbient = null;
            }

            if (clearState)
            {
                _currentAmbientIndex = 0;
                _currentAmbientVolume = 0;
            }
        }

        public void StopSounds()
        {
            LinkedListNode<UOSound> first = _currentSounds.First;

            while (first != null)
            {
                LinkedListNode<UOSound> next = first.Next;

                first.Value.Stop();

                _currentSounds.Remove(first);

                first = next;
            }
        }

        public void Update()
        {
            if (!_canReproduceAudio)
            {
                return;
            }

#if !BROWSER_WASM
            // FAudio-backed device recovery/health-check — meaningless on
            // wasm (Web Audio has no "device" to disconnect) and would trap
            // via TryCreateAudioInstance's DynamicSoundEffectInstance probe.
            if (_audioDeviceDisconnected)
            {
                TryRecoverAudio();
                if (_audioDeviceDisconnected)
                {
                    return;
                }
            }

            if(Time.Ticks > _nextAudioHealthCheck)
            {
                CheckAudioDeviceHealth();
                _nextAudioHealthCheck = Time.Ticks + 5000;
            }
#endif

            bool runninWarMusic = _currentMusic[1] != null;
            Profile currentProfile = ProfileManager.CurrentProfile;

            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null && currentProfile != null)
                {
                    if (Client.Game.IsActive)
                    {
                        if (!currentProfile.ReproduceSoundsInBackground)
                        {
                            _currentMusic[i].Volume = i == 0 && runninWarMusic || !currentProfile.EnableMusic ? 0 : currentProfile.MusicVolume / SOUND_DELTA;
                        }
                    }
                    else if (!currentProfile.ReproduceSoundsInBackground && _currentMusic[i].Volume != 0.0f)
                    {
                        _currentMusic[i].Volume = 0;
                    }
                }

                _currentMusic[i]?.Update();
            }

            try
            {
                _currentAmbient?.MaintainLoopBuffers();
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to maintain ambient buffers: {ex.Message}");
                _audioDeviceDisconnected = true;
                StopAmbientPlayback(clearState: false);
            }

            LinkedListNode<UOSound> first = _currentSounds.First;

            while (first != null)
            {
                LinkedListNode<UOSound> next = first.Next;

                if (!first.Value.IsPlaying(Time.Ticks))
                {
                    first.Value.Stop();
                    _currentSounds.Remove(first);
                }

                first = next;
            }
        }

        public UOMusic GetCurrentMusic()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_currentMusic[i] != null && _currentMusic[i].IsPlaying(Time.Ticks))
                {
                    return _currentMusic[i];
                }
            }
            return null;
        }

        public void OnAudioDeviceAdded()
        {
            if (_audioDeviceDisconnected && _canReproduceAudio)
            {
                Log.Info("Audio device added - attempting immediate recovery...");
                TryImmediateFallback();
            }
            else if (_canReproduceAudio)
            {
                Log.Info("Audio device added while audio is working - system has more audio options available");
            }
        }

        public void OnAudioDeviceRemoved()
        {
            if (_canReproduceAudio)
            {
                Log.Warn("Audio device removed - attempting immediate fallback to alternative device");
                _audioDeviceDisconnected = true;

                StopAllAudio();

                TryImmediateFallback();
            }
        }

        private void TryImmediateFallback()
        {
            Log.Info("Attempting immediate fallback to available audio device...");

            if (TryCreateAudioInstance())
            {
                _audioDeviceDisconnected = false;
                Log.Info("Immediate audio fallback successful!");
                RestoreCurrentMusic();
                RestoreCurrentAmbient();
            }
            else
            {
                Log.Warn("Immediate audio fallback failed - no alternative device available");
                _lastAudioRecoveryAttempt = Time.Ticks;
            }
        }

        private void TryRecoverAudio()
        {
            if (Time.Ticks - _lastAudioRecoveryAttempt < AUDIO_RECOVERY_DELAY)
            {
                return;
            }

            _lastAudioRecoveryAttempt = Time.Ticks;

            Log.Info("Attempting audio recovery...");

            if (TryCreateAudioInstance())
            {
                _audioDeviceDisconnected = false;
                Log.Info("Audio recovery successful!");
                RestoreCurrentMusic();
                RestoreCurrentAmbient();
            }
            else
            {
                Log.Warn("Audio recovery failed - no hardware available");
            }
        }

        private bool TryCreateAudioInstance()
        {
#if BROWSER_WASM
            // FAudio not linked on wasm — `new DynamicSoundEffectInstance`
            // would P/Invoke-trap. All device recovery (TryImmediateFallback /
            // TryRecoverAudio, incl. the SDL OnAudioDeviceAdded/Removed events)
            // funnels through here, so neutering this one chokepoint keeps the
            // whole FAudio device-management path off on wasm. Web Audio needs
            // no device probe.
            return false;
#else
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var testInstance = new DynamicSoundEffectInstance(22050, AudioChannels.Mono);
                        testInstance.Dispose();

                        Log.Info($"Audio device test successful on attempt {attempt + 1}");
                        return true;
                    }
                    catch (NoAudioHardwareException) when (attempt < 2)
                    {
                        Log.Warn($"Audio test attempt {attempt + 1} failed - trying again...");
                    }
                }
                return false;
            }
            catch (NoAudioHardwareException ex)
            {
                Log.Warn($"No audio hardware available: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio device test failed: {ex.Message}");
                return false;
            }
#endif
        }

        private void StopAllAudio()
        {
            try
            {
                StopSounds();
                StopMusic();
                StopAmbientPlayback(clearState: false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Error stopping audio during device disconnection: {ex.Message}");
            }
        }

        private void RestoreCurrentAmbient()
        {
            if (_currentAmbientIndex > 0)
            {
                PlayAmbientSound(_currentAmbientIndex, _currentAmbientVolume);
            }
        }

        private void RestoreCurrentMusic()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_currentMusicIndices[i] > 0)
                {
                    PlayMusic(_currentMusicIndices[i], i == 1);
                }
            }
        }

        private bool CheckAudioDeviceHealth()
        {
#if BROWSER_WASM
            // FAudio SoundEffect.MasterVolume read would trap on wasm. Only
            // ever called from the #if !BROWSER_WASM Update() block, so this
            // guard is defensive. Web Audio has no device to health-check.
            return false;
#else
            if (!_canReproduceAudio || _audioDeviceDisconnected)
            {
                return false;
            }

            try
            {
                float volume = SoundEffect.MasterVolume;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio device health check failed: {ex.Message}");
                _audioDeviceDisconnected = true;
                return false;
            }
#endif
        }
    }
}
