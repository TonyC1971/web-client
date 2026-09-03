using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class Warmode
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        world.Player.InWarMode = p.ReadBool();
    }
}
