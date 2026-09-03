using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class UpdateItem
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        uint serial = p.ReadUInt32BE();
        ushort count = 0;
        byte graphicInc = 0;
        byte direction = 0;
        ushort hue = 0;
        byte flags = 0;
        byte type = 0;

        if ((serial & 0x80000000) != 0)
        {
            serial &= 0x7FFFFFFF;
            count = 1;
        }

        ushort graphic = p.ReadUInt16BE();

        if ((graphic & 0x8000) != 0)
        {
            graphic &= 0x7FFF;
            graphicInc = p.ReadUInt8();
        }

        if (count > 0)
            count = p.ReadUInt16BE();
        else
            count++;

        ushort x = p.ReadUInt16BE();

        if ((x & 0x8000) != 0)
        {
            x &= 0x7FFF;
            direction = 1;
        }

        ushort y = p.ReadUInt16BE();

        if ((y & 0x8000) != 0)
        {
            y &= 0x7FFF;
            hue = 1;
        }

        if ((y & 0x4000) != 0)
        {
            y &= 0x3FFF;
            flags = 1;
        }

        if (direction != 0)
            direction = p.ReadUInt8();

        sbyte z = p.ReadInt8();

        if (hue != 0)
            hue = p.ReadUInt16BE();

        if (flags != 0)
            flags = p.ReadUInt8();

        //if (graphic != 0x2006)
        //    graphic += graphicInc;

        if (graphic >= 0x4000)
            //graphic -= 0x4000;
            type = 2;

        Helpers.ObjectHelpers.UpdateGameObject(
            world,
            serial,
            graphic,
            graphicInc,
            count,
            x,
            y,
            z,
            (Direction)direction,
            hue,
            (Flags)flags,
            count,
            type,
            1
        );
    }
}
