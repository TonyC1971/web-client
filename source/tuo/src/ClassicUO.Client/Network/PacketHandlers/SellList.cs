using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class SellList
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        Mobile vendor = world.Mobiles.Get(p.ReadUInt32BE());

        if (vendor == null)
            return;

        ushort countItems = p.ReadUInt16BE();

        if (countItems <= 0)
            return;

        ShopGump gump = UIManager.GetGump<ShopGump>(vendor);
        gump?.Dispose();
        ModernShopGump modernGump = UIManager.GetGump<ModernShopGump>(vendor);
        modernGump?.Dispose();

        if (ProfileManager.CurrentProfile.UseModernShopGump)
            modernGump = new ModernShopGump(world, vendor, false);
        else
            gump = new ShopGump(world, vendor, false, 100, 0);

        for (int i = 0; i < countItems; i++)
        {
            uint serial = p.ReadUInt32BE();
            ushort graphic = p.ReadUInt16BE();
            ushort hue = p.ReadUInt16BE();
            ushort amount = p.ReadUInt16BE();
            ushort price = p.ReadUInt16BE();
            string name = p.ReadASCII(p.ReadUInt16BE());
            bool fromcliloc = false;

            if (int.TryParse(name, out int clilocnum))
            {
                name = Client.Game.UO.FileManager.Clilocs.GetString(clilocnum);
                fromcliloc = true;
            }
            else if (string.IsNullOrEmpty(name))
            {
                bool success = world.OPL.TryGetNameAndData(serial, out name, out _);

                if (!success)
                    name = Client.Game.UO.FileManager.TileData.StaticData[graphic].Name;
            }

            //if (string.IsNullOrEmpty(item.Name))
            //    item.Name = name;
            BuySellAgent.Instance?.HandleSellPacket(vendor, serial, graphic, hue, amount, price);
            if (ProfileManager.CurrentProfile.UseModernShopGump)
                modernGump.AddItem
                (
                    world,
                    serial,
                    graphic,
                    hue,
                    amount,
                    price,
                    name,
                    fromcliloc
                );
            else
                gump.AddItem
                (
                    serial,
                    graphic,
                    hue,
                    amount,
                    price,
                    name,
                    fromcliloc
                );
        }

        if (ProfileManager.CurrentProfile.UseModernShopGump)
            UIManager.Add(modernGump);
        else
            UIManager.Add(gump);

        BuySellAgent.Instance?.HandleSellPacketFinished(vendor);
    }
}
