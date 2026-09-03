using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class BuyList
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        Item container = world.Items.Get(p.ReadUInt32BE());

        if (container == null)
            return;

        Mobile vendor = world.Mobiles.Get(container.Container);

        if (vendor == null)
            return;

        ShopGump gump = UIManager.GetGump<ShopGump>();
        ModernShopGump modernGump = UIManager.GetGump<ModernShopGump>();

        if (ProfileManager.CurrentProfile.UseModernShopGump)
        {
            modernGump?.Dispose();
            UIManager.Add(modernGump = new ModernShopGump(world, vendor, true));
        }
        else
        {
            if (gump != null && (gump.LocalSerial != vendor || !gump.IsBuyGump))
            {
                gump.Dispose();
                gump = null;
            }

            if (gump == null)
            {
                gump = new ShopGump(world, vendor, true, 150, 5);
                UIManager.Add(gump);
            }
        }

        if (container.Layer == Layer.ShopBuyRestock || container.Layer == Layer.ShopBuy)
        {
            byte count = p.ReadUInt8();

            LinkedObject first = container.Items;

            if (first == null)
                return;

            bool reverse = false;

            if (container.Graphic == 0x2AF8) //hardcoded logic in original client that we must match
                //sort the contents
                first = container.SortContents<Item>((x, y) => x.X - y.X);
            else
            {
                //skip to last item and read in reverse later
                reverse = true;

                while (first?.Next != null)
                    first = first.Next;
            }

            for (int i = 0; i < count; i++)
            {
                if (first == null)
                    break;

                var it = (Item)first;

                it.Price = p.ReadUInt32BE();
                byte nameLen = p.ReadUInt8();
                string name = p.ReadASCII(nameLen);

                if (world.OPL.TryGetNameAndData(it.Serial, out string s, out _))
                    it.Name = s;
                else if (int.TryParse(name, out int cliloc))
                    it.Name = Client.Game.UO.FileManager.Clilocs.Translate(
                        cliloc,
                        $"\t{it.ItemData.Name}: \t{it.Amount}",
                        true
                    );
                else if (string.IsNullOrEmpty(name))
                    it.Name = it.ItemData.Name;
                else
                    it.Name = name;

                if (reverse)
                    first = first.Previous;
                else
                    first = first.Next;
            }
        }
    }
}
