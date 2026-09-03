using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class EquipItem
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        uint serial = p.ReadUInt32BE();

        Item item = world.GetOrCreateItem(serial);

        if (item.Graphic != 0 && item.Layer != Layer.Backpack)
            //ClearContainerAndRemoveItems(item);
            world.RemoveItemFromContainer(item);

        if (SerialHelper.IsValid(item.Container))
        {
            UIManager.GetGump<ContainerGump>(item.Container)?.RequestUpdateContents();

            UIManager.GetGump<PaperDollGump>(item.Container)?.RequestUpdateContents();
            UIManager.GetGump<ModernPaperdoll>(item.Container)?.RequestUpdateContents();
        }

        item.Graphic = (ushort)(p.ReadUInt16BE() + p.ReadInt8());
        item.Layer = (Layer)p.ReadUInt8();
        item.Container = p.ReadUInt32BE();
        item.FixHue(p.ReadUInt16BE());
        item.Amount = 1;

        Entity entity = world.Get(item.Container);

        entity?.PushToBack(item);

        if (item.Layer == Layer.Mount && entity is Mobile mob)
            mob.Mount = item;

        if (item.Layer >= Layer.ShopBuyRestock && item.Layer <= Layer.ShopSell)
        {
            //item.Clear();
        }
        else if (SerialHelper.IsValid(item.Container) && item.Layer < Layer.Mount)
        {
            UIManager.GetGump<PaperDollGump>(item.Container)?.RequestUpdateContents();
            UIManager.GetGump<ModernPaperdoll>(item.Container)?.RequestUpdateContents();
        }

        if (
            entity == world.Player
            && (item.Layer == Layer.OneHanded || item.Layer == Layer.TwoHanded)
        )
            world.Player?.UpdateAbilities();
    }
}
