using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class TextEntryDialog
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        uint serial = p.ReadUInt32BE();
        byte parentID = p.ReadUInt8();
        byte buttonID = p.ReadUInt8();

        ushort textLen = p.ReadUInt16BE();
        string text = p.ReadASCII(textLen);

        bool haveCancel = p.ReadBool();
        byte variant = p.ReadUInt8();
        uint maxLength = p.ReadUInt32BE();

        ushort descLen = p.ReadUInt16BE();
        string desc = p.ReadASCII(descLen);

        var gump = new TextEntryDialogGump(
            world,
            serial,
            143,
            172,
            variant,
            (int)maxLength,
            text,
            desc,
            buttonID,
            parentID
        ) { CanCloseWithRightClick = haveCancel };

        UIManager.Add(gump);
    }
}
