using ClassicUO.Game;
using ClassicUO.IO;
using ClassicUO.Utility.Platforms;

namespace ClassicUO.Network.PacketHandlers;

internal static class OpenUrl
{
    public static void Receive(World world, ref StackDataReader p)
    {
        string url = p.ReadASCII();

        if (!string.IsNullOrEmpty(url))
            PlatformHelper.LaunchBrowser(url);
    }
}
