#if BROWSER_WASM
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Timer = System.Timers.Timer;

namespace ClassicUO.Game.Managers
{
    // WASM reimplementation of the desktop SQLite-backed ItemDatabaseManager.
    // Same public API (Initialize / GetItemCustomName / AddOrUpdateItem /
    // SearchItems / ClearOldDataAsync / Dispose) so the re-included Myra UI
    // (Game/UI/MyraWindows/Widgets/Assistant/ItemDatabase) works unchanged.
    //
    // The browser has no native SQLite, so the store is an in-memory
    // ConcurrentDictionary keyed by Serial (the SQLite PK) and is serialized
    // as a compact binary blob to /Data/Client/UserData/itemdb.bin via
    // WasmUserDataStore — IDBFS-backed (persists across reloads on FS.syncfs)
    // and local-only, matching the desktop items.db semantics. Search is LINQ
    // over the in-memory set (≤ MAX_SEARCH_LIMIT rows is trivial in-memory).
    public sealed class ItemDatabaseManager : IDisposable
    {
        private const string STORE = "itemdb.bin";
        private const int STORE_VERSION = 1;
        private const int MAX_SEARCH_LIMIT = 10000;
        private const int SAVE_DEBOUNCE_MS = 3000;

        private static readonly Lazy<ItemDatabaseManager> _instance = new(() => new ItemDatabaseManager());
        public static ItemDatabaseManager Instance => _instance.Value;

        private readonly ConcurrentDictionary<uint, ItemInfo> _items = new();
        private readonly object _saveLock = new();
        private Timer _saveTimer;
        private bool _initialized;
        private bool _disposed;
        private bool _dirty;

        public void Initialize()
        {
            if (_initialized)
                return;

            try
            {
                Load();
                _initialized = true;
                Log.Trace($"[itemdb] WASM ItemDatabaseManager initialized: {_items.Count} item(s) loaded");
            }
            catch (Exception ex)
            {
                Log.Error($"[itemdb] init failed: {ex}");
            }
        }

        public Task<string> GetItemCustomName(uint serial)
        {
            string name = _items.TryGetValue(serial, out ItemInfo info) ? (info.CustomName ?? string.Empty) : string.Empty;
            return Task.FromResult(name);
        }

        public void AddOrUpdateItem(Item item, World world)
        {
            if (!_initialized || item == null || world?.Player == null || ProfileManager.CurrentProfile?.ItemDatabaseEnabled == false)
                return;

            if (item.ItemData.IsDoor || item.ItemData.IsLight || item.ItemData.IsInternal || item.ItemData.IsRoof || item.ItemData.IsWall || item.IsMulti || item.IsCorpse || StaticFilters.IsRock(item.Graphic) || StaticFilters.IsTree(item.Graphic, out _))
                return;

            Layer layer = Layer.Invalid;
            try
            {
                layer = (Layer)item.ItemData.Layer;
            }
            catch (Exception ex)
            {
                Log.Warn($"[itemdb] failed to get layer for item {item.Serial}: {ex.Message}");
            }

            string name = item.Name ?? string.Empty;
            string properties = string.Empty;
            if (world.OPL.TryGetNameAndData(item.Serial, out string oplName, out string oplData))
            {
                if (!string.IsNullOrEmpty(oplName))
                    name = oplName;
                if (!string.IsNullOrEmpty(oplData))
                    properties = oplData;
            }

            uint character = world.Player.Serial;
            string characterName = world.Player.Name ?? string.Empty;
            string serverName = ProfileManager.CurrentProfile?.ServerName ?? "unknown";
            string customName = item.CustomName ?? string.Empty;
            DateTime now = DateTime.Now;

            _items.AddOrUpdate(item.Serial,
                _ => new ItemInfo
                {
                    Serial = item.Serial,
                    Graphic = item.Graphic,
                    Hue = item.Hue,
                    Name = name,
                    Properties = properties,
                    Container = item.Container,
                    Layer = layer,
                    UpdatedTime = now,
                    Character = character,
                    CharacterName = characterName,
                    ServerName = serverName,
                    X = item.X,
                    Y = item.Y,
                    OnGround = item.OnGround,
                    CustomName = customName,
                },
                (_, existing) =>
                {
                    // Mirror the SQLite upsert: empty incoming Name/Properties/
                    // CharacterName/ServerName/CustomName keep the stored value.
                    existing.Graphic = item.Graphic;
                    existing.Hue = item.Hue;
                    if (!string.IsNullOrEmpty(name)) existing.Name = name;
                    if (!string.IsNullOrEmpty(properties)) existing.Properties = properties;
                    existing.Container = item.Container;
                    existing.Layer = layer;
                    existing.UpdatedTime = now;
                    existing.Character = character;
                    if (!string.IsNullOrEmpty(characterName)) existing.CharacterName = characterName;
                    if (!string.IsNullOrEmpty(serverName)) existing.ServerName = serverName;
                    existing.X = item.X;
                    existing.Y = item.Y;
                    existing.OnGround = item.OnGround;
                    if (!string.IsNullOrEmpty(customName)) existing.CustomName = customName;
                    return existing;
                });

            MarkDirty();
        }

