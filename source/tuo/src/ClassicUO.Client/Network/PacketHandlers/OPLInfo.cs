using ClassicUO.Game;
using ClassicUO.IO;
using ClassicUO.Network.PacketHandlers.Helpers;

namespace ClassicUO.Network.PacketHandlers;

internal static class OPLInfo
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.ClientFeatures.TooltipsEnabled)
        {
            uint serial = p.ReadUInt32BE();
            uint revision = p.ReadUInt32BE();

            if (!world.OPL.IsRevisionEquals(serial, revision))
                SharedStore.AddMegaCliLocRequest(serial);
        }
    }
}
