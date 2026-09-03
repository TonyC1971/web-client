using ClassicUO.Game;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class AttackCharacter
{
    public static void Receive(World world, ref StackDataReader p)
    {
        uint serial = p.ReadUInt32BE();
        GameActions.SendCloseStatus(world, world.TargetManager.LastAttack);
        world.TargetManager.LastAttack = serial;
        GameActions.RequestMobileStatus(world, serial);
    }
}
