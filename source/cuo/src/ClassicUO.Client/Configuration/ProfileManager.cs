// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;

namespace ClassicUO.Configuration
{
    internal static class ProfileManager
    {
        public static GlobalProfile GlobalProfile { get; private set; }
        public static Profile CurrentProfile { get; private set; }
        public static string ProfilePath { get; private set; }

        private static string _rootPath;
        private static string RootPath
        {
            get
            {
                if (string.IsNullOrEmpty(_rootPath))
                {
                    if (string.IsNullOrWhiteSpace(Settings.GlobalSettings.ProfilesPath))
                    {
                        _rootPath = Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Profiles");
                    }
                    else
                    {
                        _rootPath = Settings.GlobalSettings.ProfilesPath;
                    }
                }

                return _rootPath;
            }
        }

        public static void Load(string servername, string username, string charactername)
        {
            GlobalProfile = ConfigurationResolver.Load<GlobalProfile>(Path.Combine(RootPath, "globalprofile.json"), ProfileJsonContext.DefaultToUse.GlobalProfile) ?? new GlobalProfile();

            string path = FileSystemHelper.CreateFolderIfNotExists(RootPath, username, servername, charactername);
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
            // eligible. Deduction had run out: the seeded profile demonstrably came back
            // migrated, yet the in-branch log line never reached the console, and every
            // remaining explanation was a guess. This states the two inputs outright.
            Console.WriteLine($"[relief-probe] marker={CurrentProfile.SmoothingResetV2} "
                              + $"fps={Settings.GlobalSettings.FPS} path={ProfilePath}");

            if (!CurrentProfile.SmoothingResetV2)
            {
                // Captured before the reset so the log line below can state what actually
                // changed. Without it there is no way to tell "the branch never ran" from
                // "it ran and the write has not reached IndexedDB yet" -- which is exactly
                // where the v0.9.517 verification stalled: the smoke could see the profile
                // migrate but could not account for the frame cap.
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

                Save(CurrentProfile, ProfilePath);
                // Plain Console.WriteLine, NOT WasmTrace: trace strings are stripped from
                // prod builds and this has to survive there. It is also the answer to a
                // real support question -- "my settings reset themselves" -- which is
                // otherwise unanswerable, since a one-shot migration leaves no trace.
                string engineState = Client.Game != null ? "applied" : "no-game-yet";
                Console.WriteLine($"[relief-v2] first run for this profile: smoothing -> 0/0/false, "
                                  + $"fps {fpsBefore} -> {Settings.GlobalSettings.FPS}, engine={engineState}");
            }

            ValidateFields(CurrentProfile);

#if BROWSER_WASM
            // v0.4.88: force certain UX-sensitive toggles for Guest
            // sessions on every load, regardless of what the saved profile
            // says. main.js writes CUO_USER_KIND into Module.ENV during
            // preRun based on /api/me's session sub (`guest-*` => "guest",
            // anything else => "discord"). This override covers Guests
            // whose previous-session profile carries the opposite value.
            //
            // Settings affected:
            //   UseNewTargetSystem   -> OFF (HealthLinesManager + Macro target viz)
            //   EnablePathfind       -> OFF (double-right-click pathfinder auto-walk)
            //   UseAlternativeLights -> ON  (v0.5.12 — Video > Alternative lights)
            //
            // Discord users keep their saved values — only Guests are
            // force-overridden.
            string userKind = Environment.GetEnvironmentVariable("CUO_USER_KIND");
            if (string.Equals(userKind, "guest", StringComparison.Ordinal))
            {
                CurrentProfile.UseNewTargetSystem = false;
                CurrentProfile.EnablePathfind = false;
                CurrentProfile.UseAlternativeLights = true;
                Console.WriteLine("[cuo-guest] forced UseNewTargetSystem=false + EnablePathfind=false + UseAlternativeLights=true on profile load (Guest session)");
            }
            else
            {
                Console.WriteLine($"[cuo-guest] session kind='{userKind}' — no override applied");
            }
#endif
        }

        public static void SetProfileAsDefault(Profile profile)
        {
            Save(profile, RootPath, "default.json");
        }

        public static Profile NewFromDefault()
        {
            return ConfigurationResolver.Load<Profile>(Path.Combine(RootPath, "default.json"), ProfileJsonContext.DefaultToUse.Profile) ?? new Profile();
        }

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

        public static void UnLoadProfile()
        {
            GlobalProfile = null;
            CurrentProfile = null;
        }

        internal static void Save(Profile profile, string path, string filename = "profile.json")
        {
            ConfigurationResolver.Save(profile, Path.Combine(path, filename), ProfileJsonContext.DefaultToUse.Profile);
            if (GlobalProfile != null)
            {
                ConfigurationResolver.Save(GlobalProfile, Path.Combine(RootPath, "globalprofile.json"), ProfileJsonContext.DefaultToUse.GlobalProfile);
            }
        }
    }
}
