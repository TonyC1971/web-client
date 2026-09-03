// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Linq;
using ClassicUO.IO.Audio;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility.Platforms;
using Microsoft.Xna.Framework;
using MathHelper = ClassicUO.Utility.MathHelper;
using ClassicUO.Configuration;
using ClassicUO.Game.Scenes;
using ClassicUO.Utility.Logging;
using ClassicUO.Assets;
using ClassicUO.Network;

namespace ClassicUO.Game
{
    internal sealed class World
    {
        private readonly EffectManager _effectManager;
        private readonly List<uint> _toRemove = new List<uint>();
        private uint _timeToDelete;

        public World()
        {
            WMapManager = new WorldMapEntityManager(this);
            CorpseManager = new CorpseManager(this);
            Party = new PartyManager(this);
            HouseManager = new HouseManager(this);
            WorldTextManager = new WorldTextManager(this);
            _effectManager = new EffectManager(this);
            MessageManager = new MessageManager(this);
            ContainerManager = new ContainerManager(this);
            IgnoreManager = new IgnoreManager(this);
            SkillsGroupManager = new SkillsGroupManager(this);
            ChatManager = new ChatManager(this);
            AuraManager = new AuraManager(this);
            UoAssist = new UoAssist(this);
            TargetManager = new TargetManager(this);
            DelayedObjectClickManager = new DelayedObjectClickManager(this);
            BoatMovingManager = new BoatMovingManager(this);
            NameOverHeadManager = new NameOverHeadManager(this);
            Macros = new MacroManager(this);
            CommandManager = new CommandManager(this);
            Weather = new Weather(this);
            InfoBars = new InfoBarManager(this);
        }

        public Point RangeSize;

        public PlayerMobile Player { get; private set; }

        public HouseCustomizationManager CustomHouseManager;

        public WorldMapEntityManager WMapManager { get; }

        public ActiveSpellIconsManager ActiveSpellIcons = new ActiveSpellIconsManager();

        public uint LastObject, ObjectToRemove;

        public ObjectPropertiesListManager OPL { get; } = new ObjectPropertiesListManager();

        public CorpseManager CorpseManager { get; }

        public PartyManager Party { get; }

        public HouseManager HouseManager { get; }

        public MessageManager MessageManager { get; }

        public ContainerManager ContainerManager { get; }

        public IgnoreManager IgnoreManager { get; }

        public SkillsGroupManager SkillsGroupManager { get; }

        public ChatManager ChatManager { get; }

        public AuraManager AuraManager { get; }

        public UoAssist UoAssist { get; }

        public TargetManager TargetManager { get; }

        public DelayedObjectClickManager DelayedObjectClickManager { get; }

        public BoatMovingManager BoatMovingManager { get; }

        public NameOverHeadManager NameOverHeadManager { get; }

        public MacroManager Macros { get; }

        public CommandManager CommandManager { get; }

        public Weather Weather { get; }

        public InfoBarManager InfoBars { get; }

        public Dictionary<uint, Item> Items { get; } = new Dictionary<uint, Item>();

        public Dictionary<uint, Mobile> Mobiles { get; } = new Dictionary<uint, Mobile>();

        public Map.Map Map { get; private set; }

        public byte ClientViewRange { get; set; } = Constants.MAX_VIEW_RANGE;

        public bool SkillsRequested { get; set; }

        public Season Season { get; private set; } = Season.Summer;
        public Season OldSeason { get; set; } = Season.Summer;

        public int OldMusicIndex { get; set; }

        public WorldTextManager WorldTextManager { get; }

        public JournalManager Journal { get; } = new JournalManager();


