using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.PacketHandlers;

internal static class KrriosClientSpecial
{
    public static void Receive(World world, ref StackDataReader p)
    {
        byte type = p.ReadUInt8();

        switch (type)
        {
            case 0x00: // accepted
                Log.Trace("Krrios special packet accepted");
                world.WMapManager.SetACKReceived();
                world.WMapManager.SetEnable(true);

                break;

            case 0x01: // custom party info
            case 0x02: // guild track info
                bool locations = type == 0x01 || p.ReadBool();

                uint serial;

                while ((serial = p.ReadUInt32BE()) != 0)
                    if (locations)
                    {
                        ushort x = p.ReadUInt16BE();
                        ushort y = p.ReadUInt16BE();
                        byte map = p.ReadUInt8();
                        int hits = type == 1 ? 0 : p.ReadUInt8();

                        world.WMapManager.AddOrUpdate(
                            serial,
                            x,
                            y,
                            hits,
                            map,
                            type == 0x02,
                            null,
                            true
                        );

                        if (type == 0x02) //is guild member
                        {
                            Entity ent = world.Get(serial);
                            if (ent != null && !string.IsNullOrEmpty(ent.Name))
                                _ = FriendliesSQLManager.Instance.AddAsync(ent.Serial, ent.Name);
                        }
                    }

                world.WMapManager.RemoveUnupdatedWEntity();

                break;

            case 0x03: // runebook contents
                break;

            case 0x04: // guardline data
                break;

            case 0xF0:
                break;

            case 0xFE:

                Client.Game.EnqueueAction(5000, () =>
                {
                    Log.Info("Razor ACK sent");
                    AsyncNetClient.Socket.Send_RazorACK();
                });

                break;
        }
    }
}
