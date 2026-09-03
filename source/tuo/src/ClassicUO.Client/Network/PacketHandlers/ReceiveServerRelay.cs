using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class ReceiveServerRelay
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.InGame)
            return;

        LoginHandshake.Instance?.HandleRelayServerPacket(ref p, Settings.GlobalSettings.IgnoreRelayIp);
    }
}
