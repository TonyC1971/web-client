using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class Ping
{
    public static void Receive(World world, ref StackDataReader p) =>
        AsyncNetClient.Socket.Statistics.PingReceived(p.ReadUInt8());
}