        public int MapIndex
        {
            get => Map?.Index ?? -1;
            set
            {
                if (MapIndex != value)
                {
                    // --- Facet swap instrumentation + transition (R1) ---
                    // Multi-facet aware (0-5). value<0 is a map-clear
                    // (logout-style) — don't treat that as a real swap.
                    int _swapFromIdx = MapIndex;
                    bool _isRealFacetSwap = value >= 0;
                    if (_isRealFacetSwap)
                    {
                        // Underscore-separated payload so a negative from-idx
                        // (cold-login -1 → 0) doesn't collide with the dash
                        // separators of the signal name itself.
                        WasmSignal.Send($"facet-swap-begin_{_swapFromIdx}_{value}");
                        InFacetTransition = true;
                        _facetTransitionFramesRemaining = FACET_TRANSITION_FRAMES;
                        _swapPendingFirstUpdate = true;
                        _swapPendingFirstDraw = true;
                    }

                    InternalMapChangeClear(true);

                    if (value < 0 && Map != null)
                    {
                        Map.Destroy();
                        Map = null;

                        return;
                    }

                    if (Map != null)
                    {
                        if (MapIndex >= 0)
                        {
#if BROWSER_WASM
                            // v0.5.17 R2: cache instead of destroy — warm chunks
                            // survive for repeat visits to this facet.
                            StoreMapToLru(Map);
#else
                            Map.Destroy();
#endif
                        }

                        ushort x = Player.X;
                        ushort y = Player.Y;
                        sbyte z = Player.Z;

                        Map = null;

                        if (value >= MapLoader.MAPS_COUNT)
                        {
                            value = 0;
                        }

                        Client.Game.UO.FileManager.Maps.LoadMap(value, ClientFeatures.Flags.HasFlag(CharacterListFlags.CLF_UNLOCK_FELUCCA_AREAS));
#if BROWSER_WASM
                        Map = AcquireMapFromLru(value) ?? new Map.Map(this, value);
#else
                        Map = new Map.Map(this, value);
#endif

                        Player.SetInWorldTile(x, y, z);
                        Player.ClearSteps();
                    }
                    else
                    {
                        Client.Game.UO.FileManager.Maps.LoadMap(value, ClientFeatures.Flags.HasFlag(CharacterListFlags.CLF_UNLOCK_FELUCCA_AREAS));
#if BROWSER_WASM
                        Map = AcquireMapFromLru(value) ?? new Map.Map(this, value);
#else
                        Map = new Map.Map(this, value);
#endif
                    }

                    // force cursor update when switching map
                    if (Client.Game.UO.GameCursor != null)
                    {
                        Client.Game.UO.GameCursor.Graphic = 0xFFFF;
                    }

                    if (_isRealFacetSwap)
                    {
                        // After new Map() constructor returns — the setter's
                        // critical path is done; remaining work in the JS
                        // window is the first Update + first Draw + AoI
                        // re-stream from server.
                        WasmSignal.Send("facet-swap-end-setter");
                    }

                    UoAssist.SignalMapChanged(value);
                }
            }
        }

        // --- Facet swap state (R1: instrumentation + transition mode) ---
        // Set true on every real MapIndex change (value>=0). Stays true for
        // FACET_TRANSITION_FRAMES Update ticks, then auto-clears. During this
        // window the renderer may downgrade quality (skip animation interp,
        // dynamic lighting, decorative overlays) to defer non-essential work
        // until the new world is interactive. Consumers MUST tolerate the
        // flag being false at any time — it is a hint, not a contract.
        public bool InFacetTransition { get; private set; }
        private int _facetTransitionFramesRemaining;
        private bool _swapPendingFirstUpdate;
        private bool _swapPendingFirstDraw;
        private const int FACET_TRANSITION_FRAMES = 30;

        public void TickFacetTransition()
        {
            if (!InFacetTransition) return;
            _facetTransitionFramesRemaining--;
            if (_facetTransitionFramesRemaining <= 0)
            {
                InFacetTransition = false;
                WasmSignal.Send("facet-swap-transition-cleared");
            }
        }

        public void NotifyFirstUpdateAfterSwap()
        {
            if (_swapPendingFirstUpdate)
            {
                _swapPendingFirstUpdate = false;
                WasmSignal.Send("facet-swap-first-update");
            }
        }

        public void NotifyFirstDrawAfterSwap()
        {
            if (_swapPendingFirstDraw)
            {
                _swapPendingFirstDraw = false;
                WasmSignal.Send("facet-swap-first-draw");
            }
        }

#if BROWSER_WASM
        // v0.5.17 R2: LRU Map cache — keep up to 3 old Map instances after a
        // facet swap instead of destroying them. When the player returns to a
        // previously-visited facet all its chunks (Land+Static) are already
        // loaded → zero cold-chunk I/O cost on repeat visits.
        //
        // Requires _terrainChunks to be an instance field in Map.cs (done).
        // Items/Mobiles are safely removed by InternalMapChangeClear before
        // StoreMapToLru, so the cached Map only retains terrain objects.
        private const int MAP_LRU_SIZE = 3;
        private readonly Map.Map[] _mapLru = new Map.Map[MAP_LRU_SIZE];

