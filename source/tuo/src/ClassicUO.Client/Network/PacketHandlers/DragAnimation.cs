using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class DragAnimation
{
    public static void Receive(World world, ref StackDataReader p)
    {
        ushort graphic = p.ReadUInt16BE();
        graphic += p.ReadUInt8();
        ushort hue = p.ReadUInt16BE();
        ushort count = p.ReadUInt16BE();
        uint source = p.ReadUInt32BE();
        ushort sourceX = p.ReadUInt16BE();
        ushort sourceY = p.ReadUInt16BE();
        sbyte sourceZ = p.ReadInt8();
        uint dest = p.ReadUInt32BE();
        ushort destX = p.ReadUInt16BE();
        ushort destY = p.ReadUInt16BE();
        sbyte destZ = p.ReadInt8();

        if (graphic == 0x0EED)
            graphic = 0x0EEF;
        else if (graphic == 0x0EEA)
            graphic = 0x0EEC;
        else if (graphic == 0x0EF0)
            graphic = 0x0EF2;

        Mobile entity = world.Mobiles.Get(source);

        if (entity == null)
            source = 0;
        else
        {
            sourceX = entity.X;
            sourceY = entity.Y;
            sourceZ = entity.Z;
        }

        Mobile destEntity = world.Mobiles.Get(dest);

        if (destEntity == null)
            dest = 0;
        else
        {
            destX = destEntity.X;
            destY = destEntity.Y;
            destZ = destEntity.Z;
        }

        world.SpawnEffect(
            !SerialHelper.IsValid(source) || !SerialHelper.IsValid(dest)
                ? GraphicEffectType.Moving
                : GraphicEffectType.DragEffect,
            source,
            dest,
            graphic,
            hue,
            sourceX,
            sourceY,
            sourceZ,
            destX,
            destY,
            destZ,
            5,
            5000,
            true,
            false,
            false,
            GraphicEffectBlendMode.Normal
        );
    }
}
