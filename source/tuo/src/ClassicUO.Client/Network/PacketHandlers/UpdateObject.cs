using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;

namespace ClassicUO.Network.PacketHandlers;

internal static class UpdateObject
{
    public static void Receive(World world, ref StackDataReader p)
    {
        if (world.Player == null)
            return;

        uint serial = p.ReadUInt32BE();
        ushort graphic = p.ReadUInt16BE();
        ushort x = p.ReadUInt16BE();
        ushort y = p.ReadUInt16BE();
        sbyte z = p.ReadInt8();
        var direction = (Direction)p.ReadUInt8();
        ushort hue = p.ReadUInt16BE();
        var flags = (Flags)p.ReadUInt8();
        var notoriety = (NotorietyFlag)p.ReadUInt8();
        bool oldDead = false;
        //bool alreadyExists =world.Get(serial) != null;

        if (serial == world.Player)
        {
            oldDead = world.Player.IsDead;
            world.Player.Graphic = graphic;
            world.Player.CheckGraphicChange();
            world.Player.FixHue(hue);
            world.Player.Flags = flags;
        }
        else
            Helpers.ObjectHelpers.UpdateGameObject(world, serial, graphic, 0, 0, x, y, z, direction, hue, flags, 0, 0,
                1);

        Entity obj = world.Get(serial);

        if (obj == null)
            return;

        if (!obj.IsEmpty)
        {
            LinkedObject o = obj.Items;

            while (o != null)
            {
                LinkedObject next = o.Next;
                var it = (Item)o;

                if (!it.Opened && it.Layer != Layer.Backpack)
                    world.RemoveItem(it.Serial, true);

                o = next;
            }
        }

        if (SerialHelper.IsMobile(serial) && obj is Mobile mob)
        {
            mob.NotorietyFlag = notoriety;

            UIManager.GetGump<PaperDollGump>(serial)?.RequestUpdateContents();
            UIManager.GetGump<ModernPaperdoll>(serial)?.RequestUpdateContents();
        }

        if (p[0] != 0x78)
            p.Skip(6);

        uint itemSerial = p.ReadUInt32BE();

        while (itemSerial != 0 && p.Position < p.Length)
        {
            //if (!SerialHelper.IsItem(itemSerial))
            //    break;

            ushort itemGraphic = p.ReadUInt16BE();
            byte layer = p.ReadUInt8();
            ushort item_hue = 0;

            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_70331)
                item_hue = p.ReadUInt16BE();
            else if ((itemGraphic & 0x8000) != 0)
            {
                itemGraphic &= 0x7FFF;
                item_hue = p.ReadUInt16BE();
            }

            Item item = world.GetOrCreateItem(itemSerial);
            item.Graphic = itemGraphic;
            item.FixHue(item_hue);
            item.Amount = 1;
            world.RemoveItemFromContainer(item);
            item.Container = serial;
            item.Layer = (Layer)layer;

            if (item.Layer == Layer.Mount && obj is Mobile parMob)
                parMob.Mount = item;

            item.CheckGraphicChange();

            obj.PushToBack(item);

            itemSerial = p.ReadUInt32BE();
        }

        if (serial == world.Player)
        {
            if (oldDead != world.Player.IsDead)
            {
                if (world.Player.IsDead)
                    // NOTE: This packet causes some weird issue on sphere servers.
                    //       When the character dies, this packet trigger a "reset" and
                    //       somehow it messes up the packet reading server side
                    //NetClient.Socket.Send_DeathScreen();
                    world.ChangeSeason(ClassicUO.Game.Managers.Season.Desolation, 42);
                else
                    world.ChangeSeason(world.OldSeason, world.OldMusicIndex);
            }

            UIManager.GetGump<PaperDollGump>(serial)?.RequestUpdateContents();
            UIManager.GetGump<ModernPaperdoll>(serial)?.RequestUpdateContents();
            GameActions.RequestEquippedOPL(world);

            world.Player.UpdateAbilities();
        }
    }
}
