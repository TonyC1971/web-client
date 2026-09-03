using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class MapData
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        uint serial = p.ReadUInt32BE();

        MapGump gump = UIManager.GetGump<MapGump>(serial);

        if (gump != null)
            switch ((MapMessageType)p.ReadUInt8())
            {
                case MapMessageType.Add:
                    p.Skip(1);

                    ushort x = p.ReadUInt16BE();
                    ushort y = p.ReadUInt16BE();

                    gump.AddPin(x, y);

                    break;

                case MapMessageType.Insert:
                    break;
                case MapMessageType.Move:
                    break;
                case MapMessageType.Remove:
                    break;

                case MapMessageType.Clear:
                    gump.ClearContainer();

                    break;

                case MapMessageType.Edit:
                    break;

                case MapMessageType.EditResponse:
                    gump.SetPlotState(p.ReadUInt8());

                    break;
            }
    }
}
