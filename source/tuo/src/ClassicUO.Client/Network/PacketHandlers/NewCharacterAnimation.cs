using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class NewCharacterAnimation
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        Mobile mobile = world.Mobiles.Get(p.ReadUInt32BE());

        if (mobile == null)
            return;

        ushort type = p.ReadUInt16BE();
        ushort action = p.ReadUInt16BE();
        byte mode = p.ReadUInt8();
        byte group = Mobile.GetObjectNewAnimation(mobile, type, action, mode);

        mobile.SetAnimation(
            group,
            repeatCount: 1,
            repeat: (type == 1 || type == 2) && mobile.Graphic == 0x0015,
            forward: true,
            fromServer: true
        );
    }
}