        private void StoreMapToLru(Map.Map map)
        {
            if (map == null) return;
            // Avoid double-storing (defensive)
            for (int i = 0; i < MAP_LRU_SIZE; i++)
                if (_mapLru[i] == map) return;
            // Find an empty slot or evict the oldest (last) entry
            int slot = MAP_LRU_SIZE - 1;
            for (int i = 0; i < MAP_LRU_SIZE; i++)
            {
                if (_mapLru[i] == null) { slot = i; break; }
            }
            _mapLru[slot]?.Destroy();
            // Shift existing entries toward the end to free slot 0
            for (int i = slot; i > 0; i--)
                _mapLru[i] = _mapLru[i - 1];
            _mapLru[0] = map;
        }

        private Map.Map AcquireMapFromLru(int mapIndex)
        {
            for (int i = 0; i < MAP_LRU_SIZE; i++)
            {
                if (_mapLru[i]?.Index == mapIndex)
                {
                    var found = _mapLru[i];
                    // Bring to front
                    for (int j = i; j > 0; j--)
                        _mapLru[j] = _mapLru[j - 1];
                    _mapLru[0] = null; // slot 0 will be set by caller
                    return found;
                }
            }
            return null;
        }

        private void ClearMapLru()
        {
            for (int i = 0; i < MAP_LRU_SIZE; i++)
            {
                _mapLru[i]?.Destroy();
                _mapLru[i] = null;
            }
        }
#endif

        public bool InGame => Player != null && Map != null;

        public IsometricLight Light { get; } = new IsometricLight
        {
            Overall = 0,
            Personal = 0,
            RealOverall = 0,
            RealPersonal = 0
        };

        public LockedFeatures ClientLockedFeatures { get; } = new LockedFeatures();

        public ClientFeatures ClientFeatures { get; } = new ClientFeatures();

        public string ServerName { get; set; } = "_";



        public void CreatePlayer(uint serial)
        {
            if (ProfileManager.CurrentProfile == null)
            {
                string lastChar = LastCharacterManager.GetLastCharacter(LoginScene.Account, ServerName);
                ProfileManager.Load(ServerName, LoginScene.Account, lastChar);
            }

            if (Player != null)
            {
                Clear();
            }

            Player = new PlayerMobile(this, serial);
            Mobiles.Add(Player);

            Log.Trace($"Player [0x{serial:X8}] created");
            // Belt-and-suspenders signal for main.js: by the time the
            // server populates the player the world is definitely live,
            // even if earlier login-gump-added / gamescene-active
            // signals got missed. main.js listens for
            // `cuo:player-created` to mark BOOT_OK_KEY + hide the
            // loader. Direct C# -> JS, no console sniff.
            WasmSignal.Send("player-created");
        }

        public void ChangeSeason(Season season, int music)
        {
            Season = season;

            foreach (Chunk chunk in Map.GetUsedChunks())
            {
                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (GameObject obj = chunk?.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                        {
                            obj.UpdateGraphicBySeason();
                        }
                    }
                }
            }

            //TODO(deccer): refactor this out into _audioPlayer.PlayMusic(...)
            UOMusic currentMusic = Client.Game.Audio.GetCurrentMusic();
            if (currentMusic == null || currentMusic.Index == Client.Game.Audio.LoginMusicIndex)
            {
                Client.Game.Audio.PlayMusic(music, false);
            }
        }


        /*[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CheckToRemove(Entity obj, int distance)
        {
            if (Player == null || obj.Serial == Player.Serial)
                return false;

            return Math.Max(Math.Abs(obj.X - RangeSize.X), Math.Abs(obj.Y - RangeSize.Y)) > distance;
        }
        */

