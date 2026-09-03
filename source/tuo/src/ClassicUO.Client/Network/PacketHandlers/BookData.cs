using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class BookData
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        uint serial = p.ReadUInt32BE();
        ushort pageCnt = p.ReadUInt16BE();

        ModernBookGump gump = UIManager.GetGump<ModernBookGump>(serial);

        if (gump == null || gump.IsDisposed)
            return;

        for (int i = 0; i < pageCnt; i++)
        {
            int pageNum = p.ReadUInt16BE() - 1;
            gump.KnownPages.Add(pageNum);

            if (pageNum < gump.BookPageCount && pageNum >= 0)
            {
                ushort lineCnt = p.ReadUInt16BE();

                for (int line = 0; line < lineCnt; line++)
                {
                    int index = pageNum * ModernBookGump.MAX_BOOK_LINES + line;

                    if (index < gump.BookLines.Length)
                        gump.BookLines[index] = ModernBookGump.IsNewBook
                            ? p.ReadUTF8(true)
                            : p.ReadASCII();
                    else
                        Log.Error(
                            "BOOKGUMP: The server is sending a page number GREATER than the allowed number of pages in BOOK!"
                        );
                }

                if (lineCnt < ModernBookGump.MAX_BOOK_LINES)
                    for (int line = lineCnt; line < ModernBookGump.MAX_BOOK_LINES; line++)
                        gump.BookLines[pageNum * ModernBookGump.MAX_BOOK_LINES + line] =
                            string.Empty;
            }
            else
                Log.Error(
                    "BOOKGUMP: The server is sending a page number GREATER than the allowed number of pages in BOOK!"
                );
        }

        gump.ServerSetBookText();
    }
}
