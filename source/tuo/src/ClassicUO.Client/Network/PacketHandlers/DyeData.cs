using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Renderer;

namespace ClassicUO.Network.PacketHandlers;

internal static class DyeData
{
    public static void Receive(World world, ref StackDataReader p)
    {
        uint serial = p.ReadUInt32BE();
        p.Skip(2);
        ushort graphic = p.ReadUInt16BE();

        ref readonly SpriteInfo gumpInfo = ref Client.Game.UO.Gumps.GetGump(0x0906);

        int x = (Client.Game.Window.ClientBounds.Width >> 1) - (gumpInfo.UV.Width >> 1);
        int y = (Client.Game.Window.ClientBounds.Height >> 1) - (gumpInfo.UV.Height >> 1);

        ColorPickerGump gump = UIManager.GetGump<ColorPickerGump>(serial);

        if (gump == null || gump.IsDisposed || gump.Graphic != graphic)
        {
            gump?.Dispose();

            gump = new ColorPickerGump(world, serial, graphic, x, y, null);

            UIManager.Add(gump);
        }
    }
}
