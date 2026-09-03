using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class NewHealthBarUpdate
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        if (p[0] == 0x16 && Client.Game.UO.Version < Utility.ClientVersion.CV_500A)
            return;

        Mobile mobile = world.Mobiles.Get(p.ReadUInt32BE());

        if (mobile == null)
            return;

        ushort count = p.ReadUInt16BE();

        for (int i = 0; i < count; i++)
        {
            ushort type = p.ReadUInt16BE();
            bool enabled = p.ReadBool();

            if (type == 1)
            {
                if (enabled)
                {
                    if (Client.Game.UO.Version >= Utility.ClientVersion.CV_7000)
                        mobile.SetSAPoison(true);
                    else
                        mobile.Flags |= Flags.Poisoned;
                }
                else
                {
                    if (Client.Game.UO.Version >= Utility.ClientVersion.CV_7000)
                        mobile.SetSAPoison(false);
                    else
                        mobile.Flags &= ~Flags.Poisoned;
                }

                BandageManager.Instance.SetPoisoned(mobile.Serial, enabled);
            }
            else if (type == 2)
            {
                if (enabled)
                    mobile.Flags |= Flags.YellowBar;
                else
                    mobile.Flags &= ~Flags.YellowBar;
            }
            else if (type == 3)
            {
                // ???
            }
        }
    }
}
