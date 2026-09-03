using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace ClassicUO.Wasm;

// JSExport bridge for the vertical rail's JS-macro API (window.UORailBridge in
// rail.js). main.js wires window.UORailBridge.* to these exports once in-world.
// Methods return Task / Task<T> because Mercury MT forbids synchronous JS->C#
// crossings (see WasmViewport.cs). NEVER poll these during boot. Game access
// lives in ClassicUO.Game.RailBridgeApi (inside ClassicUO.Client).
internal static partial class WasmRailBridge
{
    [JSExport]
    internal static Task<string> GetPlayer()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetPlayerJson());
    }

    // Player-metrics telemetry: gameplay-counter deltas since the last poll
    // (tiles walked, distinct NPCs seen). main.js polls this every few seconds
    // in-world and feeds the deltas to the metrics heartbeat. Pure read.
    [JSExport]
    internal static Task<string> CollectMetrics()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.CollectMetricsJson());
    }

    // Gameview screenshot capture (operator 2026-06-23). Returns a base64 PNG of the
    // WORLD render target (gameview only — no gumps/UI/cursor), or "" if not in-game.
    // main.js calls this on PrintScreen and keeps the last 5 in an OPFS ring-buffer;
    // the player picks which to upload (never automatic).
    [JSExport]
    internal static Task<string> CaptureGameview()
    {
        return Task.FromResult(ClassicUO.Game.ScreenshotBridge.CaptureGameviewB64());
    }

    [JSExport]
    internal static Task SysMessage(string text, int hue)
    {
        ClassicUO.Game.RailBridgeApi.SysMessage(text, hue);
        return Task.CompletedTask;
    }

    // Sprite smoothing (texel-AA, operator goal 2026-07-10): the rail's Game
    // Options slider drives the SAME profile fields as the in-game OptionsGump,
    // so both UIs stay in sync and the choice persists via the profile sync.
    // level 0..100 (0 = off), full = per-tap hue-resolved interior smoothing.
    [JSExport]
    internal static Task SetSpriteSmoothing(int level, bool full)
    {
        ClassicUO.Game.RailBridgeApi.SetSpriteSmoothing(level, full);
        return Task.CompletedTask;
    }

    // "level|full" for the rail panel to show current state when it opens.
    [JSExport]
    internal static Task<string> GetSpriteSmoothing()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetSpriteSmoothing());
    }

    [JSExport]
    internal static Task Say(string text)
    {
        ClassicUO.Game.RailBridgeApi.Say(text);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task UseItem(double serial)
    {
        ClassicUO.Game.RailBridgeApi.UseItem(serial);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<bool> Target(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.Target(serial));
    }

    [JSExport]
    internal static Task<bool> TargetSelf()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.TargetSelf());
    }

    [JSExport]
    internal static Task<bool> TargetLast()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.TargetLast());
    }

    [JSExport]
    internal static Task UseSkill(int index)
    {
        ClassicUO.Game.RailBridgeApi.UseSkill(index);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task CastSpell(int index)
    {
        ClassicUO.Game.RailBridgeApi.CastSpell(index);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<bool> CancelTarget()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.CancelTarget());
    }

    [JSExport]
    internal static Task OpenNativeOptions()
    {
        ClassicUO.Game.RailBridgeApi.OpenNativeOptions();
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> GetProfile()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetProfileJson());
    }

    [JSExport]
    internal static Task SetSetting(string name, string value)
    {
        ClassicUO.Game.RailBridgeApi.SetProfileValue(name, value);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> GetMacros()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetMacrosJson());
    }

    [JSExport]
    internal static Task<string> GetHuePalette()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetHuePaletteJson());
    }

    [JSExport]
    internal static Task<bool> AddMacro(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.AddMacro(name));
    }

    [JSExport]
    internal static Task<bool> DeleteMacro(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.DeleteMacro(name));
    }

    [JSExport]
    internal static Task<string> GetMacroCatalog()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetMacroCatalogJson());
    }

    [JSExport]
    internal static Task<bool> AddMacroAction(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.AddMacroAction(name));
    }

    [JSExport]
    internal static Task<bool> RemoveMacroActionAt(string name, int index)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.RemoveMacroActionAt(name, index));
    }

    [JSExport]
    internal static Task<bool> SetMacroAction(string name, int index, int code, int sub, string text)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.SetMacroAction(name, index, code, sub, text));
    }

    [JSExport]
    internal static Task<bool> SetMacroKey(string name, string token, bool alt, bool ctrl, bool shift)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.SetMacroKey(name, token, alt, ctrl, shift));
    }

    [JSExport]
    internal static Task<bool> ClearMacroKey(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ClearMacroKey(name));
    }

    [JSExport]
    internal static Task<bool> RenameMacro(string oldName, string newName)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.RenameMacro(oldName, newName));
    }

    // Shard round-trip latency in ms for the rail readout; -1 when there is no reading.
    [JSExport]
    internal static Task<int> GetPing()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetPing());
    }

    // ── Agents engine — real item read/move/equip primitives ────────────────
    [JSExport]
    internal static Task<string> GetEquippedItems()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetEquippedItemsJson());
    }

    [JSExport]
    internal static Task<string> GetContainerItems(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetContainerItemsJson(serial));
    }

    [JSExport]
    internal static Task<double> GetBackpackSerial()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetBackpackSerial());
    }

    [JSExport]
    internal static Task<bool> EquipItem(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.EquipItem(serial));
    }

    [JSExport]
    internal static Task<bool> MoveItem(double serial, double dest, int amount)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.MoveItem(serial, dest, amount));
    }

    [JSExport]
    internal static Task<bool> GrabItem(double serial, int amount, double bag)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GrabItem(serial, amount, bag));
    }

    // ── Target picker (autoloot "+") + real item art ───────────────────────
    [JSExport]
    internal static Task<bool> RequestTarget()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.RequestRailTarget());
    }

    [JSExport]
    internal static Task<string> PollTarget()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.PollRailTargetJson());
    }

    [JSExport]
    internal static Task CancelRailTarget()
    {
        ClassicUO.Game.RailBridgeApi.CancelRailTarget();
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> GetItemArt(int graphic)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetItemArtJson(graphic));
    }

    // Durability tracker (Agents > Durability) — worn gear + OPL durability.
    [JSExport]
    internal static Task<string> GetEquipmentDurability()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetEquipmentDurabilityJson());
    }

    // ── Lists agent (Friends / Enemies) ─────────────────────────────────────
    [JSExport]
    internal static Task<bool> ListsHasFriends()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ListsHasFriends());
    }

    [JSExport]
    internal static Task<string> GetFriends()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetFriendsJson());
    }

    [JSExport]
    internal static Task<bool> AddFriend(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.AddFriend(serial));
    }

    [JSExport]
    internal static Task<bool> RemoveFriend(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.RemoveFriendBySerial(serial));
    }

    [JSExport]
    internal static Task<string> GetIgnored()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetIgnoredJson());
    }

    [JSExport]
    internal static Task<bool> AddIgnore(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.AddIgnore(serial));
    }

    [JSExport]
    internal static Task<bool> RemoveIgnore(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.RemoveIgnore(name));
    }

    // UOAM live peers → drawn on the in-game WorldMap (Agents > World Map).
    [JSExport]
    internal static Task SetUoamPeers(string data)
    {
        ClassicUO.Game.RailBridgeApi.SetUoamPeers(data);
        return Task.CompletedTask;
    }

    // ── Filters agent (sound filtering, TazUO SoundFilterManager) ────────────
    [JSExport]
    internal static Task<bool> FiltersHasEngine()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.FiltersHasEngine());
    }

    [JSExport]
    internal static Task<string> GetRecentSounds()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetRecentSoundsJson());
    }

    [JSExport]
    internal static Task<string> GetSoundFilters()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetSoundFiltersJson());
    }

    [JSExport]
    internal static Task AddSoundFilter(int id)
    {
        ClassicUO.Game.RailBridgeApi.AddSoundFilter(id);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task RemoveSoundFilter(int id)
    {
        ClassicUO.Game.RailBridgeApi.RemoveSoundFilter(id);
        return Task.CompletedTask;
    }

    // ── Chat agent (native UO conference system) ─────────────────────────────
    [JSExport]
    internal static Task<string> GetChatState()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetChatStateJson());
    }

    [JSExport]
    internal static Task<bool> ChatRegisterName(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ChatRegisterName(name));
    }

    [JSExport]
    internal static Task<bool> ChatJoin(string name, string password)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ChatJoinChannel(name, password));
    }

    [JSExport]
    internal static Task<bool> ChatLeave()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ChatLeaveChannel());
    }

    [JSExport]
    internal static Task<bool> ChatSend(string msg)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ChatSend(msg));
    }

    // ── Perception / navigation / UI verbs (operator 2026-06-11) ─────────────
    // Journal read, world scan, target-cursor detect, pathfinding, gump read/reply,
    // and the "natural human" mouse path. Read verbs pure; action verbs reuse the
    // native client paths. All gated by the rail allow-list + per-shard policy.
    [JSExport]
    internal static Task<string> GetJournal(int max)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetJournalJson(max));
    }

    [JSExport]
    internal static Task<string> ScanWorld(int range, int kind)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ScanWorldJson(range, kind));
    }

    [JSExport]
    internal static Task<bool> IsTargeting()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.IsTargeting());
    }

    [JSExport]
    internal static Task<bool> WalkTo(int x, int y, int distance)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.WalkTo(x, y, distance));
    }

    [JSExport]
    internal static Task<bool> StopWalk()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.StopWalk());
    }

    [JSExport]
    internal static Task<string> GetGumps()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetGumpsJson());
    }

    [JSExport]
    internal static Task<bool> GumpReply(double gumpServerSerial, int button)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GumpReply(gumpServerSerial, button));
    }

    [JSExport]
    internal static Task MouseMove(int x, int y)
    {
        ClassicUO.Game.RailBridgeApi.MouseMove(x, y);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task MouseClick(bool rightButton)
    {
        ClassicUO.Game.RailBridgeApi.MouseClick(rightButton);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task MouseDoubleClick(bool rightButton)
    {
        ClassicUO.Game.RailBridgeApi.MouseDoubleClick(rightButton);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> ObjectAtCursor()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ObjectAtCursorJson());
    }

    // ── LegionScript API completion — CUO subset (2026-07-25) ─────────────────
    [JSExport]
    internal static Task SetWarMode(bool enabled)
    {
        ClassicUO.Game.RailBridgeApi.SetWarMode(enabled);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task ToggleAbility(string ability)
    {
        ClassicUO.Game.RailBridgeApi.ToggleAbility(ability);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<bool> PrimaryAbilityActive()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.PrimaryAbilityActive());
    }

    [JSExport]
    internal static Task<bool> SecondaryAbilityActive()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.SecondaryAbilityActive());
    }

    [JSExport]
    internal static Task<string> KnownAbilityNames()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.KnownAbilityNamesJson());
    }

    [JSExport]
    internal static Task<string> ActiveBuffs()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ActiveBuffsJson());
    }

    [JSExport]
    internal static Task<bool> BuffExists(string name)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.BuffExists(name));
    }

    [JSExport]
    internal static Task SetSkillLock(string skill, string mode)
    {
        ClassicUO.Game.RailBridgeApi.SetSkillLock(skill, mode);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task SetStatLock(string stat, string mode)
    {
        ClassicUO.Game.RailBridgeApi.SetStatLock(stat, mode);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task Virtue(string virtue)
    {
        ClassicUO.Game.RailBridgeApi.Virtue(virtue);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task TrackingArrow(int x, int y, double identifier)
    {
        ClassicUO.Game.RailBridgeApi.TrackingArrow(x, y, identifier);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> ClearLeftHand()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ClearLeftHand());
    }

    [JSExport]
    internal static Task<string> ClearRightHand()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ClearRightHand());
    }

    [JSExport]
    internal static Task Mount(double serial)
    {
        ClassicUO.Game.RailBridgeApi.Mount(serial);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task Dismount()
    {
        ClassicUO.Game.RailBridgeApi.Dismount();
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task ToggleFly()
    {
        ClassicUO.Game.RailBridgeApi.ToggleFly();
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task Rename(double serial, string name)
    {
        ClassicUO.Game.RailBridgeApi.Rename(serial, name);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<double> GetHeldItem()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetHeldItem());
    }

    [JSExport]
    internal static Task PickUpToCursor(double serial, int amt)
    {
        ClassicUO.Game.RailBridgeApi.PickUpToCursor(serial, amt);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task DropFromCursor(double serial, int x, int y, int z, double container)
    {
        ClassicUO.Game.RailBridgeApi.DropFromCursor(serial, x, y, z, container);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task MoveItemOffset(double serial, int amt, int x, int y, int z, bool osi)
    {
        ClassicUO.Game.RailBridgeApi.MoveItemOffset(serial, amt, x, y, z, osi);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task RequestOPLData(string serialsCsv)
    {
        ClassicUO.Game.RailBridgeApi.RequestOPLData(serialsCsv);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> ItemNameAndProps(double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.ItemNameAndProps(serial));
    }

    [JSExport]
    internal static Task ContextMenu(double serial, int entry)
    {
        ClassicUO.Game.RailBridgeApi.ContextMenu(serial, entry);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task CloseContextMenus()
    {
        ClassicUO.Game.RailBridgeApi.CloseContextMenus();
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<int> GetMap()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetMap());
    }

    [JSExport]
    internal static Task<string> GetTile(int x, int y)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetTileJson(x, y));
    }

    [JSExport]
    internal static Task<string> GetStaticsAt(int x, int y)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetStaticsAtJson(x, y));
    }

    [JSExport]
    internal static Task<string> GetStaticsInArea(int x1, int y1, int x2, int y2)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetStaticsInAreaJson(x1, y1, x2, y2));
    }

    [JSExport]
    internal static Task<double> GetPartyLeader()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetPartyLeader());
    }

    [JSExport]
    internal static Task<string> GetPartyMemberSerials()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetPartyMemberSerialsJson());
    }

    [JSExport]
    internal static Task TargetLandRel(int xOffset, int yOffset)
    {
        ClassicUO.Game.RailBridgeApi.TargetLandRel(xOffset, yOffset);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task TargetTileRel(int xOffset, int yOffset, int graphic)
    {
        ClassicUO.Game.RailBridgeApi.TargetTileRel(xOffset, yOffset, graphic);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<bool> Pathfinding()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.Pathfinding());
    }

    [JSExport]
    internal static Task CancelPathfinding()
    {
        ClassicUO.Game.RailBridgeApi.CancelPathfinding();
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task<string> FindLayer(string layer, double serial)
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.FindLayerJson(layer, serial));
    }


    // Attack + Turn: base-ClassicUO verbs that were missing on cuo (v0.9.489). The
    // RailBridgeApi implementations exist here too now, so LS AND the JS sandbox
    // reach them on both clients.
    [JSExport]
    internal static Task Attack(double serial)
    {
        ClassicUO.Game.RailBridgeApi.Attack(serial);
        return Task.CompletedTask;
    }

    [JSExport]
    internal static Task Turn(double direction)
    {
        ClassicUO.Game.RailBridgeApi.Turn(direction);
        return Task.CompletedTask;
    }


    [JSExport]
    internal static Task<string> GetSkills()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.GetSkillsJson());
    }

    [JSExport]
    internal static Task<string> CurrentAbilityNames()
    {
        return Task.FromResult(ClassicUO.Game.RailBridgeApi.CurrentAbilityNamesJson());
    }

}
