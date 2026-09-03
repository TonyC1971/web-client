#if BROWSER_WASM
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    // WASM reimplementation of the desktop SQLite-backed FriendliesSQLManager.
    // Same public API; in-memory serial -> name map persisted as a compact
    // binary blob to /Data/Client/UserData/friendlies.bin via WasmUserDataStore
    // (IDBFS-backed, local-only — desktop parity). Friendlies are a small set,
    // so the whole map is rewritten on each mutation.
    public class FriendliesSQLManager : IDisposable
    {
        private const string STORE = "friendlies.bin";
        private const int STORE_VERSION = 1;

        public static FriendliesSQLManager Instance
        {
            get
            {
                field ??= new FriendliesSQLManager();
                return field;
            }
            private set => field = value;
        }

        private readonly ConcurrentDictionary<uint, string> _map = new();
        private readonly object _saveLock = new();
        private bool _disposed;

        public FriendliesSQLManager()
        {
            Load();
        }

        public Task AddAsync(uint serial, string name)
        {
            _map[serial] = name ?? string.Empty;
            Save();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(uint serial)
        {
            if (_map.TryRemove(serial, out _))
                Save();
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(uint serial) => Task.FromResult(_map.ContainsKey(serial));

        public Task<string> GetNameAsync(uint serial)
            => Task.FromResult(_map.TryGetValue(serial, out string n) ? n : null);

        public Task<Dictionary<uint, string>> GetAllAsync()
            => Task.FromResult(new Dictionary<uint, string>(_map));

        public Task ClearAsync()
        {
            _map.Clear();
            Save();
            return Task.CompletedTask;
        }

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
                    return;

                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    uint serial = r.ReadUInt32();
                    string name = r.ReadString();
                    _map[serial] = name;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[friendlies] load failed (starting fresh): {ex}");
                _map.Clear();
            }
        }

        private void Save()
        {
            try
            {
                List<KeyValuePair<uint, string>> snapshot;
                lock (_saveLock)
                    snapshot = new List<KeyValuePair<uint, string>>(_map);

                using var ms = new MemoryStream(snapshot.Count * 24 + 8);
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(STORE_VERSION);
                    w.Write(snapshot.Count);
                    foreach (KeyValuePair<uint, string> kv in snapshot)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value ?? string.Empty);
                    }
                }

                WasmUserDataStore.Save(STORE, ms.ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"[friendlies] save failed: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Save();
        }
    }
}
#endif
