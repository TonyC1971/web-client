using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class OpenBook
{
    public static void Receive(World world, ref StackDataReader p)
    {
        uint serial = p.ReadUInt32BE();
        bool oldpacket = p[0] == 0x93;
        bool editable = p.ReadBool();

        if (!oldpacket)
            editable = p.ReadBool();
        else
            p.Skip(1);

        ModernBookGump bgump = UIManager.GetGump<ModernBookGump>(serial);

        if (bgump == null || bgump.IsDisposed)
        {
            ushort page_count = p.ReadUInt16BE();
            string title = oldpacket
                ? p.ReadUTF8(60, true)
                : p.ReadUTF8(p.ReadUInt16BE(), true);
            string author = oldpacket
                ? p.ReadUTF8(30, true)
                : p.ReadUTF8(p.ReadUInt16BE(), true);

            UIManager.Add(
                new ModernBookGump(world, serial, page_count, title, author, editable, oldpacket) { X = 100, Y = 100 }
            );

            AsyncNetClient.Socket.Send_BookPageDataRequest(serial, 1);
        }
        else
        {
            p.Skip(2);
            bgump.IsEditable = editable;
            bgump.SetTile(
                oldpacket ? p.ReadUTF8(60, true) : p.ReadUTF8(p.ReadUInt16BE(), true),
                editable
            );
            bgump.SetAuthor(
                oldpacket ? p.ReadUTF8(30, true) : p.ReadUTF8(p.ReadUInt16BE(), true),
                editable
            );
            bgump.UseNewHeader = !oldpacket;
            bgump.SetInScreen();
            bgump.BringOnTop();
        }
    }
}
