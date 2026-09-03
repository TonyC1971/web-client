using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class Pathfinding
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        ushort x = p.ReadUInt16BE();
        ushort y = p.ReadUInt16BE();
        ushort z = p.ReadUInt16BE();

        world.Player.Pathfinder.WalkTo(x, y, z, 0);
    }
}
