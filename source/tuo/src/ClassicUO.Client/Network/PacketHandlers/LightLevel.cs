using System;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class LightLevel
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        byte level = p.ReadUInt8();

        if (level > 0x1E)
            level = 0x1E;

        world.Light.RealOverall = level;

        if (
            !ProfileManager.CurrentProfile.UseCustomLightLevel
            || ProfileManager.CurrentProfile.LightLevelType == 1
        )
            world.Light.Overall =
                ProfileManager.CurrentProfile.LightLevelType == 1
                    ? Math.Min(level, ProfileManager.CurrentProfile.LightLevel)
                    : level;
    }
}
