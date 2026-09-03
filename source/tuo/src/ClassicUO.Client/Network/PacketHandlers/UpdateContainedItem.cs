using System;
using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class UpdateContainedItem
{
    public static void Receive(World world, ref StackDataReader p)
    {
            if (!world.InGame)
            {
                return;
            }

            uint serial = p.ReadUInt32BE();
            ushort graphic = (ushort)(p.ReadUInt16BE() + p.ReadUInt8());
            ushort amount = Math.Max((ushort)1, p.ReadUInt16BE());
            ushort x = p.ReadUInt16BE();
            ushort y = p.ReadUInt16BE();

            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_6017)
            {
                p.Skip(1);
            }

            uint containerSerial = p.ReadUInt32BE();
            ushort hue = p.ReadUInt16BE();

            Helpers.ItemHelpers.AddItemToContainer(world, serial, graphic, amount, x, y, hue, containerSerial);
    }
}
