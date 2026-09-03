using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class GraphicEffect
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        var type = (GraphicEffectType)p.ReadUInt8();

        if (type > GraphicEffectType.FixedFrom)
        {
            if (type == GraphicEffectType.ScreenFade && p[0] == 0x70)
            {
                p.Skip(8);
                ushort val = p.ReadUInt16BE();

                if (val > 4)
                    val = 4;

                Log.Warn("Effect not implemented");
            }

            return;
        }

        uint source = p.ReadUInt32BE();
        uint target = p.ReadUInt32BE();
        ushort graphic = p.ReadUInt16BE();
        ushort srcX = p.ReadUInt16BE();
        ushort srcY = p.ReadUInt16BE();
        sbyte srcZ = p.ReadInt8();
        ushort targetX = p.ReadUInt16BE();
        ushort targetY = p.ReadUInt16BE();
        sbyte targetZ = p.ReadInt8();
        byte speed = p.ReadUInt8();
        byte duration = p.ReadUInt8();
        ushort unk = p.ReadUInt16BE();
        bool fixedDirection = p.ReadBool();
        bool doesExplode = p.ReadBool();
        uint hue = 0;
        GraphicEffectBlendMode blendmode = 0;

        if (p[0] == 0x70) { }
        else
        {
            hue = p.ReadUInt32BE();
            blendmode = (GraphicEffectBlendMode)(p.ReadUInt32BE() % 7);

            if (p[0] == 0xC7)
            {
                ushort tileID = p.ReadUInt16BE();
                ushort explodeEffect = p.ReadUInt16BE();
                ushort explodeSound = p.ReadUInt16BE();
                uint serial = p.ReadUInt32BE();
                byte layer = p.ReadUInt8();
                p.Skip(2);
            }
        }

        world.SpawnEffect(
            type,
            source,
            target,
            graphic,
            (ushort)hue,
            srcX,
            srcY,
            srcZ,
            targetX,
            targetY,
            targetZ,
            speed,
            duration,
            fixedDirection,
            doesExplode,
            false,
            blendmode
        );
    }
}