        public void SearchItems(Action<List<ItemInfo>> onResults,
            uint? serial = null,
            ushort? graphic = null,
            ushort? hue = null,
            string name = null,
            string properties = null,
            uint? container = null,
            Layer? layer = null,
            DateTime? updatedAfter = null,
            DateTime? updatedBefore = null,
            uint? character = null,
            string characterName = null,
            string serverName = null,
            bool? onGround = null,
            int limit = 1000)
        {
            Profile profile = ProfileManager.CurrentProfile;
            if (!_initialized || profile == null || !profile.ItemDatabaseEnabled)
            {
                Task.Run(() => onResults?.Invoke(new List<ItemInfo>()));
                return;
            }

            if (limit < 0)
                limit = 1000;
            else if (limit > MAX_SEARCH_LIMIT)
                limit = MAX_SEARCH_LIMIT;

            Task.Run(() =>
            {
                List<ItemInfo> results;
                try
                {
                    IEnumerable<ItemInfo> q = _items.Values;

                    if (serial.HasValue) q = q.Where(i => i.Serial == serial.Value);
                    if (graphic.HasValue) q = q.Where(i => i.Graphic == graphic.Value);
                    if (hue.HasValue) q = q.Where(i => i.Hue == hue.Value);
                    if (!string.IsNullOrEmpty(name)) q = q.Where(i => i.Name != null && i.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(properties)) q = q.Where(i => i.Properties != null && i.Properties.Contains(properties, StringComparison.OrdinalIgnoreCase));
                    if (container.HasValue) q = q.Where(i => i.Container == container.Value);
                    if (layer.HasValue) q = q.Where(i => i.Layer == layer.Value);
                    if (updatedAfter.HasValue) q = q.Where(i => i.UpdatedTime >= updatedAfter.Value);
                    if (updatedBefore.HasValue) q = q.Where(i => i.UpdatedTime <= updatedBefore.Value);
                    if (character.HasValue) q = q.Where(i => i.Character == character.Value);
                    if (!string.IsNullOrEmpty(characterName)) q = q.Where(i => i.CharacterName != null && i.CharacterName.Contains(characterName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(serverName)) q = q.Where(i => i.ServerName != null && i.ServerName.Contains(serverName, StringComparison.OrdinalIgnoreCase));
                    if (onGround.HasValue) q = q.Where(i => i.OnGround == onGround.Value);

                    q = q.OrderByDescending(i => i.UpdatedTime);
                    if (limit > 0) q = q.Take(limit);

                    results = q.ToList();
                }
                catch (Exception ex)
                {
                    Log.Error($"[itemdb] search failed: {ex}");
                    results = new List<ItemInfo>();
                }

                onResults?.Invoke(results);
            });
        }

        public Task ClearOldDataAsync(TimeSpan maxAge)
        {
            Profile profile = ProfileManager.CurrentProfile;
            if (!_initialized || profile == null || !profile.ItemDatabaseEnabled)
                return Task.CompletedTask;

            return Task.Run(() =>
            {
                try
                {
                    DateTime cutoff = DateTime.Now - maxAge;
                    int removed = 0;
                    foreach (KeyValuePair<uint, ItemInfo> kv in _items)
                    {
                        if (kv.Value.UpdatedTime < cutoff && _items.TryRemove(kv.Key, out _))
                            removed++;
                    }

                    if (removed > 0)
                    {
                        MarkDirty();
                        Log.Trace($"[itemdb] cleared {removed} old item(s) (older than {cutoff})");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[itemdb] clear old data failed: {ex}");
                }
            });
        }

        private void MarkDirty()
        {
            lock (_saveLock)
            {
                _dirty = true;
                if (_disposed)
                    return;

                if (_saveTimer == null)
                {
                    _saveTimer = new Timer(SAVE_DEBOUNCE_MS) { AutoReset = false };
                    _saveTimer.Elapsed += SaveTimerElapsed;
                }

                _saveTimer.Stop();
                _saveTimer.Start();
            }
        }

        private void SaveTimerElapsed(object sender, ElapsedEventArgs e) => Save();

        private void Load()
        {
            byte[] data = WasmUserDataStore.Load(STORE);
            if (data == null || data.Length < 8)
                return;

            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);

                int ver = r.ReadInt32();
                if (ver != STORE_VERSION)
                {
                    Log.Warn($"[itemdb] store version {ver} != {STORE_VERSION}, starting fresh");
                    return;
                }

                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var info = new ItemInfo
                    {
                        Serial = r.ReadUInt32(),
                        Graphic = r.ReadUInt16(),
                        Hue = r.ReadUInt16(),
                        Name = r.ReadString(),
                        Properties = r.ReadString(),
                        Container = r.ReadUInt32(),
                        Layer = (Layer)r.ReadInt32(),
                        UpdatedTime = new DateTime(r.ReadInt64()),
                        Character = r.ReadUInt32(),
                        CharacterName = r.ReadString(),
                        ServerName = r.ReadString(),
                        X = r.ReadInt32(),
                        Y = r.ReadInt32(),
                        OnGround = r.ReadBoolean(),
                        CustomName = r.ReadString(),
                    };
                    _items[info.Serial] = info;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[itemdb] load/deserialize failed (starting fresh): {ex}");
                _items.Clear();
            }
        }

