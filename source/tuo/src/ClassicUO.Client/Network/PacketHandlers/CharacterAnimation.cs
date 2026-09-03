using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class CharacterAnimation
{
    public static void Receive(World world, ref StackDataReader p)
    {
        Mobile mobile = world.Mobiles.Get(p.ReadUInt32BE());

        if (mobile == null)
            return;

        ushort action = p.ReadUInt16BE();
        ushort frame_count = p.ReadUInt16BE();
        ushort repeat_count = p.ReadUInt16BE();
        bool forward = !p.ReadBool();
        bool repeat = p.ReadBool();
        byte delay = p.ReadUInt8();

        mobile.SetAnimation(
            Mobile.GetReplacedObjectAnimation(mobile.Graphic, action),
            delay,
            (byte)frame_count,
            (byte)repeat_count,
            repeat,
            forward,
            true
        );
    }
}
