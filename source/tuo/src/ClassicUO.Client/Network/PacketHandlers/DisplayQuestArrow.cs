using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class DisplayQuestArrow
{
    public static void Receive(World world, ref StackDataReader p)
    {
        bool display = p.ReadBool();
        ushort mx = p.ReadUInt16BE();
        ushort my = p.ReadUInt16BE();

        uint serial = 0;

        if (Client.Game.UO.Version >= Utility.ClientVersion.CV_7090)
            serial = p.ReadUInt32BE();

        QuestArrowGump arrow = UIManager.GetGump<QuestArrowGump>(serial);

        if (display)
        {
            if (arrow == null)
                UIManager.Add(new QuestArrowGump(world, serial, mx, my));
            else
                arrow.SetRelativePosition(mx, my);
        }
        else
        {
            if (arrow != null)
                arrow.Dispose();
        }
    }
}
