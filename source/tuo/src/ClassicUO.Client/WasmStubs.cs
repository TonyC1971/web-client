// WebClient WASM-fork stubs (additive, 2026-05-26)
//
// Empty placeholder types so the WASM build compiles without the
// out-of-scope dependencies (LegionScripting / IronPython / Async.IRC /
// Microsoft.Data.Sqlite / Vosk). All guarded by #if BROWSER_WASM so
// upstream TazUO (desktop) builds unaffected.
//
// Rationale: stubbing the types is cheaper than #if-guarding every
// call site. EventSink.cs has 5+ event signatures referencing
// ApiItem/ApiBuff/ApiMobile; Profile.cs/GameScene.cs touch
// TazUOChatManager in 6 places. Without subscribers (LegionScripting
// is excluded from the build), the events fire into the void —
// harmless no-op behaviour.
//
// To re-enable the real types: remove the matching <Compile Remove>
// lines in ClassicUO.Client.csproj's WASM-fork block. The real source
// then takes precedence and these stubs disappear (they only compile
// when BROWSER_WASM is defined, which is the marker for the WASM
// build).
//
// Maintained as part of the multi-client port. See:
//   docs/tuo/LESSONS_FROM_CUO.md
//   docs/tuo/BUILD_NOTES.md
//   source/tuo/UPSTREAM.md

#if BROWSER_WASM

namespace ClassicUO.LegionScripting.ApiClasses
{
    /// <summary>WASM stub — see WasmStubs.cs at the top of this file.</summary>
    public sealed class ApiItem
    {
        public ApiItem(object _) { }
    }

    /// <summary>WASM stub — see WasmStubs.cs at the top of this file.</summary>
    public sealed class ApiBuff
    {
        public ApiBuff(object _) { }
    }

    /// <summary>WASM stub — see WasmStubs.cs at the top of this file.</summary>
    public sealed class ApiMobile
    {
        public ApiMobile(object _) { }
    }
}

namespace ClassicUO.LegionScripting
{
    /// <summary>WASM stub — see WasmStubs.cs at the top of this file.
    /// Non-static because auto-gen `EventSinkApi(LegionAPI api)` takes
    /// it as a constructor parameter (CS0721 fires if it's static).
    /// Has a `ScheduleCallbacks` method that the auto-gen calls.</summary>
    public sealed class LegionAPI : System.IDisposable
    {
        public void Dispose() { /* no-op */ }
        public void ScheduleCallbacks(object[] callbacks, object eventArgs) { /* no-op */ }
    }

    /// <summary>WASM stub: ScriptRecorder. Full Record*/StartRecording/
    /// IsRecording/ActionCount/etc. surface inventoried from grep across
    /// the whole TUO source. All methods accept `params object[]` so any
    /// concrete call signature compiles; all do nothing. Events return
    /// dummy add/remove.</summary>
    public sealed class ScriptRecorder
    {
        public static ScriptRecorder Instance { get; } = new ScriptRecorder();

        // Record* methods (player action logging)
        public void RecordAbility(params object[] args) { }
        public void RecordAllyMsg(params object[] args) { }
        public void RecordAttack(params object[] args) { }
        public void RecordBandageSelf(params object[] args) { }
        public void RecordCastSpell(params object[] args) { }
        public void RecordCloseContainer(params object[] args) { }
        public void RecordContextMenu(params object[] args) { }
        public void RecordDismount(params object[] args) { }
        public void RecordDragDrop(params object[] args) { }
        public void RecordEmoteMsg(params object[] args) { }
        public void RecordEquipItem(params object[] args) { }
        public void RecordGrayMenuResponse(params object[] args) { }
        public void RecordGuildMsg(params object[] args) { }
        public void RecordMenuResponse(params object[] args) { }
        public void RecordMount(params object[] args) { }
        public void RecordPartyMsg(params object[] args) { }
        public void RecordReplyGump(params object[] args) { }
        public void RecordSay(params object[] args) { }
        public void RecordTarget(params object[] args) { }
        public void RecordTargetLocation(params object[] args) { }
        public void RecordUseItem(params object[] args) { }
        public void RecordUseSkill(params object[] args) { }
        public void RecordVirtue(params object[] args) { }
        public void RecordWaitForGump(params object[] args) { }
        public void RecordWhisperMsg(params object[] args) { }
        public void RecordYellMsg(params object[] args) { }
        public void UpdatePlayerPosition(params object[] args) { }

