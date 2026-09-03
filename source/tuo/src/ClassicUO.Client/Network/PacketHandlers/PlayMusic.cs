using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class PlayMusic
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (p.Length == 3) // Play Midi Music packet (0x6D, 0x10, index)
        {
            byte cmd = p.ReadUInt8();
            byte index = p.ReadUInt8();

            // Check for stop music packet (6D 1F FF)
            if (cmd == 0x1F && index == 0xFF)
                Client.Game.Audio.StopMusic();
            else
                Client.Game.Audio.PlayMusic(index);
        }
        else
        {
            ushort index = p.ReadUInt16BE();
            Client.Game.Audio.PlayMusic(index);
        }
    }
}
