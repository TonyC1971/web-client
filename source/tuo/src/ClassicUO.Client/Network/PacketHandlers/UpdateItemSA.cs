using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;
using ClassicUO.Network.PacketHandlers.Helpers;

namespace ClassicUO.Network.PacketHandlers;

internal static class UpdateItemSA
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        p.Skip(2);
        byte type = p.ReadUInt8();
        uint serial = p.ReadUInt32BE();
        ushort graphic = p.ReadUInt16BE();
        byte graphicInc = p.ReadUInt8();
        ushort amount = p.ReadUInt16BE();
        ushort unk = p.ReadUInt16BE();
        ushort x = p.ReadUInt16BE();
        ushort y = p.ReadUInt16BE();
        sbyte z = p.ReadInt8();
        var dir = (Direction)p.ReadUInt8();
        ushort hue = p.ReadUInt16BE();
        var flags = (Flags)p.ReadUInt8();
        ushort unk2 = p.ReadUInt16BE();

        if (serial != world.Player)
        {
            ObjectHelpers.UpdateGameObject(
                world,
                serial,
                graphic,
                graphicInc,
                amount,
                x,
                y,
                z,
                dir,
                hue,
                flags,
                unk,
                type,
                unk2
            );

            if (graphic == 0x2006 && ProfileManager.CurrentProfile.AutoOpenCorpses)
                world.Player.TryOpenCorpses();
        }
        else if (p[0] == 0xF7)
            PlayerHelpers.UpdatePlayer(world, serial, graphic, graphicInc, hue, flags, x, y, z, 0, dir);
    }
}
