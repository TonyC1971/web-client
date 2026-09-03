using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class Season
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        byte season = p.ReadUInt8();
        byte music = p.ReadUInt8();

        if (season > 4)
            season = 0;

        // Apply season filter
        world.RealSeason = (Game.Managers.Season)season;
        Game.Managers.Season filteredSeason = SeasonFilter.Instance.ApplyFilter((Game.Managers.Season)season);

        if (world.Player.IsDead && filteredSeason == Game.Managers.Season.Desolation)
            return;

        world.OldSeason = (Game.Managers.Season)season;
        world.OldMusicIndex = music;

        if (world.Season == Game.Managers.Season.Desolation)
            world.OldMusicIndex = 42;

        world.ChangeSeason(filteredSeason, music);
    }
}
