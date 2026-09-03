using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class TipWindow
{
    public static void Receive(World world, ref StackDataReader p)
    {
        byte flag = p.ReadUInt8();

        if (flag == 1)
            return;

        uint tip = p.ReadUInt32BE();
        string str = p.ReadASCII(p.ReadUInt16BE())?.Replace('\r', '\n');

        int x = 20;
        int y = 20;

        if (flag == 0)
        {
            x = 200;
            y = 100;
        }

        UIManager.Add(new TipNoticeGump(world, tip, flag, str) { X = x, Y = y });
    }
}
