using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class ConfirmWalk
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        byte seq = p.ReadUInt8();
        byte noto = (byte)(p.ReadUInt8() & ~0x40);

        if (noto == 0 || noto >= 8)
            noto = 0x01;

        world.Player.NotorietyFlag = (NotorietyFlag)noto;
        world.Player.Walker.ConfirmWalk(seq);

        // AddToTile is already handled in Mobile.ProcessSteps when the step visually completes
        // Calling it again here was redundant and caused performance issues
    }
}
