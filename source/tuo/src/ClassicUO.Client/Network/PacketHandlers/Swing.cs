using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class Swing
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (!world.InGame)
            return;

        p.Skip(1);

        uint attackers = p.ReadUInt32BE();

        if (attackers != world.Player)
            return;

        uint defenders = p.ReadUInt32BE();

        const int TIME_TURN_TO_LASTTARGET = 2000;

        if (
            world.TargetManager.LastAttack == defenders
            && world.Player.InWarMode
            && world.Player.Walker.LastStepRequestTime + TIME_TURN_TO_LASTTARGET < Time.Ticks
            && world.Player.Steps.Count == 0
        )
        {
            Mobile enemy = world.Mobiles.Get(defenders);

            if (enemy != null)
            {
                Direction pdir = DirectionHelper.GetDirectionAB(
                    world.Player.X,
                    world.Player.Y,
                    enemy.X,
                    enemy.Y
                );

                int x = world.Player.X;
                int y = world.Player.Y;
                sbyte z = world.Player.Z;

                if (
                    world.Player.Pathfinder.CanWalk(ref pdir, ref x, ref y, ref z)
                    && world.Player.Direction != pdir
                )
                    world.Player.Walk(pdir, false);
            }
        }
    }
}
