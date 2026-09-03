using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class SecureTrading
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        byte type = p.ReadUInt8();
        uint serial = p.ReadUInt32BE();

        if (type == 0)
        {
            uint id1 = p.ReadUInt32BE();
            uint id2 = p.ReadUInt32BE();

            // standard client doesn't allow the trading system if one of the traders is invisible (=not sent by server)
            if (world.Get(id1) == null || world.Get(id2) == null)
                return;

            bool hasName = p.ReadBool();
            string name = string.Empty;

            if (hasName && p.Position < p.Length)
                name = p.ReadASCII();

            UIManager.Add(new TradingGump(world, serial, name, id1, id2));
        }
        else if (type == 1)
            UIManager.GetTradingGump(serial)?.Dispose();
        else if (type == 2)
        {
            uint id1 = p.ReadUInt32BE();
            uint id2 = p.ReadUInt32BE();

            TradingGump trading = UIManager.GetTradingGump(serial);

            if (trading != null)
            {
                trading.ImAccepting = id1 != 0;
                trading.HeIsAccepting = id2 != 0;

                trading.RequestUpdateContents();
            }
        }
        else if (type == 3 || type == 4)
        {
            TradingGump trading = UIManager.GetTradingGump(serial);

            if (trading != null)
            {
                if (type == 4)
                {
                    trading.Gold = p.ReadUInt32BE();
                    trading.Platinum = p.ReadUInt32BE();
                }
                else
                {
                    trading.HisGold = p.ReadUInt32BE();
                    trading.HisPlatinum = p.ReadUInt32BE();
                }
            }
        }
    }
}
