using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class SetWeather
{
    public static void Receive(World world, ref StackDataReader p)
    {
        GameScene scene = Client.Game.GetScene<GameScene>();

        if (scene == null)
            return;

        var type = (WeatherType)p.ReadUInt8();

        if (world.Weather.CurrentWeather != type)
        {
            byte count = p.ReadUInt8();
            byte temp = p.ReadUInt8();

            world.Weather.Generate(type, count, temp);
            EventSink.InvokeOnSetWeather(null, new WeatherEventArgs(type, count, temp));
        }
    }
}