        public void Update()
        {
            if (Player != null)
            {
                if (SerialHelper.IsValid(ObjectToRemove))
                {
                    Item rem = Items.Get(ObjectToRemove);
                    ObjectToRemove = 0;

                    if (rem != null)
                    {
                        Entity container = Get(rem.Container);

                        RemoveItem(rem, true);

                        if (rem.Layer == Layer.OneHanded || rem.Layer == Layer.TwoHanded)
                        {
                            Player.UpdateAbilities();
                        }

                        if (container != null)
                        {
                            if (SerialHelper.IsMobile(container.Serial))
                            {
                                UIManager.GetGump<PaperDollGump>(container.Serial)?.RequestUpdateContents();
                            }
                            else if (SerialHelper.IsItem(container.Serial))
                            {
                                UIManager.GetGump<ContainerGump>(container.Serial)?.RequestUpdateContents();

                                if (container.Graphic == 0x2006)
                                {
                                    UIManager.GetGump<GridLootGump>(container)?.RequestUpdateContents();
                                }
                            }
                        }
                    }
                }

                bool do_delete = _timeToDelete < Time.Ticks;

                if (do_delete)
                {
                    _timeToDelete = Time.Ticks + 50;
                }

                foreach (Mobile mob in Mobiles.Values)
                {
                    mob.Update();

                    if (do_delete && mob.Distance > ClientViewRange /*CheckToRemove(mob, ClientViewRange)*/)
                    {
                        RemoveMobile(mob);
                    }

                    if (mob.IsDestroyed)
                    {
                        _toRemove.Add(mob.Serial);
                    }
                    else
                    {
                        if (mob.NotorietyFlag == NotorietyFlag.Ally)
                        {
                            WMapManager.AddOrUpdate
                            (
                                mob.Serial,
                                mob.X,
                                mob.Y,
                                MathHelper.PercetangeOf(mob.Hits, mob.HitsMax),
                                MapIndex,
                                true,
                                mob.Name
                            );
                        }
                        else if (Party.Leader != 0 && Party.Contains(mob))
                        {
                            WMapManager.AddOrUpdate
                            (
                                mob.Serial,
                                mob.X,
                                mob.Y,
                                MathHelper.PercetangeOf(mob.Hits, mob.HitsMax),
                                MapIndex,
                                false,
                                mob.Name
                            );
                        }
                    }
                }

                if (_toRemove.Count != 0)
                {
                    for (int i = 0; i < _toRemove.Count; i++)
                    {
                        Mobiles.Remove(_toRemove[i]);
                    }

                    _toRemove.Clear();
                }

                foreach (Item item in Items.Values)
                {
                    item.Update();

                    if (do_delete && item.OnGround && item.Distance > ClientViewRange /*CheckToRemove(item, ClientViewRange)*/)
                    {
                        if (item.IsMulti)
                        {
                            if (HouseManager.TryToRemove(item, ClientViewRange))
                            {
                                RemoveItem(item);
                            }
                        }
                        else
                        {
                            RemoveItem(item);
                        }
                    }

                    if (item.IsDestroyed)
                    {
                        _toRemove.Add(item.Serial);
                    }
                }

                if (_toRemove.Count != 0)
                {
                    for (int i = 0; i < _toRemove.Count; i++)
                    {
                        Items.Remove(_toRemove[i]);
                    }

                    _toRemove.Clear();
                }

                _effectManager.Update();
                WorldTextManager.Update();
                WMapManager.RemoveUnupdatedWEntity();
            }
        }

        public bool Contains(uint serial)
        {
            if (SerialHelper.IsItem(serial))
            {
                return Items.Contains(serial);
            }

            return SerialHelper.IsMobile(serial) && Mobiles.Contains(serial);
        }

        public Entity Get(uint serial)
        {
            Entity ent;

            if (SerialHelper.IsMobile(serial))
            {
                ent = Mobiles.Get(serial);

                if (ent == null)
                {
                    ent = Items.Get(serial);
                }
            }
            else
            {
                ent = Items.Get(serial);

                if (ent == null)
                {
                    ent = Mobiles.Get(serial);
                }
            }

            if (ent != null && ent.IsDestroyed)
            {
                ent = null;
            }

            return ent;
        }

        public Item GetOrCreateItem(uint serial)
        {
            Item item = Items.Get(serial);

            if (item != null && item.IsDestroyed)
            {
                Items.Remove(serial);
                item = null;
            }

            if (item == null /*|| item.IsDestroyed*/)
            {
                item = Item.Create(this, serial);
                Items.Add(item);
            }

            return item;
        }

        public Mobile GetOrCreateMobile(uint serial)
        {
            Mobile mob = Mobiles.Get(serial);

            if (mob != null && mob.IsDestroyed)
            {
                Mobiles.Remove(serial);
                mob = null;
            }

            if (mob == null /*|| mob.IsDestroyed*/)
            {
                mob = Mobile.Create(this, serial);
                Mobiles.Add(mob);
            }

            return mob;
        }

