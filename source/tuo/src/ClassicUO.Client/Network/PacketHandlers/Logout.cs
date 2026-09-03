using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class Logout
{
    public static void Receive(World world, ref StackDataReader p)
    {
        // http://docs.polserver.com/packets/index.php?Packet=0xD1

        if (
            Client.Game.GetScene<GameScene>().DisconnectionRequested
            && (
                world.ClientFeatures.Flags
                & CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON
            ) != 0
        )
        {
            if (p.ReadBool())
            {
                // client can disconnect
                AsyncNetClient.Socket.Disconnect().Wait();
                Client.Game.SetScene(new LoginScene(world));
            }
            else
                Log.Warn("0x1D - client asked to disconnect but server answered 'NO!'");
        }
    }
}