        // State + control
        public bool IsRecording => false;
        public bool IsPaused => false;
        public int ActionCount => 0;
        public System.TimeSpan RecordingDuration => System.TimeSpan.Zero;
        public void StartRecording() { }
        public void StopRecording() { }
        public void PauseRecording() { }
        public void ResumeRecording() { }
        public void ClearRecording() { }
        public string GenerateScript() => string.Empty;
        public void RemoveActionAt(int _) { }
        public void SwapActions(int _, int __) { }

        // Events — empty add/remove keeps `+=` callers compiling.
        public event System.EventHandler ActionRecorded { add { } remove { } }
        public event System.EventHandler RecordingStateChanged { add { } remove { } }
    }

    /// <summary>WASM stub: ScriptingInfoGump. (string, object) overload
    /// covers all caller variants — string, int, ushort, etc.</summary>
    public static class ScriptingInfoGump
    {
        public static void AddOrUpdateInfo(string key, object value) { }
    }

    /// <summary>WASM stub: PersistentVars. GameScene.cs calls Load/Unload at boot.</summary>
    public static class PersistentVars
    {
        public static void Load() { }
        public static void Unload() { }
    }

    /// <summary>WASM stub: ScriptBrowser. CommandManager.cs references it.</summary>
    public static class ScriptBrowser
    {
        public static void Show() { }
    }

    /// <summary>WASM stub: nested LegionScripting class in the
    /// ClassicUO.LegionScripting namespace. Various callers reference
    /// Init/Unload/DownloadApiPy as static methods.</summary>
    public static class LegionScripting
    {
        public static void Init(object _) { }
        public static void Unload() { }
        public static void DownloadApiPy() { }

        // v5.3 sync (2026-07-20): the SpellBar gained "script" slots (#568),
        // so SpellBar.cs / SpellBarManager.cs now touch the script registry.
        // Those two files are NOT excluded on wasm (the bar itself works), so
        // the registry gets an empty stub: no scripts are ever listed, the
        // events never fire, play/stop are no-ops. Web scripting lives in the
        // rail (Pyodide) — not in this C# engine.
        public static System.Collections.Generic.List<ScriptFile> LoadedScripts { get; } = new();

        public static event System.EventHandler<ScriptFile> ScriptStarted
        {
            add { /* no-op */ }
            remove { /* no-op */ }
        }

        public static event System.EventHandler<ScriptFile> ScriptStopped
        {
            add { /* no-op */ }
            remove { /* no-op */ }
        }

        public static void PlayScript(ScriptFile _) { }
        public static void StopScript(ScriptFile _) { }
    }

    /// <summary>WASM stub: script hotkey registry (upstream #609). The real one
    /// binds keys to Legion scripts; web scripting lives in the rail, so
    /// registering nothing is the correct behaviour here.</summary>
    public static class ScriptHotkeysManager
    {
        public static void RegisterAll() { }
    }

    /// <summary>WASM stub: a LegionScripting script handle. Only the members
    /// the SpellBar touches are modelled (RelativePath as the stable id).</summary>
    public sealed class ScriptFile
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool IsPlaying => false;
    }

    /// <summary>WASM stub: Utility helper class in
    /// ClassicUO.LegionScripting namespace. SimpleProgressBar.cs uses
    /// Utility.GetColorFromHex(hexString) → Color.</summary>
    public static class Utility
    {
        public static Microsoft.Xna.Framework.Color GetColorFromHex(string _)
            => Microsoft.Xna.Framework.Color.White;
    }
}

namespace ClassicUO.Game.Managers
{
    /// <summary>WASM stub: VoiceRecognitionManager (real one removed —
    /// uses Vosk native binary). GameController.cs subscribes to
    /// TextRecognized; GameScene.cs calls InitializeAsync. Dispose called too.</summary>
    public sealed class VoiceRecognitionManager : System.IDisposable
    {
        public static VoiceRecognitionManager Instance { get; } = new VoiceRecognitionManager();

        // `Action<string>` to match the upstream callback shape
        // (GameController.OnVoiceTextRecognized(string text)).
        public event System.Action<string> TextRecognized
        {
            add { /* no-op */ }
            remove { /* no-op */ }
        }

        public bool IsInitializing => false;
        public bool IsInitialized => false;
        public bool IsListening => false;

        public System.Threading.Tasks.Task InitializeAsync(string modelPath, bool startListeningAfter = false)
            => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task Reinitialize(params object[] _)
            => System.Threading.Tasks.Task.CompletedTask;

