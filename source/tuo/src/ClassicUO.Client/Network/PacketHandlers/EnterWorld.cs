using System;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class EnterWorld
{
    public static void Receive(World world, ref StackDataReader p)
    {
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW0: enter Receive");
#endif
        uint serial = p.ReadUInt32BE();

#if BROWSER_WASM
        Log.Trace($"[ew-debug] EW1: pre-CreatePlayer serial=0x{serial:X8}");
#endif
        world.CreatePlayer(serial);
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW2: post-CreatePlayer");
#endif

        p.Skip(4);
        world.Player.Graphic = p.ReadUInt16BE();
#if BROWSER_WASM
        Log.Trace($"[ew-debug] EW3: pre-CheckGraphicChange graphic=0x{world.Player.Graphic:X4}");
#endif
        world.Player.CheckGraphicChange();
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW4: post-CheckGraphicChange");
#endif
        ushort x = p.ReadUInt16BE();
        ushort y = p.ReadUInt16BE();
        sbyte z = (sbyte)p.ReadUInt16BE();

        if (world.Map == null)
            world.MapIndex = 0;

#if BROWSER_WASM
        Log.Trace($"[ew-debug] EW5: pre-SetInWorldTile xyz=({x},{y},{z})");
#endif
        world.Player.SetInWorldTile(x, y, z);
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW6: post-SetInWorldTile");
#endif
        world.Player.Direction = (Direction)(p.ReadUInt8() & 0x7);
        world.RangeSize.X = x;
        world.RangeSize.Y = y;

        if (
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseCustomLightLevel
        )
            world.Light.Overall =
                ProfileManager.CurrentProfile.LightLevelType == 1
                    ? Math.Min(world.Light.Overall, ProfileManager.CurrentProfile.LightLevel)
                    : ProfileManager.CurrentProfile.LightLevel;

#if BROWSER_WASM
        Log.Trace("[ew-debug] EW7: pre-UpdateCurrentMusicVolume");
#endif
        Client.Game.Audio.UpdateCurrentMusicVolume();
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW8: post-UpdateCurrentMusicVolume");
#endif

        if (Client.Game.UO.Version >= ClassicUO.Utility.ClientVersion.CV_200)
        {
            if (ProfileManager.CurrentProfile != null)
#if BROWSER_WASM
            {
                Log.Trace("[ew-debug] EW9: pre-Send_GameWindowSize");
                AsyncNetClient.Socket.Send_GameWindowSize(
                    (uint)Client.Game.Scene.Camera.Bounds.Width,
                    (uint)Client.Game.Scene.Camera.Bounds.Height
                );
                Log.Trace("[ew-debug] EW10: post-Send_GameWindowSize");
            }
#else
                AsyncNetClient.Socket.Send_GameWindowSize(
                    (uint)Client.Game.Scene.Camera.Bounds.Width,
                    (uint)Client.Game.Scene.Camera.Bounds.Height
                );
#endif

            AsyncNetClient.Socket.Send_Language(Settings.GlobalSettings.Language);
        }

#if BROWSER_WASM
        Log.Trace("[ew-debug] EW11: pre-Send_ClientVersion");
#endif
        AsyncNetClient.Socket.Send_ClientVersion(Settings.GlobalSettings.ClientVersion);
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW12: pre-SingleClick");
#endif

        GameActions.SingleClick(world, world.Player);
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW13: post-SingleClick pre-Send_SkillsRequest");
#endif
        AsyncNetClient.Socket.Send_SkillsRequest(world.Player.Serial);
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW14: post-Send_SkillsRequest");
#endif

        if (world.Player.IsDead)
            world.ChangeSeason(ClassicUO.Game.Managers.Season.Desolation, 42);

        if (
            Client.Game.UO.Version >= ClassicUO.Utility.ClientVersion.CV_70796
            && ProfileManager.CurrentProfile != null
        )
            AsyncNetClient.Socket.Send_ShowPublicHouseContent(
                ProfileManager.CurrentProfile.ShowHouseContent
            );

#if BROWSER_WASM
        Log.Trace("[ew-debug] EW15: pre-Send_ToPlugins_AllSkills");
#endif
        AsyncNetClient.Socket.Send_ToPlugins_AllSkills();
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW16: pre-Send_ToPlugins_AllSpells");
#endif
        AsyncNetClient.Socket.Send_ToPlugins_AllSpells();
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW17: post-Send_ToPlugins_AllSpells");
#endif

#if !BROWSER_WASM
        // Desktop-only: the map web server is an HttpListener — impossible in
        // the browser build (audit T3-1). A persisted WebMapAutoStart=true from
        // a desktop profile only produced an error log per session here.
        if (ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.WebMapAutoStart &&
            !MapWebServerManager.Instance.IsRunning)
            _ = MapWebServerManager.Instance.Start();
#endif
#if BROWSER_WASM
        Log.Trace("[ew-debug] EW99: exit Receive");
#endif
    }
}
