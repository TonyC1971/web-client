using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Renderer;

namespace ClassicUO.Network.PacketHandlers;

internal static class DisplayMap
{
    public static void Receive(World world, ref StackDataReader p)
    {
        uint serial = p.ReadUInt32BE();
        ushort gumpid = p.ReadUInt16BE();
        ushort startX = p.ReadUInt16BE();
        ushort startY = p.ReadUInt16BE();
        ushort endX = p.ReadUInt16BE();
        ushort endY = p.ReadUInt16BE();
        ushort width = p.ReadUInt16BE();
        ushort height = p.ReadUInt16BE();

        var gump = new MapGump(world, serial, gumpid, width, height);
        SpriteInfo multiMapInfo;

        if (p[0] == 0xF5 || Client.Game.UO.Version >= Utility.ClientVersion.CV_308Z)
        {
            ushort facet = 0;

            if (p[0] == 0xF5)
                facet = p.ReadUInt16BE();

            multiMapInfo = Client.Game.UO.MultiMaps.GetMap(facet, width, height, startX, startY, endX, endY);

            gump.MapInfos(startX, startY, endX, endY, facet);
        }
        else
        {
            multiMapInfo = Client.Game.UO.MultiMaps.GetMap(null, width, height, startX, startY, endX, endY);

            gump.MapInfos(startX, startY, endX, endY);
        }

        if (multiMapInfo.Texture != null)
            gump.SetMapTexture(multiMapInfo.Texture);

        UIManager.Add(gump);

        Item it = world.Items.Get(serial);

        if (it != null)
            it.Opened = true;
    }
}