        public void RemoveItemFromContainer(uint serial)
        {
            Item it = Items.Get(serial);

            if (it != null)
            {
                RemoveItemFromContainer(it);
            }
        }

        public void RemoveItemFromContainer(Item obj)
        {
            uint containerSerial = obj.Container;

            // if entity is running the "dying" animation we have to reset container too.
            // SerialHelper.IsValid(containerSerial) is not ideal in this case
            if (containerSerial != 0xFFFF_FFFF)
            {
                if (SerialHelper.IsMobile(containerSerial))
                {
                    UIManager.GetGump<PaperDollGump>(containerSerial)?.RequestUpdateContents();
                }
                else if (SerialHelper.IsItem(containerSerial))
                {
                    UIManager.GetGump<ContainerGump>(containerSerial)?.RequestUpdateContents();
                }

                Entity container = Get(containerSerial);

                if (container != null)
                {
                    container.Remove(obj);
                }

                obj.Container = 0xFFFF_FFFF;
            }

            obj.Next = null;
            obj.Previous = null;
            obj.RemoveFromTile();
        }

        public bool RemoveItem(uint serial, bool forceRemove = false)
        {
            Item item = Items.Get(serial);

            if (item == null || item.IsDestroyed)
            {
                return false;
            }

            LinkedObject first = item.Items;
            RemoveItemFromContainer(item);

            while (first != null)
            {
                LinkedObject next = first.Next;

                RemoveItem(first as Item, forceRemove);

                first = next;
            }

            OPL.Remove(serial);
            item.Destroy();

            if (forceRemove)
            {
                Items.Remove(serial);
            }

            return true;
        }

        public bool RemoveMobile(uint serial, bool forceRemove = false)
        {
            Mobile mobile = Mobiles.Get(serial);

            if (mobile == null || mobile.IsDestroyed)
            {
                return false;
            }

            LinkedObject first = mobile.Items;

            while (first != null)
            {
                LinkedObject next = first.Next;

                RemoveItem(first as Item, forceRemove);

                first = next;
            }

            OPL.Remove(serial);
            mobile.Destroy();

            if (forceRemove)
            {
                Mobiles.Remove(serial);
            }

            return true;
        }

        public void SpawnEffect
        (
            GraphicEffectType type,
            uint source,
            uint target,
            ushort graphic,
            ushort hue,
            ushort srcX,
            ushort srcY,
            sbyte srcZ,
            ushort targetX,
            ushort targetY,
            sbyte targetZ,
            byte speed,
            int duration,
            bool fixedDir,
            bool doesExplode,
            bool hasparticles,
            GraphicEffectBlendMode blendmode
        )
        {
            _effectManager.CreateEffect
            (
                type,
                source,
                target,
                graphic,
                hue,
                srcX,
                srcY,
                srcZ,
                targetX,
                targetY,
                targetZ,
                speed,
                duration,
                fixedDir,
                doesExplode,
                hasparticles,
                blendmode
            );
        }

        public uint FindNearest(ScanTypeObject scanType)
        {
            int distance = int.MaxValue;
            uint serial = 0;

            if (scanType == ScanTypeObject.Objects)
            {
                foreach (Item item in Items.Values)
                {
                    if (item.IsMulti || item.IsDestroyed || !item.OnGround)
                    {
                        continue;
                    }

                    if (item.Distance < distance)
                    {
                        distance = item.Distance;
                        serial = item.Serial;
                    }
                }
            }
            else
            {
                foreach (Mobile mobile in Mobiles.Values)
                {
                    if (mobile.IsDestroyed || mobile == Player)
                    {
                        continue;
                    }

                    switch (scanType)
                    {
                        case ScanTypeObject.Party:
                            if (!Party.Contains(mobile))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Followers:
                            if (!(mobile.IsRenamable && mobile.NotorietyFlag != NotorietyFlag.Invulnerable && mobile.NotorietyFlag != NotorietyFlag.Enemy))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Hostile:
                            if (mobile.NotorietyFlag == NotorietyFlag.Ally || mobile.NotorietyFlag == NotorietyFlag.Innocent || mobile.NotorietyFlag == NotorietyFlag.Invulnerable)
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Objects:
                            /* This was handled separately above */
                            continue;
                    }

                    if (mobile.Distance < distance)
                    {
                        distance = mobile.Distance;
                        serial = mobile.Serial;
                    }
                }
            }

            return serial;
        }

