// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ClassicUO
{
    sealed class UltimaOnline
    {
        public Renderer.Animations.Animations Animations { get; private set; }
        public Renderer.Arts.Art Arts { get; private set; }
        public Renderer.Gumps.Gump Gumps { get; private set; }
        public Renderer.Texmaps.Texmap Texmaps { get; private set; }
        public Renderer.Lights.Light Lights { get; private set; }
        public Renderer.MultiMaps.MultiMap MultiMaps { get; private set; }
        public Renderer.Sounds.Sound Sounds { get; private set; }
        public World World { get; private set; }
        public GameCursor GameCursor { get; private set; }

        public ClientVersion Version { get; private set; }
        public ClientFlags Protocol { get; set; }
        public string ClientPath { get; private set; }
        public UOFileManager FileManager { get; private set; }


        public UltimaOnline()
        {

        }

        public unsafe void Load(GameController game)
        {
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U1: pre-LoadUOFiles");
            LoadUOFiles();
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U2: post-LoadUOFiles");

            const int TEXTURE_WIDTH = 512;
            const int TEXTURE_HEIGHT = 1024;
            const int LIGHTS_TEXTURE_WIDTH = 32;
            const int LIGHTS_TEXTURE_HEIGHT = 63;

            var hueSamplers = new Texture2D[2];
            hueSamplers[0] = new Texture2D(game.GraphicsDevice, TEXTURE_WIDTH, TEXTURE_HEIGHT);
            hueSamplers[1] = new Texture2D(game.GraphicsDevice, LIGHTS_TEXTURE_WIDTH, LIGHTS_TEXTURE_HEIGHT);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U3: hueSamplers Texture2D ctors done");

            uint[] buffer = new uint[Math.Max(
                LIGHTS_TEXTURE_WIDTH * LIGHTS_TEXTURE_HEIGHT,
                TEXTURE_WIDTH * TEXTURE_HEIGHT
            )];

            fixed (uint* ptr = buffer)
            {
                FileManager.Hues.CreateShaderColors(buffer);
                ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U4: CreateShaderColors done");

                hueSamplers[0].SetDataPointerEXT(
                    0,
                    null,
                    (IntPtr)ptr,
                    TEXTURE_WIDTH * TEXTURE_HEIGHT * sizeof(uint)
                );
                ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U5: hueSamplers[0].SetDataPointerEXT done");

                LightColors.CreateLightTextures(buffer, LIGHTS_TEXTURE_HEIGHT);
                ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U6: CreateLightTextures done");
                hueSamplers[1].SetDataPointerEXT(
                    0,
                    null,
                    (IntPtr)ptr,
                    LIGHTS_TEXTURE_WIDTH * LIGHTS_TEXTURE_HEIGHT * sizeof(uint)
                );
                ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U7: hueSamplers[1].SetDataPointerEXT done");
            }

            game.GraphicsDevice.Textures[1] = hueSamplers[0];
            game.GraphicsDevice.Textures[2] = hueSamplers[1];
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U8: textures assigned");

            Animations = new Renderer.Animations.Animations(FileManager.Animations, game.GraphicsDevice);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U9: Animations renderer ctor");
            Arts = new Renderer.Arts.Art(FileManager.Arts, FileManager.Hues, game.GraphicsDevice);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U10: Arts renderer ctor");
            Gumps = new Renderer.Gumps.Gump(FileManager.Gumps, game.GraphicsDevice);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U11: Gumps renderer ctor");
            Texmaps = new Renderer.Texmaps.Texmap(FileManager.Texmaps, game.GraphicsDevice);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U12: Texmaps renderer ctor");
            Lights = new Renderer.Lights.Light(FileManager.Lights, game.GraphicsDevice);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U13: Lights renderer ctor");
            MultiMaps = new Renderer.MultiMaps.MultiMap(FileManager.MultiMaps, game.GraphicsDevice);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U14: MultiMaps renderer ctor");
            Sounds = new Renderer.Sounds.Sound(FileManager.Sounds);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U15: Sounds renderer ctor");

            LightColors.LoadLights();
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U16: LightColors.LoadLights done");

            World = new World();
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U17: new World done");
            GameCursor = new GameCursor(World);
            ClassicUO.Utility.Logging.Log.Trace("[uoload-debug] U18: GameCursor done — Load EXIT");
        }

        public void Unload()
        {
            FileManager.Dispose();
            World?.Map?.Destroy();
        }


        private void LoadUOFiles()
        {
            Task<bool> skipServerSelectTask = Client.Settings.GetAsync(SettingsScope.Global, Constants.SqlSettings.SKIP_SERVER_SELECTION, false);

            TazLang.Load(Settings.GlobalSettings.UILanguage);

            string clientPath = Settings.GlobalSettings.UltimaOnlineDirectory;
            Log.Trace($"Ultima Online installation folder: {clientPath}");

            Log.Trace("Loading files...");

            if (!string.IsNullOrWhiteSpace(Settings.GlobalSettings.ClientVersion))
            {
                // sanitize client version
                Settings.GlobalSettings.ClientVersion = Settings.GlobalSettings.ClientVersion.Replace(",", ".").Replace(" ", "").ToLower();
            }

            string clientVersionText = Settings.GlobalSettings.ClientVersion;

            // check if directory is good
            if (!Directory.Exists(clientPath))
            {
                Log.Error("Invalid client directory: " + clientPath);
                Client.ShowErrorMessage(string.Format(ResErrorMessages.ClientPathIsNotAValidUODirectory, clientPath));

                throw new InvalidClientDirectory($"'{clientPath}' is not a valid directory");
            }

            // try to load the client version
            if (!ClientVersionHelper.IsClientVersionValid(clientVersionText, out ClientVersion clientVersion))
            {
                Log.Warn($"Client version [{clientVersionText}] is invalid, let's try to read the client.exe");

                // mmm something bad happened, try to load from client.exe
                if (!ClientVersionHelper.TryParseFromFile(Path.Combine(clientPath, "client.exe"), out clientVersionText) || !ClientVersionHelper.IsClientVersionValid(clientVersionText, out clientVersion))
                {
                    Log.Error("Invalid client version: " + clientVersionText);
                    Client.ShowErrorMessage(string.Format(ResGumps.ImpossibleToDefineTheClientVersion0, clientVersionText));

                    throw new InvalidClientVersion($"Invalid client version: '{clientVersionText}'");
                }

                Log.Trace($"Found a valid client.exe [{clientVersionText} - {clientVersion}]");

                // update the wrong/missing client version in settings.json
                Settings.GlobalSettings.ClientVersion = clientVersionText;
            }

            Version = clientVersion;
            ClientPath = clientPath;

            Protocol = ClientFlags.CF_T2A;

            if (Version >= ClientVersion.CV_200)
            {
                Protocol |= ClientFlags.CF_RE;
            }

            if (Version >= ClientVersion.CV_300)
            {
                Protocol |= ClientFlags.CF_TD;
            }

            if (Version >= ClientVersion.CV_308)
            {
                Protocol |= ClientFlags.CF_LBR;
            }

            if (Version >= ClientVersion.CV_308Z)
            {
                Protocol |= ClientFlags.CF_AOS;
            }

            if (Version >= ClientVersion.CV_405A)
            {
                Protocol |= ClientFlags.CF_SE;
            }

            if (Version >= ClientVersion.CV_60144)
            {
                Protocol |= ClientFlags.CF_SA;
            }

            skipServerSelectTask.Wait();
            Settings.GlobalSettings.SkipServerSelect = skipServerSelectTask.Result || CUOEnviroment.SkipServerSelect;

            Log.Trace($"Client path: '{clientPath}'");
            Log.Trace($"Client version: {clientVersion}");
            Log.Trace($"Protocol: {Protocol}");

            FileManager = new UOFileManager(clientVersion, clientPath);

            try
            {
                FileManager.Load(Settings.GlobalSettings.UseVerdata, Settings.GlobalSettings.Language, Settings.GlobalSettings.MapsLayouts);
            }
            catch (FileNotFoundException ex)
            {
                string missing = !string.IsNullOrEmpty(ex.FileName) ? ex.FileName : ex.Message;

                Log.Error($"Missing required UO data file while loading: {ex}");

                Client.ShowErrorMessage(
                    "A required Ultima Online data file could not be found:\n\n" +
                    $"{missing}\n\n" +
                    "Please verify your UO data files are present in:\n" +
                    $"{clientPath}");

                // Exit cleanly so the global unhandled-exception handler does not
                // generate a crash log/report for what is a missing-files setup issue.
                Environment.Exit(0);
            }

            StaticFilters.Load(FileManager.TileData);
            BuffTable.Load();
            ChairTable.Load();

            //ATTENTION: you will need to enable ALSO ultimalive server-side, or this code will have absolutely no effect!
            UltimaLive.Enable();
        }
    }


    internal static class Client
    {
        public static GameController Game { get; private set; }
        public static SQLSettingsManager Settings { get; private set; }
        public static bool UnitTestingActive;

        public static void Run(IPluginHost pluginHost)
        {
            Debug.Assert(Game == null);

            Log.Trace("[run-debug] R1: Running game...");

            // Initialize SQLSettingsManager
            Settings = new SQLSettingsManager();
            Log.Trace("[run-debug] R2: SQLSettingsManager initialized");

            Log.Trace("[run-debug] R3: pre-GameController ctor");
            Game = new GameController(pluginHost);
            Log.Trace("[run-debug] R4: post-GameController ctor");

            using (Game)
            {
                // https://github.com/FNA-XNA/FNA/wiki/7:-FNA-Environment-Variables#fna_graphics_enable_highdpi
                CUOEnviroment.IsHighDPI = Environment.GetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI") == "1";
                Log.Trace($"[run-debug] R5: IsHighDPI={CUOEnviroment.IsHighDPI}");

                _ = Settings.GetAsyncOnMainThread(SettingsScope.Global, Constants.SqlSettings.GAME_SCALE, 1f, f => Game.SetScale(f));
                Log.Trace("[run-debug] R6: post-GetAsyncOnMainThread");

                Log.Trace("[run-debug] R7: pre-Game.Run");
                Game.Run();
                Log.Trace("[run-debug] R8: post-Game.Run");
            }

            // Dispose SQLSettingsManager
            try
            {
                Settings?.Dispose();
                Log.Trace("SQLSettingsManager disposed");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to dispose SQLSettingsManager: {ex.Message}");
            }

            Log.Trace("Exiting game...");
        }

        public static void ShowErrorMessage(string msg) => SDL.SDL_ShowSimpleMessageBox(SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "ERROR", msg, IntPtr.Zero);

        /// <summary>
        /// Guidance shown when the graphics shaders fail to compile. This almost always indicates an
        /// environment problem (outdated/unavailable OpenGL) rather than a bug in the shader itself.
        /// </summary>
        public const string GraphicsShaderHelpMessage =
            "TazUO could not compile its graphics shaders. This almost always means your system's OpenGL " +
            "support is too old or unavailable - common causes are running over Remote Desktop, running in a " +
            "virtual machine without 3D acceleration, or missing/outdated GPU drivers.\n\n" +
            "Try: update your GPU drivers, run on the local console (not Remote Desktop), or change the renderer " +
            "in settings (e.g. Vulkan).\n\n" +
            "You can also try launching TazUO with a different graphics driver by adding one of the following " +
            "command-line arguments:\n" +
            "     -force_driver 1   (OpenGL)\n" +
            "     -force_driver 2   (Vulkan)\n" +
            "     -force_driver 3   (SDL/FNA auto-select)\n" +
            "   Try each one in turn until the client starts successfully.";

        /// <summary>
        /// Returns true when the exception was raised while compiling an effect/shader (e.g. the
        /// MOJOSHADER_compileEffect failures produced by FNA3D when the host's OpenGL is unsupported).
        /// </summary>
        public static bool IsShaderCompileFailure(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e.Message != null &&
                    (e.Message.Contains("MOJOSHADER", StringComparison.OrdinalIgnoreCase) ||
                     e.Message.Contains("compileEffect", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