        public void ToggleListening() { }

        public void Dispose() { }
    }

    // NOTE: FriendliesSQLManager, ItemDatabaseManager and ItemInfo are NO
    // LONGER stubbed here. They are real WASM implementations now —
    // FriendliesSQLManager.Wasm.cs + ItemDatabaseManager.Wasm.cs, backed by
    // in-memory stores persisted to /Data/Client/UserData/ via
    // WasmUserDataStore (IDBFS) — and ItemInfo is the real
    // Game/Data/ItemInfo.cs (which already compiles under WASM). Removing
    // these stubs avoids the duplicate-type collision with the real impls.
}

namespace ClassicUO.Game.UI.MyraWindows
{
    /// <summary>WASM stub: ScriptManagerWindow in the MyraWindows
    /// namespace. Callers in TopBarGump.cs / HideHudManager.cs use
    /// .Show() static; callers in Profile.cs do `new ScriptManagerWindow()
    /// + .Load(xml)` + pass to UIManager.Add(IGui). The "new + Load"
    /// callers are #if-guarded out in Profile.cs (deep XML loader for
    /// the scripting UI — feature out-of-scope under BROWSER_WASM).
    /// So this stub only needs the static Show() and is left as a
    /// non-static class to keep `new ScriptManagerWindow()` legal in
    /// the unguarded paths (if any survive future re-vendors).</summary>
    public sealed class ScriptManagerWindow
    {
        // Singleton pattern. GameActions.CloseLegionScriptingGump():
        //   var window = ScriptManagerWindow.Instance;
        //   if (window != null && window.IsVisible) { window.IsVisible = false; ... }
        // Stub keeps IsVisible=false so the close-path early-outs.
        public static ScriptManagerWindow Instance { get; } = new ScriptManagerWindow();
        public bool IsVisible { get; set; }
        // v5.3 sync: GameActions.CloseLegionScriptingGump() now also checks
        // IsDisposed and calls Dispose(). IsVisible stays false so the guard
        // early-outs before either is reached; modelled for compilation.
        public bool IsDisposed { get; private set; }
        public void Dispose() { IsDisposed = true; }
        public static void Show() { }
        public void Load(object _) { }
    }

    /// <summary>WASM stub: RunningScriptsWindow. Same pattern as
    /// ScriptManagerWindow: Profile.cs `new` + Load() guarded out.</summary>
    public sealed class RunningScriptsWindow
    {
        public void Load(object _) { }
    }

    /// <summary>WASM stub: TazUOChatWindow in the MyraWindows namespace.
    /// Real one calls into TazUOChatManager which we've stubbed too.</summary>
    public static class TazUOChatWindow
    {
        public static void Show() { }
    }
}

/// <summary>WASM stub: SettingsScope enum at the GLOBAL namespace
/// (matches upstream SQLSettingsManager.cs which declares it after
/// its namespace block closes). Auto-generated SqlSettingAttribute
/// in ClassicUO.Configuration namespace references it unqualified, so
/// it has to live in global. Values mirror upstream exactly.</summary>
public enum SettingsScope
{
    Char,
    Account,
    Server,
    Global,
}

namespace ClassicUO.Game.Managers
{
    /// <summary>WASM SQLSettingsManager (real one removed — SQLite native
    /// binary not available under WASM). Provides the same async API surface
    /// that Client.cs + auto-generated Profile.SqlSettings.g.cs depend on,
    /// backed by an in-process Dictionary. Now PERSISTED: the dictionary is
    /// serialized to /Data/Client/UserData/sqlsettings.bin via
    /// WasmUserDataStore (IDBFS-backed, survives reload) — local-only, like
    /// the desktop SQLite settings db. (Discord-synced profile still persists
    /// the user-facing UI files separately.)</summary>
    public sealed class SQLSettingsManager : System.IDisposable
    {
        private const string STORE = "sqlsettings.bin";
        private const int STORE_VERSION = 1;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _mem
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        public SQLSettingsManager() { Load(); }

        public void Dispose() { Save(); _mem.Clear(); }

        private static string K(SettingsScope scope, string name) => $"{scope}:{name}";

        private void Load()
        {
            byte[] data = WasmUserDataStore.Load(STORE);
            if (data == null || data.Length < 8)
                return;
            try
            {
                using var ms = new System.IO.MemoryStream(data);
                using var r = new System.IO.BinaryReader(ms);
                if (r.ReadInt32() != STORE_VERSION)
                    return;
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string key = r.ReadString();
                    string val = r.ReadString();
                    _mem[key] = val;
                }
            }
            catch (System.Exception ex)
            {
                ClassicUO.Utility.Logging.Log.Error($"[sqlsettings] load failed (starting fresh): {ex.Message}");
                _mem.Clear();
            }
        }