        public uint FindNext(ScanTypeObject scanType, uint lastSerial, bool reverse)
        {
            bool found = false;

            if (scanType == ScanTypeObject.Objects)
            {
                var items = reverse ? Items.Values.Reverse() : Items.Values;
                foreach (Item item in items)
                {
                    if (item.IsMulti || item.IsDestroyed || !item.OnGround)
                    {
                        continue;
                    }

                    if (lastSerial == 0)
                    {
                        return item.Serial;
                    }
                    else if (item.Serial == lastSerial)
                    {
                        found = true;
                    }
                    else if (found)
                    {
                        return item.Serial;
                    }
                }
            }
            else
            {
                IEnumerable<Mobile> mobiles = reverse ? Mobiles.Values.Reverse() : Mobiles.Values;
                foreach (Mobile mobile in mobiles)
                {
                    if (mobile.IsDestroyed || mobile == Player)
                    {
                        continue;
                    }

                    switch (scanType)
                    {
                        case ScanTypeObject.Party:
                            if (!Party.Contains(mobile))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Followers:
                            if (!(mobile.IsRenamable && mobile.NotorietyFlag != NotorietyFlag.Invulnerable && mobile.NotorietyFlag != NotorietyFlag.Enemy))
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Hostile:
                            if (mobile.NotorietyFlag == NotorietyFlag.Ally || mobile.NotorietyFlag == NotorietyFlag.Innocent || mobile.NotorietyFlag == NotorietyFlag.Invulnerable)
                            {
                                continue;
                            }
                            break;
                        case ScanTypeObject.Objects:
                            /* This was handled separately above */
                            continue;
                    }

                    if (lastSerial == 0)
                    {
                        return mobile.Serial;
                    }
                    else if (mobile.Serial == lastSerial)
                    {
                        found = true;
                    }
                    else if (found)
                    {
                        return mobile.Serial;
                    }
                }
            }

            if (lastSerial != 0)
            {
                /* If we get here, it means we didn't find anything but we started with a serial number. That means
                 * if we restart the search from the beginning it may find something again. */
                return FindNext(scanType, 0, reverse);
            }

            return 0;
        }


        public void Clear()
        {
            foreach (Mobile mobile in Mobiles.Values)
            {
                RemoveMobile(mobile);
            }

            foreach (Item item in Items.Values)
            {
                RemoveItem(item);
            }

            UIManager.GetGump<BaseHealthBarGump>(Player?.Serial)?.Dispose();

            ObjectToRemove = 0;
            LastObject = 0;
            Items.Clear();
            Mobiles.Clear();
            Player?.Destroy();
            Player = null;
            Map?.Destroy();
            Map = null;
#if BROWSER_WASM
            ClearMapLru();
#endif
            Light.Overall = Light.RealOverall = 0;
            Light.Personal = Light.RealPersonal = 0;
            ClientLockedFeatures.SetFlags(0);
            Party?.Clear();
            TargetManager.LastAttack = 0;
            MessageManager.PromptData = default;
            _effectManager.Clear();
            _toRemove.Clear();
            CorpseManager.Clear();
            OPL.Clear();
            WMapManager.Clear();
            HouseManager?.Clear();

            Season = Season.Summer;
            OldSeason = Season.Summer;

            Journal.Clear();
            WorldTextManager.Clear();
            ActiveSpellIcons.Clear();

            SkillsRequested = false;
        }

        private void InternalMapChangeClear(bool noplayer)
        {
            if (!noplayer)
            {
                Map.Destroy();
                Map = null;
                Player.Destroy();
                Player = null;
            }

            foreach (Item item in Items.Values)
            {
                if (noplayer && Player != null && !Player.IsDestroyed)
                {
                    if (item.RootContainer == Player)
                    {
                        continue;
                    }
                }

                if (item.OnGround && item.IsMulti)
                {
                    HouseManager.Remove(item.Serial);
                }

                _toRemove.Add(item);
            }

            foreach (uint serial in _toRemove)
            {
                RemoveItem(serial, true);
            }

            _toRemove.Clear();

            foreach (Mobile mob in Mobiles.Values)
            {
                if (noplayer && Player != null && !Player.IsDestroyed)
                {
                    if (mob == Player)
                    {
                        continue;
                    }
                }

                _toRemove.Add(mob);
            }

            foreach (uint serial in _toRemove)
            {
                RemoveMobile(serial, true);
            }

            _toRemove.Clear();
        }
    }
}