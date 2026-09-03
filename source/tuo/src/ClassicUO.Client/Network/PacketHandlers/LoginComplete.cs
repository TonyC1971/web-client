using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.Gumps.Login;
using ClassicUO.IO;
using ClassicUO.Utility;

namespace ClassicUO.Network.PacketHandlers;

internal static class LoginComplete
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player != null && Client.Game.Scene is LoginScene)
        {
            var scene = new GameScene(world);
            Client.Game.SetScene(scene);
            LoginScene.Instance?.Dispose();
            LoginGump.Instance?.Dispose();
#if BROWSER_WASM
            // Tell main.js the world scene is live so it resizes the canvas from
            // the LoginScene's fixed 640x480 up to the browser window. NON-blocking
            // async signal (WasmSignal.Send -> wasm_signal_event_async ->
            // MAIN_THREAD_ASYNC_EM_ASM); the blocking variant deadlocked the deputy
            // here (iter61). main.js's cuo:gamescene-active listener calls
            // WasmViewport.ResizeGame.
            //
            // RE-ADDED 2026-05-30: this was briefly reverted after a "broke entering
            // Britannia" report — but that turned out to be ModernUO's
            // account-already-online behaviour (logging the SAME account a 2nd time
            // while the 1st session lingers hangs at "Verifying Account"), NOT this
            // emit. The operator confirmed the resize WORKS with a fresh account.
            WasmSignal.Send("gamescene-active");
#endif

            GameActions.RequestMobileStatus(world, world.Player);
            AsyncNetClient.Socket.Send_OpenChat("");

            AsyncNetClient.Socket.Send_SkillsRequest(world.Player);
            ObjectActionQueue.Instance.Enqueue(ObjectActionQueueItem.DoubleClick(world.Player | 0x8000_0000), ActionPriority.UseItem);

            if (Client.Game.UO.Version >= ClassicUO.Utility.ClientVersion.CV_306E)
                AsyncNetClient.Socket.Send_ClientType();

            if (Client.Game.UO.Version >= ClassicUO.Utility.ClientVersion.CV_305D)
                AsyncNetClient.Socket.Send_ClientViewRange(world.ClientViewRange);

            // Reset the global action cooldown here because, for some reason, immediately
            // sending multiple actions (e.g. reopening paperdoll and reopening containers)
            // results in the server telling the client it must wait to perform actions.
            GlobalActionCooldown.BeginCooldown();

            // FIRST-LOGIN preset (operator 2026-07-10, mirrors cuo): a profile that never
            // saved a gump layout (no gumps.xml — guests always, Discord users only before
            // their synced profile lands) gets the core windows pre-opened so the first
            // session doesn't start on a bare screen. Probe BEFORE ReadGumps.
            bool freshProfile = !System.IO.File.Exists(
                System.IO.Path.Combine(ProfileManager.ProfilePath, "gumps.xml"));

            List<Gump> gumps = ProfileManager.CurrentProfile.ReadGumps(
                world,
                ProfileManager.ProfilePath
            );

            if (gumps != null)
                foreach (Gump gump in gumps)
                    UIManager.Add(gump);

            if (freshProfile)
            {
                try
                {
                    GameActions.OpenPaperdoll(world, world.Player.Serial);
                    GameActions.OpenBackpack(world); // no-op if the layer item hasn't streamed yet
                    GameActions.OpenMiniMap(world);
                    GameActions.OpenStatusBar(world);
                    // Skills ride the Send_SkillsRequest issued above: flagging the
                    // request makes its response open the skills gump too.
                    world.SkillsRequested = true;
                    // Fixed layout (operator 2026-07-10): deferred pass snaps each
                    // async gump to its slot as it appears (see FirstLoginGumpPreset).
                    FirstLoginGumpPreset.Arm();
                }
                catch
                {
                    // cosmetic preset — never let it break world entry
                }
            }
        }
    }
}
