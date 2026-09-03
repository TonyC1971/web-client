using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class ClientVersion
{
    public static void Receive(World world, ref StackDataReader p) => AsyncNetClient.Socket.Send_ClientVersion(Settings.GlobalSettings.ClientVersion);
}
