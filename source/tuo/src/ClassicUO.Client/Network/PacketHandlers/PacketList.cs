using ClassicUO.Game;
using ClassicUO.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class PacketList
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        int count = p.ReadUInt16BE();

        for (int i = 0; i < count; i++)
        {
            byte id = p.ReadUInt8();

            if (id == 0xF3)
                UpdateItemSA.Receive(world, ref p);
            else
            {
                Log.Warn($"Unknown packet ID: [0x{id:X2}] in 0xF7");
                break;
            }
        }
    }
}
