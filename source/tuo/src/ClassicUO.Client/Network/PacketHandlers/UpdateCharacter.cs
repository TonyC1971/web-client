using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class UpdateCharacter
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        uint serial = p.ReadUInt32BE();
        Mobile mobile = world.Mobiles.Get(serial);

        if (mobile == null)
            return;

        ushort graphic = p.ReadUInt16BE();
        ushort x = p.ReadUInt16BE();
        ushort y = p.ReadUInt16BE();
        sbyte z = p.ReadInt8();
        var direction = (Direction)p.ReadUInt8();
        ushort hue = p.ReadUInt16BE();
        var flags = (Flags)p.ReadUInt8();
        var notoriety = (NotorietyFlag)p.ReadUInt8();

        mobile.NotorietyFlag = notoriety;

        if (serial == world.Player)
        {
            mobile.Flags = flags;
            mobile.Graphic = graphic;
            mobile.CheckGraphicChange();
            mobile.FixHue(hue);
            // TODO: x,y,z, direction cause elastic effect, ignore 'em for the moment
        }
        else
            Helpers.ObjectHelpers.UpdateGameObject(world, serial, graphic, 0, 0, x, y, z, direction, hue, flags, 0, 1, 1);
    }
}
