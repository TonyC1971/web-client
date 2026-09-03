using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class CorpseEquipment
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        uint serial = p.ReadUInt32BE();
        Entity corpse = world.Get(serial);

        if (corpse == null)
            return;

        // if it's not a corpse we should skip this [?]
        if (corpse.Graphic != 0x2006)
            return;

        var layer = (Layer)p.ReadUInt8();

        while (layer != Layer.Invalid && p.Position < p.Length)
        {
            uint item_serial = p.ReadUInt32BE();

            if (layer - 1 != Layer.Backpack)
            {
                Item item = world.GetOrCreateItem(item_serial);

                world.RemoveItemFromContainer(item);
                item.Container = serial;
                item.Layer = layer - 1;
                corpse.PushToBack(item);
            }

            layer = (Layer)p.ReadUInt8();
        }
    }
}
