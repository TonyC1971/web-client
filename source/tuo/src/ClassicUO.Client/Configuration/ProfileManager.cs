// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.ComponentModel;
using System.IO;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps.GridHighLight;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;

namespace ClassicUO.Configuration
{
    internal static class ProfileManager
    {
        /// <summary>
        /// Occurs when the current <see cref="Profile"/> has changed.
        /// Currently, this happens only during world creation/destruction, i.e., once per login.
        /// </summary>
        public static event EventHandler CurrentProfileChanged;

        /// <summary>
        /// Occurs when a property of the current <see cref="Profile"/> has changed.
        /// </summary>
        public static event PropertyChangedEventHandler CurrentProfilePropertyChanged;

        static ProfileManager()
        {
            // Subscribe to player creation event to load Char-scoped settings
            EventSink.OnPlayerCreated += OnPlayerCreated;
        }

        private static void OnPlayerCreated(object sender, System.EventArgs e) =>
            // Load Char-scoped settings after player is created (when serial is available)
            CurrentProfile?.LoadCharScopedSettings();

        public static Profile CurrentProfile
        {
            get;
            private set
            {
                if (field == value)
                    return;

                // If we had a profile, unregister the event first
                if (field != null)
                    field.PropertyChanged -= OnCurrentProfilePropertyChanged;

                field = value;

                // Register the event on the new value
                if (field != null)
                    field.PropertyChanged += OnCurrentProfilePropertyChanged;

                // Notify that the profile itself has changed (as opposed to a profile 'setting'
                CurrentProfileChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static string ProfilePath { get; private set; }

        public static string RootPath
        {
            get
            {
                if (string.IsNullOrEmpty(field))
                {
                    if (string.IsNullOrWhiteSpace(Settings.GlobalSettings.ProfilesPath))
                    {
                        field = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Profiles");
                    }
                    else
                    {
                        field = Settings.GlobalSettings.ProfilesPath;
                    }
                }

                return field;
            }
        }

        public static void Load(string servername, string username, string charactername)
        {
            string path = FileSystemHelper.CreateFolderIfNotExists(RootPath, username.Trim(), servername.Trim(), charactername.Trim());
            string fileToLoad = Path.Combine(path, "profile.json");

            ProfilePath = path;
            CurrentProfile = ConfigurationResolver.Load<Profile>(fileToLoad, ProfileJsonContext.DefaultToUse.Profile) ?? NewFromDefault();

            CurrentProfile.Username = username;
            CurrentProfile.ServerName = servername;
            CurrentProfile.CharacterName = charactername;


            // Sprite smoothing OFF for everyone, ONCE (operator 2026-07-28). The
            // first-run xBRZ default added earlier today is gone with it: smoothing is
            // opt-in again, and anyone who had it on has to turn it back on themselves.
            // Guarded by SmoothingResetV2 so it fires once per profile -- otherwise the
            // player could never keep the setting.
            // Unconditional, so the state is legible whether or not the migration is
            // eligible. Distinguishes "the branch never ran" from "it ran and the write
            // has not reached IndexedDB yet" -- the two were indistinguishable from
            // outside, which is where the v0.9.517 verification stalled. Also the only
            // answer to "why did my settings reset themselves": a one-shot migration
            // otherwise leaves no trace at all.
            Console.WriteLine($"[relief-probe] marker={CurrentProfile.SmoothingResetV2} "
                              + $"fps={Settings.GlobalSettings.FPS} path={ProfilePath}");

            if (!CurrentProfile.SmoothingResetV2)
            {
                // Captured before the reset so the log line below can state what actually
                // changed. Without it there is no telling "the branch never ran" from "it ran
                // and the write has not reached IndexedDB yet" -- exactly where the v0.9.517
                // verification stalled.
                int fpsBefore = Settings.GlobalSettings.FPS;
                CurrentProfile.SmoothingResetV2 = true;
                CurrentProfile.SpriteSmoothingMode = 0;
                CurrentProfile.SpriteSmoothingLevel = 0;
                CurrentProfile.SpriteSmoothingFull = false;
                // FPS too (operator 2026-07-28): anyone sitting above 45 comes DOWN to
                // 45. Only downwards -- a player who deliberately chose 30 for a weak
                // machine must not be pushed UP by a change meant to help them.
                if (Settings.GlobalSettings.FPS > 45)
                {
                    Settings.GlobalSettings.FPS = 45;
                    // Apply it NOW. SetRefreshRate ran at startup with the OLD value, so
                    // without this the slider would read 45 while the engine kept drawing
                    // 60 -- the exact opposite of the relief this is meant to give.
                    Client.Game?.SetRefreshRate(45);
                    // And persist it: FPS lives in settings.json, NOT in the profile, and
                    // the only login-path Save() happens at server selection, i.e. BEFORE
                    // this runs. Without the write the next boot reads 60 again while
                    // SmoothingResetV2 (saved with the profile) blocks a second attempt.
                    Settings.GlobalSettings.Save();
                }

                ConfigurationResolver.Save(CurrentProfile, Path.Combine(ProfilePath, "profile.json"), ProfileJsonContext.DefaultToUse.Profile);
                // Plain Console.WriteLine, NOT WasmTrace: trace strings are stripped from
                // prod builds and this has to survive there.
                string engineState = Client.Game != null ? "applied" : "no-game-yet";
                Console.WriteLine($"[relief-v2] first run for this profile: smoothing -> 0/0/false, "
                                  + $"fps {fpsBefore} -> {Settings.GlobalSettings.FPS}, engine={engineState}");
            }

            if (CurrentProfile.GridHighlightSetup.Count == 0)
            {
                GridHighLightProfile.MigrateGridHighlightToSetup(CurrentProfile);
                ConfigurationResolver.Save(CurrentProfile, Path.Combine(ProfilePath, "profile.json"), ProfileJsonContext.DefaultToUse.Profile);
            }

            // Load (or migrate from the legacy per-list profile storage) the cooldown-bar rules.
            if (CooldownBarsConfig.LoadForProfile(ProfilePath, CurrentProfile))
            {
                ConfigurationResolver.Save(CurrentProfile, Path.Combine(ProfilePath, "profile.json"), ProfileJsonContext.DefaultToUse.Profile);
            }

            ValidateFields(CurrentProfile);

            CurrentProfile.AfterLoad();

            Client.Game?.SetVSync(CurrentProfile.EnableVSync);
        }

        public static void SetProfileAsDefault(Profile profile) => profile.SaveAs(RootPath, "default.json");

        public static Profile NewFromDefault() => ConfigurationResolver.Load<Profile>(Path.Combine(RootPath, "default.json"), ProfileJsonContext.DefaultToUse.Profile) ?? new Profile();

        private static void ValidateFields(Profile profile)
        {
            if (profile == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(profile.ServerName))
            {
                throw new InvalidDataException();
            }

            if (string.IsNullOrEmpty(profile.Username))
            {
                throw new InvalidDataException();
            }

            if (string.IsNullOrEmpty(profile.CharacterName))
            {
                throw new InvalidDataException();
            }

            if (profile.WindowClientBounds.X < 600)
            {
                profile.WindowClientBounds = new Point(600, profile.WindowClientBounds.Y);
            }

            if (profile.WindowClientBounds.Y < 480)
            {
                profile.WindowClientBounds = new Point(profile.WindowClientBounds.X, 480);
            }
        }

        public static void UnLoadProfile() => CurrentProfile = null;

        private static void OnCurrentProfilePropertyChanged(object sender, PropertyChangedEventArgs e) => CurrentProfilePropertyChanged?.Invoke(sender, e);
    }
}
