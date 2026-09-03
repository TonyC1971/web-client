using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class MultiPlacement
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        bool allowGround = p.ReadBool();
        uint targID = p.ReadUInt32BE();
        byte flags = p.ReadUInt8();
        p.Seek(18);
        ushort multiID = p.ReadUInt16BE();
        ushort xOff = p.ReadUInt16BE();
        ushort yOff = p.ReadUInt16BE();
        ushort zOff = p.ReadUInt16BE();
        ushort hue = p.ReadUInt16BE();

        world.TargetManager.SetTargetingMulti(targID, multiID, xOff, yOff, zOff, hue);
    }
}