        private void Save()
        {
            try
            {
                var snapshot = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>(_mem);
                using var ms = new System.IO.MemoryStream(snapshot.Count * 32 + 8);
                using (var w = new System.IO.BinaryWriter(ms))
                {
                    w.Write(STORE_VERSION);
                    w.Write(snapshot.Count);
                    foreach (var kv in snapshot)
                    {
                        w.Write(kv.Key ?? string.Empty);
                        w.Write(kv.Value ?? string.Empty);
                    }
                }
                WasmUserDataStore.Save(STORE, ms.ToArray());
            }
            catch (System.Exception ex)
            {
                ClassicUO.Utility.Logging.Log.Error($"[sqlsettings] save failed: {ex.Message}");
            }
        }

        // Sync API (real impl wraps async + GetAwaiter().GetResult())
        public string Get(SettingsScope scope, string name, string defaultValue = "")
        {
            return _mem.TryGetValue(K(scope, name), out var v) ? v : defaultValue;
        }

        public T Get<T>(SettingsScope scope, string name, T defaultValue = default)
        {
            if (!_mem.TryGetValue(K(scope, name), out var v)) return defaultValue;
            try { return (T)System.Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
            catch { return defaultValue; }
        }

        public void Set(SettingsScope scope, string name, string value)
        {
            _mem[K(scope, name)] = value ?? string.Empty;
            Save();
        }

        public void Set<T>(SettingsScope scope, string name, T value)
        {
            _mem[K(scope, name)] = System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            Save();
        }

        // Async API
        public System.Threading.Tasks.Task<string> GetAsync(SettingsScope scope, string name, string defaultValue = "")
            => System.Threading.Tasks.Task.FromResult(Get(scope, name, defaultValue));

        public System.Threading.Tasks.Task<T> GetAsync<T>(SettingsScope scope, string name, T defaultValue = default, System.Action<T> onComplete = null)
        {
            var v = Get(scope, name, defaultValue);
            onComplete?.Invoke(v);
            return System.Threading.Tasks.Task.FromResult(v);
        }

        public System.Threading.Tasks.Task<string> GetAsync(SettingsScope scope, string name, string defaultValue, System.Action<string> onComplete)
        {
            var v = Get(scope, name, defaultValue);
            onComplete?.Invoke(v);
            return System.Threading.Tasks.Task.FromResult(v);
        }

        public System.Threading.Tasks.Task GetAsyncOnMainThread<T>(SettingsScope scope, string name, T defaultValue, System.Action<T> onComplete)
        {
            onComplete?.Invoke(Get(scope, name, defaultValue));
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task SetAsync(SettingsScope scope, string name, string value)
        {
            Set(scope, name, value);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task SetAsync<T>(SettingsScope scope, string name, T value)
        {
            Set(scope, name, value);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task<System.Collections.Generic.Dictionary<string, string>> GetAllAsync(SettingsScope scope)
        {
            var prefix = scope.ToString() + ":";
            var result = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var kv in _mem)
                if (kv.Key.StartsWith(prefix))
                    result[kv.Key.Substring(prefix.Length)] = kv.Value;
            return System.Threading.Tasks.Task.FromResult(result);
        }

        public System.Collections.Generic.Dictionary<string, string> GetAll(SettingsScope scope)
            => GetAllAsync(scope).GetAwaiter().GetResult();
    }

    /// <summary>WASM stub — see WasmStubs.cs at the top of this file.</summary>
    public sealed class TazUOChatManager : System.IDisposable
    {
        // Singleton stub. The desktop build wires this to an IRC
        // client; the WASM build no-ops everything.
        public static TazUOChatManager Instance { get; } = new TazUOChatManager();

        private TazUOChatManager() { }

        public bool IsConnected => false;

        public void Init() { /* no-op in WASM build */ }

        public void Dispose() { /* no-op in WASM build */ }

        /// <summary>
        /// Profile.cs:693 default-name fallback. Real impl picks from
        /// a name pool; the stub returns a fixed placeholder.
        /// </summary>
        public static string GenerateFantasyName(int _syllableMin, int _syllableMax)
        {
            return "WebPlayer";
        }
    }
}

#endif
