using System;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class UpdateContainedItems
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        ushort count = p.ReadUInt16BE();

        for (int i = 0; i < count; i++)
        {
            uint serial = p.ReadUInt32BE();
            ushort graphic = (ushort)(p.ReadUInt16BE() + p.ReadUInt8());
            ushort amount = Math.Max(p.ReadUInt16BE(), (ushort)1);
            ushort x = p.ReadUInt16BE();
            ushort y = p.ReadUInt16BE();

            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_6017)
                p.Skip(1);

            uint containerSerial = p.ReadUInt32BE();
            ushort hue = p.ReadUInt16BE();

            if (i == 0)
            {
                Entity container = world.Get(containerSerial);

                if (container != null)
                    Helpers.ItemHelpers.ClearContainerAndRemoveItems(world, container, container.Graphic == 0x2006);
            }

            Helpers.ItemHelpers.AddItemToContainer(world, serial, graphic, amount, x, y, hue, containerSerial);
        }
    }
}
