#if BROWSER_WASM
using System;
using System.IO;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    // Pure-C# persistence for the WASM client's recovered SQLite-backed
    // features (Item Database, Friendlies, SQL settings). The desktop client
    // stores these in SQLite files under Data/; the browser has no native
    // SQLite, so the WASM managers keep their state in memory and serialize a
    // compact binary blob through this helper.
    //
    // Storage path: /Data/Client/UserData/<name>. That subtree is special:
    //   - It is IDBFS-backed (FS.mount(IDBFS, '/Data') at boot, restored via
    //     FS.syncfs(true)), so files persist across reloads once a flush runs.
    //   - It is the ONE path that main.js's profile-prune
    //     (pruneDataExceptWhitelist, which unlinks every non-whitelisted file
    //     under /Data before each FS.syncfs(false)) explicitly SPARES
    //     (`/Data/Client` early-returns from the walk) — so our blob survives
    //     the flush instead of being deleted like other transient writes.
    //   - It is NOT in the Discord profile upload set, so it stays local-only,
    //     matching the desktop SQLite semantics (per-machine, not synced).
    //
    // The actual durable write happens on the existing flush cadence
    // (autosave + pagehide call FS.syncfs(false)); Save() only updates the
    // in-MEMFS node, which is correct — these stores are caches rebuilt from
    // gameplay, so losing the last few seconds on a hard crash is acceptable.
    internal static class WasmUserDataStore
    {
        // Absolute IDBFS path (deterministic; do not rely on ExecutablePath).
        private const string DIR = "/Data/Client/UserData";

        public static byte[] Load(string name)
        {
            try
            {
                string path = Path.Combine(DIR, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"[userdata] load {name} failed: {ex.Message}");
                return null;
            }
        }

        public static void Save(string name, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(DIR);
                string path = Path.Combine(DIR, name);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Log.Error($"[userdata] save {name} failed: {ex.Message}");
            }
        }
    }
}
#endif
