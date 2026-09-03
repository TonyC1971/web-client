using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class ServerListReceived
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.InGame)
            return;

        LoginHandshake.Instance?.ServerListReceived(ref p);
    }
}