        private void Save()
        {
            lock (_saveLock)
            {
                if (!_dirty)
                    return;
                _dirty = false;
            }

            try
            {
                List<ItemInfo> snapshot = _items.Values.ToList();
                using var ms = new MemoryStream(snapshot.Count * 96 + 8);
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(STORE_VERSION);
                    w.Write(snapshot.Count);
                    foreach (ItemInfo info in snapshot)
                    {
                        w.Write(info.Serial);
                        w.Write(info.Graphic);
                        w.Write(info.Hue);
                        w.Write(info.Name ?? string.Empty);
                        w.Write(info.Properties ?? string.Empty);
                        w.Write(info.Container);
                        w.Write((int)info.Layer);
                        w.Write(info.UpdatedTime.Ticks);
                        w.Write(info.Character);
                        w.Write(info.CharacterName ?? string.Empty);
                        w.Write(info.ServerName ?? string.Empty);
                        w.Write(info.X);
                        w.Write(info.Y);
                        w.Write(info.OnGround);
                        w.Write(info.CustomName ?? string.Empty);
                    }
                }

                WasmUserDataStore.Save(STORE, ms.ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"[itemdb] save failed: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (_saveLock)
            {
                if (_saveTimer != null)
                {
                    _saveTimer.Stop();
                    _saveTimer.Elapsed -= SaveTimerElapsed;
                    _saveTimer.Dispose();
                    _saveTimer = null;
                }
                _dirty = true; // force a final flush below
            }

            Save();
        }
    }
}
#endif
