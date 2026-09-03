using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class ClientTalk
{
    public static void Receive(World world, ref StackDataReader p)
    {
        switch (p.ReadUInt8())
        {
            case 0x78:
                break;

            case 0x3C:
                break;

            case 0x25:
                break;

            case 0x2E:
                break;
        }
    }
}
