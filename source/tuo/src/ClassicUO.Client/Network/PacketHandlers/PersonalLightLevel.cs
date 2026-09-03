using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class PersonalLightLevel
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        if (world.Player == p.ReadUInt32BE())
        {
            byte level = p.ReadUInt8();

            if (level > 0x1E)
                level = 0x1E;

            world.Light.RealPersonal = level;

            if (!ProfileManager.CurrentProfile.UseCustomLightLevel)
                world.Light.Personal = level;
        }
    }
}
