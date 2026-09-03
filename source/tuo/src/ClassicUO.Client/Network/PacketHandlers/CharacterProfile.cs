using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class CharacterProfile
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        uint serial = p.ReadUInt32BE();
        string header = p.ReadASCII();
        string footer = p.ReadUnicodeBE();

        string body = p.ReadUnicodeBE();

        UIManager.GetGump<ProfileGump>(serial)?.Dispose();

        UIManager.Add(
            new ProfileGump(world, serial, header, footer, body, serial == world.Player.Serial)
        );
    }
}
