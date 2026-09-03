// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using ClassicUO;
using System.Linq;
using ClassicUO.Common.Enums;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Network;
using SDL3;

namespace ClassicUO.Game
{
    // Public, minimal bridge surface for the web client's vertical-rail JS-macro
    // API (window.UORailBridge in rail.js). Lives INSIDE ClassicUO.Client so it
    // can reach the internal GameActions / World; the WASM wrapper's [JSExport]
    // WasmRailBridge marshals JS calls here on the game (deputy) thread.
    //
    // Phase 4 increment 1: read-only player state + a LOCAL system message
    // (journal/overhead) — neither mutates server state. Movement / targeting /
    // item-use verbs are added incrementally on top of this surface.
    public static class RailBridgeApi
    {
        // JSON of the local player's vitals + position. {"ingame":false} when no
        // player exists yet (pre-login / char screen) so the JS side can react.
        public static string GetPlayerJson()
        {
            var p = World.Instance?.Player;
            if (p == null)
            {
                return "{\"ingame\":false}";
            }

            var sb = new StringBuilder(192);
            sb.Append('{');
            sb.Append("\"ingame\":true,");
            sb.Append("\"name\":").Append(JsStr(p.Name)).Append(',');
            sb.Append("\"serial\":").Append(p.Serial.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"x\":").Append(p.X).Append(',');
            sb.Append("\"y\":").Append(p.Y).Append(',');
            sb.Append("\"z\":").Append(((int)p.Z).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"direction\":").Append((int)(p.Direction & Direction.Mask)).Append(',');
            sb.Append("\"hits\":").Append(p.Hits).Append(',');
            sb.Append("\"hitsmax\":").Append(p.HitsMax).Append(',');
            sb.Append("\"mana\":").Append(p.Mana).Append(',');
            sb.Append("\"manamax\":").Append(p.ManaMax).Append(',');
            sb.Append("\"stam\":").Append(p.Stamina).Append(',');
            sb.Append("\"stammax\":").Append(p.StaminaMax).Append(',');
            sb.Append("\"map\":").Append(World.Instance.MapIndex);
            sb.Append('}');
            return sb.ToString();
        }

        // ── Player-metrics telemetry (operator 2026-06-10) ───────────────────
        // The web loader polls this every few seconds while in-world; it returns
        // the gameplay-counter DELTAS since the last poll (tiles walked, distinct
        // NPCs seen) and resets them. PURE READ of World state — no mutation, no
        // server packet. Movement = Chebyshev steps between polls (UO allows
        // diagonals); a jump > 32 tiles (recall/teleport/map change) is ignored.
        // monsters_killed needs a death-event hook — a later increment. The seen
        // set is bounded so a long session can't grow it without limit.
        private static int _mtLastX = -1, _mtLastY = -1, _mtLastMap = -1;
        private static readonly HashSet<uint> _mtSeenMobiles = new HashSet<uint>();

        // ── Combat events (operator 2026-06-11) ──────────────────────────────
        // Pending counts incremented by the PacketHandlers death hooks
        // (DisplayDeath 0xAF / DeathScreen 0x2C), drained-and-reset by the next
        // CollectMetricsJson poll. The kill hook fires when the dying mobile is
        // the player's CURRENT attack target (TargetManager.LastAttack) — exact
        // for normal combat; a kill made without ever targeting (e.g. pure area
        // spell on an untargeted mob) is not credited. Player-vs-monster split
        // is a client-side heuristic (the UO protocol has no "is a player"
        // flag): human body + non-invulnerable notoriety counts as a player.
        // last-killer (own death) is the player's last attack target's name —
        // exact for mutual combat, null for a passive gank never fought back.
        private static int _mtPendingPlayerKills, _mtPendingMonsterKills, _mtPendingDeaths;
        private static string _mtLastKillerName;

        internal static void NoteMobileDeath(World world, uint serial)
        {
            var pl = world?.Player;
            if (pl == null || serial == 0 || world.TargetManager == null) { return; }
            if (world.TargetManager.LastAttack != serial) { return; }
            Mobile m = world.Mobiles.Get(serial);
            if (m == null) { return; }
            if (m.IsHuman && m.NotorietyFlag != NotorietyFlag.Invulnerable) { _mtPendingPlayerKills++; }
            else { _mtPendingMonsterKills++; }
        }

        // Death latch: some servers emit more than one 0x2C per death (death +
        // ghost-mode variants all hit the action != 1 branch), which would
        // double-count. Latch on the first one; CollectMetricsJson re-arms it
        // once the player is observably alive again.
        private static bool _mtDeathLatch;

        internal static void NotePlayerDeath(World world)
        {
            if (world?.Player == null) { return; }
            if (_mtDeathLatch) { return; }
            _mtDeathLatch = true;
            _mtPendingDeaths++;
            uint atk = world.TargetManager != null ? world.TargetManager.LastAttack : 0;
            Mobile m = atk != 0 ? world.Mobiles.Get(atk) : null;
            string name = m?.Name;
            if (!string.IsNullOrEmpty(name)) { _mtLastKillerName = name; }
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { sb.Append('\\').Append(c); }
                else if (c >= ' ') { sb.Append(c); }
            }
            sb.Append('"');
        }

        public static string CollectMetricsJson()
        {
            var w = World.Instance;
            var p = w?.Player;
            if (p == null)
            {
                return "{}";
            }

            int tiles = 0;
            int map = w.MapIndex;
            if (_mtLastMap == map && _mtLastX >= 0)
            {
                int step = Math.Max(Math.Abs(p.X - _mtLastX), Math.Abs(p.Y - _mtLastY));
                if (step > 0 && step <= 32) { tiles = step; }
            }
            _mtLastX = p.X; _mtLastY = p.Y; _mtLastMap = map;

            int npcs = 0;
            var mobs = w.Mobiles;
            if (mobs != null)
            {
                foreach (Mobile m in mobs.Values)
                {
                    if (m == null || m.Serial == p.Serial) { continue; }
                    if (_mtSeenMobiles.Add(m.Serial)) { npcs++; }
                }
                if (_mtSeenMobiles.Count > 4096) { _mtSeenMobiles.Clear(); }
            }

            if (_mtDeathLatch && !p.IsDead) { _mtDeathLatch = false; }

            int pk = _mtPendingPlayerKills, mk = _mtPendingMonsterKills, dd = _mtPendingDeaths;
            string killer = _mtLastKillerName;
            _mtPendingPlayerKills = 0; _mtPendingMonsterKills = 0; _mtPendingDeaths = 0; _mtLastKillerName = null;

            if (tiles == 0 && npcs == 0 && pk == 0 && mk == 0 && dd == 0)
            {
                return "{}";
            }

            var sb = new StringBuilder(96);
            sb.Append('{');
            bool first = true;
            void Num(string k, int v)
            {
                if (v <= 0) { return; }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append('"').Append(k).Append("\":").Append(v);
            }
            Num("tiles_moved", tiles);
            Num("npcs_seen", npcs);
            Num("players_killed", pk);
            Num("monsters_killed", mk);
            Num("deaths", dd);
            if (dd > 0 && !string.IsNullOrEmpty(killer))
            {
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("\"last_killer\":");
                AppendJsonString(sb, killer.Length > 40 ? killer.Substring(0, 40) : killer);
            }
            sb.Append('}');
            return sb.ToString();
        }

        // Print a LOCAL system message (shows in the journal) — does NOT talk to
        // the server, so it is safe for a self-running macro. hue<=0 → default.
        // Sprite smoothing (texel-AA): the rail slider writes the SAME profile
        // fields the in-game OptionsGump edits, so both UIs stay in sync and
        // the choice persists through the profile sync. GameScene re-reads the
        // profile every frame — the change is visible the instant the slider
        // moves. Clamped so a hostile bridge call can't persist garbage.
        public static void SetSpriteSmoothing(int level, bool full)
        {
            var profile = Configuration.ProfileManager.CurrentProfile;
            if (profile == null)
            {
                return;
            }

            profile.SpriteSmoothingLevel = Math.Clamp(level, 0, 100);
            profile.SpriteSmoothingFull = full;
        }

        public static string GetSpriteSmoothing()
        {
            var profile = Configuration.ProfileManager.CurrentProfile;
            if (profile == null)
            {
                return "0|false";
            }

            return $"{profile.SpriteSmoothingLevel}|{(profile.SpriteSmoothingFull ? "true" : "false")}";
        }

        public static void SysMessage(string text, int hue)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            ushort h = (hue <= 0 || hue > 0xFFFF) ? (ushort)946 : (ushort)hue;
            GameActions.Print(text, h);
        }

        // Command-sigil block (audit 2026-07-05; TBH retired 2026-07-14): page JS
        // reaching window.UORailBridge.Say() must NOT be able to inject a server command by
        // prefixing a sigil. EVERY leading command sigil is refused (the old `.tbh*` exception
        // is gone with TBH). Latent hardening — there is no known XSS vector
        // into this bridge (portal audit found none) — but the primitive is closed for parity.
        private static readonly char[] _cmdSigils = { '[', '.', '!', '+', '-', '#', '\\', '/', ',', '~', '=' };

        // Speak as the player (sends a server speech packet) — in-world only.
        public static void Say(string text)
        {
            if (string.IsNullOrEmpty(text) || World.Instance?.Player == null)
            {
                return;
            }

            var t = text.TrimStart();
            if (t.Length > 0 && System.Array.IndexOf(_cmdSigils, t[0]) >= 0)
            {
                return;
            }

            GameActions.Say(text);
        }

        // Double-click (use) an item/mobile by serial — in-world only. serial is
        // a double because JS numbers marshal as double (a uint serial < 2^53
        // is exact).
        public static void UseItem(double serial)
        {
            var w = World.Instance;
            if (w?.Player == null || serial <= 0)
            {
                return;
            }

            GameActions.DoubleClick(w, (uint)serial);
        }

        // Respond to a pending target cursor with a serial. Returns false (no-op)
        // when nothing is awaiting a target. serial is a double (JS number).
        public static bool Target(double serial)
        {
            var w = World.Instance;
            if (w?.Player == null || serial <= 0 || !w.TargetManager.IsTargeting)
            {
                return false;
            }

            w.TargetManager.Target((uint)serial);
            return true;
        }

        public static bool TargetSelf()
        {
            var w = World.Instance;
            if (w?.Player == null || !w.TargetManager.IsTargeting)
            {
                return false;
            }

            w.TargetManager.Target(w.Player.Serial);
            return true;
        }

        public static bool TargetLast()
        {
            var w = World.Instance;
            if (w?.Player == null || !w.TargetManager.IsTargeting)
            {
                return false;
            }

            w.TargetManager.TargetLast();
            return true;
        }

        // Use a skill by its numeric index (the JS Skills.* enum maps name->index).
        // The server validates usability; an out-of-range/non-usable index no-ops.
        public static void UseSkill(int index)
        {
            if (World.Instance?.Player == null || index < 0)
            {
                return;
            }

            GameActions.UseSkill(index);
        }

        // Cast a spell by its numeric index (the JS Spells.* enum / a raw index).
        public static void CastSpell(int index)
        {
            if (World.Instance?.Player == null || index <= 0)
            {
                return;
            }

            GameActions.CastSpell(index);
        }

        // Cancel a pending target cursor. Returns true if one was up.
        public static bool CancelTarget()
        {
            var w = World.Instance;
            if (w?.Player == null || !w.TargetManager.IsTargeting)
            {
                return false;
            }

            w.TargetManager.CancelTarget();
            return true;
        }

        // Attack a mobile by serial (LegionScripting API.Attack). serial is a
        // double (JS number). The server validates the target; an invalid serial
        // or a non-attackable target no-ops. In-world only.
        public static void Attack(double serial)
        {
            var w = World.Instance;
            if (w?.Player == null || serial <= 0)
            {
                return;
            }

            GameActions.Attack(w, (uint)serial);
        }

        // Turn the player to FACE a direction (LegionScripting API.Turn). dir is 0-7:
        // 0 N, 1 NE(Right), 2 E, 3 SE(Down), 4 S, 5 SW(Left), 6 W, 7 NW(Up). A UO Walk
        // request in a direction you are NOT facing TURNS in place (no step); if you are
        // already facing it, Walk would step forward — so we no-op that case to keep Turn
        // a pure facing change. In-world only.
        public static void Turn(double direction)
        {
            var p = World.Instance?.Player;
            if (p == null)
            {
                return;
            }

            Direction dir = (Direction)(((int)direction) & (int)Direction.Mask);

            // Only turn from an IDLE state. Player.Walk decides turn-vs-move from the
            // last QUEUED step's direction (oldDirection), not the current facing — so if
            // a step is already pending, a fresh Walk can be processed as a MOVE. Skipping
            // while Steps are queued keeps oldDirection == the current facing, guaranteeing
            // the else/turn branch (no positional step). A paced script (Turn; Pause; Turn)
            // still turns each time; only rapid-fire calls mid-step are skipped. Found by
            // the LS functional test walking the character on 8 back-to-back turns.
            if (p.Steps.Count != 0)
            {
                return;
            }

            if ((p.Direction & Direction.Mask) == dir)
            {
                return;
            }

            p.Walk(dir, false);
        }

        // Read the player's skills for the LegionScripting API.GetSkill() family.
        // Returns a JSON array [{index,name,value,base,cap,lock}] — value/base/cap
        // are the real 0.1-resolution floats, lock is 0=Up 1=Down 2=Locked. Manual
        // StringBuilder (no source-gen JSON context needed). Read-only; in-world.
        public static string GetSkillsJson()
        {
            var p = World.Instance?.Player;
            if (p?.Skills == null)
            {
                return "[]";
            }

            var sb = new StringBuilder(4096);
            sb.Append('[');
            bool first = true;
            for (int i = 0; i < p.Skills.Length; i++)
            {
                var sk = p.Skills[i];
                if (sk == null)
                {
                    continue;
                }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("{\"index\":").Append(i)
                  .Append(",\"name\":").Append(JsStr(sk.Name))
                  .Append(",\"value\":").Append(sk.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"base\":").Append(sk.Base.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"cap\":").Append(sk.Cap.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"lock\":").Append((int)sk.Lock)
                  .Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Open the client's OWN native options window (ModernOptionsGump). The rail
        // "Game Options" button calls this when the admin has NOT enabled the custom
        // HTML options panel (default), and the custom panel exposes it as a link so the
        // native window is always reachable. In-world only.
        public static void OpenNativeOptions()
        {
            var w = World.Instance;
            if (w?.Player == null)
            {
                return;
            }

            GameActions.OpenSettings(w, 0);
        }

        // ── Client settings bridge ───────────────────────────────────────────
        // The rail's Game Options panel surfaces the CLIENT'S OWN settings (the
        // same Profile the native Options gump edits) as HTML — exactly what the
        // official ClassicUO Web does. GetProfileJson dumps every simple-typed
        // Profile property; SetProfileValue writes one back by name. Reflection
        // (rooted via DynamicDependency for trimming/AOT). In-world only.
        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Profile))]
        public static string GetProfileJson()
        {
            var p = ProfileManager.CurrentProfile;
            if (p == null)
            {
                return "{}";
            }

            var sb = new StringBuilder(8192);
            sb.Append('{');
            bool first = true;
            foreach (var prop in typeof(Profile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var pt = prop.PropertyType;
                object raw;
                try { raw = prop.GetValue(p); } catch { continue; }

                if (pt == typeof(Microsoft.Xna.Framework.Point))
                {
                    // Point (GameWindowPosition/Size, PlayerOffset) → two scalar
                    // sub-keys "<Name>.X"/"<Name>.Y" so the scalar rail bridge
                    // (getProfile/setSetting) can round-trip each axis on its own.
                    var pv = (Microsoft.Xna.Framework.Point)raw;
                    if (!first) { sb.Append(','); }
                    sb.Append(JsStr(prop.Name + ".X")).Append(':').Append(pv.X.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(JsStr(prop.Name + ".Y")).Append(':').Append(pv.Y.ToString(CultureInfo.InvariantCulture));
                    first = false;
                    continue;
                }

                string jv;
                if (pt == typeof(bool)) { jv = ((bool)raw) ? "true" : "false"; }
                else if (pt == typeof(int) || pt == typeof(uint) || pt == typeof(ushort) || pt == typeof(short) || pt == typeof(byte) || pt == typeof(sbyte) || pt == typeof(long) || pt.IsEnum)
                {
                    try { jv = Convert.ToInt64(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); } catch { continue; }
                }
                else if (pt == typeof(float) || pt == typeof(double))
                {
                    try { jv = Convert.ToDouble(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); } catch { continue; }
                }
                else if (pt == typeof(string)) { jv = JsStr((string)raw ?? string.Empty); }
                else { continue; }

                if (!first) { sb.Append(','); }
                sb.Append(JsStr(prop.Name)).Append(':').Append(jv);
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(Profile))]
        public static void SetProfileValue(string name, string value)
        {
            var p = ProfileManager.CurrentProfile;
            if (p == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            // Compound Point axis: "<Prop>.X" / "<Prop>.Y" (GameWindowPosition,
            // GameWindowSize, PlayerOffset). Set the one component, then mirror the
            // native ModernOptionsGump apply for the viewport-geometry ones — a bare
            // profile write does not move/resize the world viewport.
            if (name.Length > 2 && name[name.Length - 2] == '.' && (name[name.Length - 1] == 'X' || name[name.Length - 1] == 'Y'))
            {
                var baseName = name.Substring(0, name.Length - 2);
                var pprop = typeof(Profile).GetProperty(baseName, BindingFlags.Public | BindingFlags.Instance);
                if (pprop != null && pprop.CanWrite && pprop.PropertyType == typeof(Microsoft.Xna.Framework.Point))
                {
                    try
                    {
                        int comp = int.Parse(value, CultureInfo.InvariantCulture);
                        var cur = (Microsoft.Xna.Framework.Point)pprop.GetValue(p);
                        var np = (name[name.Length - 1] == 'X')
                            ? new Microsoft.Xna.Framework.Point(comp, cur.Y)
                            : new Microsoft.Xna.Framework.Point(cur.X, comp);
                        pprop.SetValue(p, np);

                        var vp = UIManager.GetGump<WorldViewportGump>();
                        if (baseName == "GameWindowPosition") { vp?.SetGameWindowPosition(np); }
                        else if (baseName == "GameWindowSize") { vp?.ResizeGameWindow(np); }
                        // Same story for the paperdolls, and TazUO-only: both gumps
                        // reconcile the profile back to their own on-screen Location
                        // (PaperDollGump does it in PreDraw -- every frame), so a bare
                        // profile write is reverted before anyone can read it. Move the
                        // gump and the reconciliation then agrees with us. Found by the
                        // rail-options-audit smoke, which flagged PaperdollPosition.X/.Y
                        // as dead settings.
                        else if (baseName == "PaperdollPosition")
                        {
                            var pd = UIManager.GetGump<PaperDollGump>();
                            if (pd != null) { pd.X = np.X; pd.Y = np.Y; }
                        }
                        else if (baseName == "ModernPaperdollPosition")
                        {
                            var mpd = UIManager.GetGump<ModernPaperdoll>();
                            if (mpd != null) { mpd.X = np.X; mpd.Y = np.Y; }
                        }
                    }
                    catch { }
                }
                return;
            }

            var prop = typeof(Profile).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite)
            {
                return;
            }

            var t = prop.PropertyType;
            try
            {
                object v;
                if (t == typeof(bool)) { v = value == "true" || value == "1"; }
                else if (t == typeof(int)) { v = int.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(uint)) { v = uint.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(ushort)) { v = ushort.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(short)) { v = short.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(byte)) { v = byte.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(sbyte)) { v = sbyte.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(long)) { v = long.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(float)) { v = float.Parse(value, CultureInfo.InvariantCulture); }
                else if (t == typeof(double)) { v = double.Parse(value, CultureInfo.InvariantCulture); }
                else if (t.IsEnum) { v = Enum.ToObject(t, int.Parse(value, CultureInfo.InvariantCulture)); }
                else if (t == typeof(string)) { v = value; }
                else { return; }
                prop.SetValue(p, v);

                // Post-processing knobs need an explicit refresh: unlike the
                // per-frame profile reads (smoothing, shadows), GameScene only
                // resolves _filterMode inside SetPostProcessingSettings(),
                // which the in-game gump calls on change — mirror that here so
                // the rail combo applies hot instead of after relogin.
                if (name == "PostProcessingType" || name == "EnablePostProcessingEffects")
                {
                    Client.Game?.GetScene<ClassicUO.Game.Scenes.GameScene>()?.SetPostProcessingSettings();
                }
            }
            catch { }
        }

        // ── Hue palette bridge ───────────────────────────────────────────────
        // The Options "Hues" sections (notoriety / spell / message colours) are
        // real Profile ushort hue indices. To paint faithful swatches the JS side
        // needs the UO palette: one representative RGB per hue. Shade 8 matches
        // the native client's representative colour (HuesLoader.GetUnicodeFontColor
        // / the colour-picker boxes). Read straight from the loaded hues.mul via
        // the public GetColor16 (non-virtual — sidesteps the Mercury InlineArray
        // gsharedvt trap). Returns [] until the game files are loaded (login+).
        // Index i in the returned array is hue (i+1); hue 0 = "no hue" (handled
        // JS-side). 0xRRGGBB ints, alpha dropped.
        public static string GetHuePaletteJson()
        {
            var hues = Client.Game?.UO?.FileManager?.Hues;
            if (hues == null || hues.HuesCount <= 1)
            {
                return "[]";
            }

            int count = hues.HuesCount;
            var sb = new StringBuilder(count * 7 + 2);
            sb.Append('[');
            for (int h = 1; h < count; h++)
            {
                ushort c16 = hues.GetColor16(0x2000, (ushort)h); // shade 8 of hue h
                int r = ((c16 >> 10) & 0x1F) * 255 / 31;
                int g = ((c16 >> 5) & 0x1F) * 255 / 31;
                int b = (c16 & 0x1F) * 255 / 31;
                if (h > 1) { sb.Append(','); }
                sb.Append((r << 16) | (g << 8) | b);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ── Macro bridge ─────────────────────────────────────────────────────
        // The rail's Hotkeys panel shows the CLIENT'S OWN macros (World.Macros —
        // the same MacroManager the native Macro gump edits), exactly like the
        // official ClassicUO Web. Read-only dump: name, the key binding rendered
        // the way the native HotkeyBox does (KeysTranslator), and the action
        // chain (MacroType/MacroSubType + any text). In-world only; no mutation.
        public static string GetMacrosJson()
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null)
            {
                return "[]";
            }

            var sb = new StringBuilder(2048);
            sb.Append('[');
            bool firstM = true;
            foreach (var macro in w.Macros.GetAllMacros())
            {
                if (macro == null)
                {
                    continue;
                }

                if (!firstM)
                {
                    sb.Append(',');
                }
                firstM = false;

                sb.Append('{');
                sb.Append("\"name\":").Append(JsStr(macro.Name)).Append(',');
                sb.Append("\"key\":").Append(JsStr(MacroKeyLabel(macro))).Append(',');
                sb.Append("\"alt\":").Append(macro.Alt ? "true" : "false").Append(',');
                sb.Append("\"ctrl\":").Append(macro.Ctrl ? "true" : "false").Append(',');
                sb.Append("\"shift\":").Append(macro.Shift ? "true" : "false").Append(',');
                sb.Append("\"actions\":[");
                bool firstA = true;
                for (var action = (MacroObject)macro.Items; action != null; action = (MacroObject)action.Next)
                {
                    if (!firstA) { sb.Append(','); }
                    firstA = false;
                    AppendMacroAction(sb, action);
                }
                sb.Append("]}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ── Macro action catalog ─────────────────────────────────────────────
        // The data the native MacroControl builds its comboboxes from: every
        // MacroType (main combobox) with its SubMenuType (0 none / 1 sub-combobox
        // / 2 text field), and for SubMenuType==1 types the MacroSubType slice
        // GetBoundByCode exposes. Values are enum VALUES so the JS editor round-
        // trips them straight back through SetMacroAction. Pure read; safe out of
        // world (no World access). INVALID* sentinels are filtered (not real
        // actions). Built once when the Hotkeys panel opens.
        // MIRROR of MacroManager._skillTable (private there): position in the
        // UseSkill MacroSubType slice (Anatomy-relative) -> real skill index in
        // skills.mul. Keep byte-for-byte in sync with MacroManager if upstream
        // ever changes it — this is how UseSkill macros actually execute.
        private static readonly byte[] _useSkillTable =
        {
            1, 2, 35, 4, 6, 12,
            14, 15, 16, 19, 21, 56 /*imbuing*/,
            23, 3, 46, 9, 30, 22,
            48, 32, 33, 47, 36, 38
        };

        public static string GetMacroCatalogJson()
        {
            var sb = new StringBuilder(8192);
            sb.Append("{\"types\":[");
            bool first = true;
            foreach (MacroType code in Enum.GetValues(typeof(MacroType)))
            {
                string name = code.ToString();
                if (name.StartsWith("INVALID", StringComparison.Ordinal)) { continue; }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append('{');
                sb.Append("\"v\":").Append((int)code).Append(',');
                sb.Append("\"n\":").Append(JsStr(Readable(name))).Append(',');
                // SubMenuType isn't a static table — derive it exactly the way the
                // MacroObject ctor does, by creating a throwaway object.
                sb.Append("\"sub\":").Append((int)Macro.Create(code).SubMenuType);
                sb.Append('}');
            }
            sb.Append("],\"subs\":{");
            bool firstS = true;
            foreach (MacroType code in Enum.GetValues(typeof(MacroType)))
            {
                int count = 0, offset = 0;
                Macro.GetBoundByCode(code, ref count, ref offset);
                if (count <= 0) { continue; }
                if (!firstS) { sb.Append(','); }
                firstS = false;
                sb.Append('"').Append((int)code).Append("\":[");
                bool firstE = true;
                var skillsLoader = Client.Game?.UO?.FileManager?.Skills;
                for (int i = 0; i < count; i++)
                {
                    string subName;
                    if (code == MacroType.UseSkill && skillsLoader != null)
                    {
                        // Player report 2026-07-18: the Hotkeys→Skills list served the
                        // MacroSubType enum spellings ("AnimalTaming", "Imbuing") — it
                        // read as hardcoded and clashed with the shard's real skill
                        // names. Serve the LOADED fileset's names instead (skills.mul,
                        // resolved through the same table UseSkill execution uses) and
                        // DROP rows whose skill doesn't exist in this fileset. The
                        // emitted value is unchanged (still the MacroSubType), so
                        // execution round-trips exactly as before.
                        int skillIndex = i < _useSkillTable.Length ? _useSkillTable[i] : 0xFF;
                        if (skillIndex >= skillsLoader.SkillsCount) { continue; }
                        subName = skillsLoader.Skills[skillIndex].Name;
                    }
                    else
                    {
                        var st = (MacroSubType)(offset + i);
                        subName = Readable(st.ToString());
                    }
                    if (!firstE) { sb.Append(','); }
                    firstE = false;
                    sb.Append("{\"v\":").Append(offset + i).Append(",\"n\":").Append(JsStr(subName)).Append('}');
                }
                sb.Append(']');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        // ── Macro write ──────────────────────────────────────────────────────
        // Uses the macro system's OWN APIs (Macro.CreateEmptyMacro + PushToBack +
        // Remove + Save) — the same path the native Macro gump takes, so no
        // hand-crafted state and no corruption risk. In-world only. Returns false
        // (no-op) when not in-world or on a bad/duplicate name.
        public static bool AddMacro(string name)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null || string.IsNullOrWhiteSpace(name)) { return false; }
            name = name.Trim();
            if (w.Macros.FindMacro(name) != null) { return false; }
            var m = Macro.CreateEmptyMacro(name);
            w.Macros.PushToBack(m);
            w.Macros.Save();
            return true;
        }

        public static bool DeleteMacro(string name)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null || string.IsNullOrEmpty(name)) { return false; }
            var m = w.Macros.FindMacro(name);
            if (m == null) { return false; }
            w.Macros.Remove(m);
            w.Macros.Save();
            return true;
        }

        // Rename in place, keeping the macro's position in the list, its keybind and its whole
        // action chain. Refuses a blank name and refuses a name another macro already holds -
        // but not a clash with ITSELF, which is what a change of capitalisation looks like to a
        // name lookup.
        public static bool RenameMacro(string oldName, string newName)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null) { return false; }
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrWhiteSpace(newName)) { return false; }
            newName = newName.Trim();
            var m = w.Macros.FindMacro(oldName);
            if (m == null) { return false; }
            if (m.Name == newName) { return true; }
            var clash = w.Macros.FindMacro(newName);
            if (clash != null && !ReferenceEquals(clash, m)) { return false; }
            m.Name = newName;
            w.Macros.Save();
            return true;
        }

        // Round-trip latency to the shard, in ms, for the rail's readout.
        //
        // This is the SAME number the native NetworkStatsGump shows: the engine already sends a
        // 0x73 ping from GameScene on a timer and NetStatistics averages the last five replies.
        // The rail was built to display it and never had a source - its listener waited on a
        // `cuo:ping` event that nothing in the tree ever dispatched, so every client showed a dash.
        //
        // -1 (not 0) means "no reading": out of world, or connected but no reply sampled yet.
        // NetStatistics.Ping returns 0 in both of those cases, and 0 would render as a truthful
        // looking "0ms" - the one value that is never real.
        public static int GetPing()
        {
            var socket = AsyncNetClient.Socket;
            if (socket == null || !socket.IsConnected) { return -1; }
            uint ping = socket.Statistics.Ping;
            return ping == 0 ? -1 : (int)ping;
        }

        // ── Macro action-chain edit (mirrors native MacroControl.MacroEntry) ──
        // Granular, one mutation per call — exactly how the native gump edits a
        // macro's action list (each combobox change is an Insert+Remove). Avoids
        // any hand-rolled chain surgery; LinkedObject keeps the head pointer.
        // All in-world only; Save() after each change like the native path.

        // Append an empty (None) action — the native "Add" button. Won't stack a
        // trailing None (AddEmptyMacro guard).
        public static bool AddMacroAction(string name)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null) { return false; }
            var macro = w.Macros.FindMacro(name);
            if (macro == null) { return false; }
            var ob = (MacroObject)macro.Items;
            if (ob == null) { macro.Items = Macro.Create(MacroType.None); w.Macros.Save(); return true; }
            if (ob.Code == MacroType.None && ob.Next == null) { return false; }
            while (ob.Next != null)
            {
                var nx = (MacroObject)ob.Next;
                if (nx.Code == MacroType.None) { return false; }
                ob = nx;
            }
            macro.PushToBack(Macro.Create(MacroType.None));
            w.Macros.Save();
            return true;
        }

        // Remove the action at index; never leave the macro with no items (the
        // native gump always keeps at least one None row — see RemoveLastCommand).
        public static bool RemoveMacroActionAt(string name, int index)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null || index < 0) { return false; }
            var macro = w.Macros.FindMacro(name);
            if (macro == null) { return false; }
            var cur = (MacroObject)macro.Items;
            for (int i = 0; i < index && cur != null; i++) { cur = (MacroObject)cur.Next; }
            if (cur == null) { return false; }
            macro.Remove(cur);
            if (macro.Items == null) { macro.Items = Macro.Create(MacroType.None); }
            w.Macros.Save();
            return true;
        }

        // Set the action at index to (code[, sub][, text]). Recreates the
        // MacroObject via Macro.Create (so SubMenuType + default SubCode match the
        // native ctor), then replaces in place with Insert+Remove — identical to
        // MacroEntry.BoxOnOnOptionSelected. code==None removes the action. sub is
        // clamped to the type's GetBoundByCode range; text applies only to string
        // actions.
        public static bool SetMacroAction(string name, int index, int code, int sub, string text)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null || index < 0) { return false; }
            var macro = w.Macros.FindMacro(name);
            if (macro == null) { return false; }
            var cur = (MacroObject)macro.Items;
            for (int i = 0; i < index && cur != null; i++) { cur = (MacroObject)cur.Next; }
            if (cur == null) { return false; }
            if (!Enum.IsDefined(typeof(MacroType), code)) { return false; }
            var type = (MacroType)code;
            if (type == MacroType.None)
            {
                macro.Remove(cur);
                if (macro.Items == null) { macro.Items = Macro.Create(MacroType.None); }
                w.Macros.Save();
                return true;
            }
            var newObj = Macro.Create(type);
            if (newObj.SubMenuType == 1)
            {
                int count = 0, offset = 0;
                Macro.GetBoundByCode(type, ref count, ref offset);
                if (sub >= offset && sub < offset + count) { newObj.SubCode = (MacroSubType)sub; }
            }
            if (newObj is MacroObjectString mos && text != null) { mos.Text = text; }
            macro.Insert(cur, newObj);
            macro.Remove(cur);
            w.Macros.Save();
            return true;
        }

        // ── Macro keybind (mirrors native MacroControl.HotkeyBox) ─────────────
        // Assign a key + Alt/Ctrl/Shift to a macro. The JS rail captures the
        // browser KeyboardEvent and normalizes e.code to a token; SdlKeyFrom
        // Browser maps it to the SAME SDL_Keycode the Emscripten SDL layer feeds
        // the in-client MacroManager, so the binding actually fires in-world.
        // Refuses a combo already taken by another macro (like BoxOnHotkeyChanged).
        public static bool SetMacroKey(string name, string token, bool alt, bool ctrl, bool shift)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null || string.IsNullOrEmpty(name)) { return false; }
            var macro = w.Macros.FindMacro(name);
            if (macro == null) { return false; }
            var key = SdlKeyFromBrowser(token);
            if (key == SDL.SDL_Keycode.SDLK_UNKNOWN) { return false; }
            var clash = w.Macros.FindMacro(key, alt, ctrl, shift);
            if (clash != null && clash != macro) { return false; }
            macro.Key = key;
            macro.MouseButton = MouseButtonType.None;
            macro.WheelScroll = false;
            macro.Alt = alt;
            macro.Ctrl = ctrl;
            macro.Shift = shift;
            w.Macros.Save();
            return true;
        }

        public static bool ClearMacroKey(string name)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Macros == null || string.IsNullOrEmpty(name)) { return false; }
            var macro = w.Macros.FindMacro(name);
            if (macro == null) { return false; }
            macro.Key = SDL.SDL_Keycode.SDLK_UNKNOWN;
            macro.MouseButton = MouseButtonType.None;
            macro.WheelScroll = false;
            macro.Alt = macro.Ctrl = macro.Shift = false;
            w.Macros.Save();
            return true;
        }

        // Browser KeyboardEvent.code token -> SDL_Keycode. Printables map to their
        // lowercase ASCII value (== the SDL3 keycode for that key); specials match
        // the KeysTranslator table. Returns SDLK_UNKNOWN for anything we don't bind
        // (lone modifiers, numpad, unknown) so the caller no-ops.
        private static SDL.SDL_Keycode SdlKeyFromBrowser(string token)
        {
            if (string.IsNullOrEmpty(token)) { return SDL.SDL_Keycode.SDLK_UNKNOWN; }
            if (token.Length == 1)
            {
                char c = char.ToLowerInvariant(token[0]);
                if (c >= 32 && c < 127) { return (SDL.SDL_Keycode)c; }
                return SDL.SDL_Keycode.SDLK_UNKNOWN;
            }
            switch (token)
            {
                case "F1": return SDL.SDL_Keycode.SDLK_F1;
                case "F2": return SDL.SDL_Keycode.SDLK_F2;
                case "F3": return SDL.SDL_Keycode.SDLK_F3;
                case "F4": return SDL.SDL_Keycode.SDLK_F4;
                case "F5": return SDL.SDL_Keycode.SDLK_F5;
                case "F6": return SDL.SDL_Keycode.SDLK_F6;
                case "F7": return SDL.SDL_Keycode.SDLK_F7;
                case "F8": return SDL.SDL_Keycode.SDLK_F8;
                case "F9": return SDL.SDL_Keycode.SDLK_F9;
                case "F10": return SDL.SDL_Keycode.SDLK_F10;
                case "F11": return SDL.SDL_Keycode.SDLK_F11;
                case "F12": return SDL.SDL_Keycode.SDLK_F12;
                case "Up": return SDL.SDL_Keycode.SDLK_UP;
                case "Down": return SDL.SDL_Keycode.SDLK_DOWN;
                case "Left": return SDL.SDL_Keycode.SDLK_LEFT;
                case "Right": return SDL.SDL_Keycode.SDLK_RIGHT;
                case "Return": return SDL.SDL_Keycode.SDLK_RETURN;
                case "Esc": return SDL.SDL_Keycode.SDLK_ESCAPE;
                case "Space": return SDL.SDL_Keycode.SDLK_SPACE;
                case "Tab": return SDL.SDL_Keycode.SDLK_TAB;
                case "Backspace": return SDL.SDL_Keycode.SDLK_BACKSPACE;
                case "Del": return SDL.SDL_Keycode.SDLK_DELETE;
                case "Ins": return SDL.SDL_Keycode.SDLK_INSERT;
                case "Home": return SDL.SDL_Keycode.SDLK_HOME;
                case "End": return SDL.SDL_Keycode.SDLK_END;
                case "PageUp": return SDL.SDL_Keycode.SDLK_PAGEUP;
                case "PageDown": return SDL.SDL_Keycode.SDLK_PAGEDOWN;
                default: return SDL.SDL_Keycode.SDLK_UNKNOWN;
            }
        }

        private static SDL.SDL_Keymod MacroMod(Macro macro)
        {
            var mod = SDL.SDL_Keymod.SDL_KMOD_NONE;
            if (macro.Alt) { mod |= SDL.SDL_Keymod.SDL_KMOD_ALT; }
            if (macro.Ctrl) { mod |= SDL.SDL_Keymod.SDL_KMOD_CTRL; }
            if (macro.Shift) { mod |= SDL.SDL_Keymod.SDL_KMOD_SHIFT; }
            return mod;
        }

        private static string MacroKeyLabel(Macro macro)
        {
            if (macro.MouseButton != MouseButtonType.None)
            {
                return KeysTranslator.GetMouseButton(macro.MouseButton, MacroMod(macro));
            }
            if (macro.WheelScroll)
            {
                return KeysTranslator.GetMouseWheel(macro.WheelUp, MacroMod(macro));
            }
            if (macro.Key == SDL.SDL_Keycode.SDLK_UNKNOWN)
            {
                return "Not bound";
            }
            return KeysTranslator.TryGetKey(macro.Key, MacroMod(macro));
        }

        private static string MacroActionLabel(MacroObject action)
        {
            string label = Readable(action.Code.ToString());
            if (action.SubCode != MacroSubType.MSC_NONE)
            {
                label += " — " + Readable(action.SubCode.ToString());
            }
            if (action.HasString() && action is MacroObjectString mos && !string.IsNullOrEmpty(mos.Text))
            {
                label += ": " + mos.Text;
            }
            return label;
        }

        // Structured serialization of one macro action for the rail's action
        // editor: enum VALUES (code/sub) so the JS dropdowns round-trip exactly
        // through SetMacroAction, plus the readable label the native gump shows.
        private static void AppendMacroAction(StringBuilder sb, MacroObject action)
        {
            sb.Append('{');
            sb.Append("\"code\":").Append((int)action.Code).Append(',');
            sb.Append("\"codeName\":").Append(JsStr(Readable(action.Code.ToString()))).Append(',');
            sb.Append("\"subType\":").Append((int)action.SubMenuType).Append(',');
            sb.Append("\"sub\":").Append((int)action.SubCode).Append(',');
            sb.Append("\"text\":").Append(JsStr(action.HasString() && action is MacroObjectString s ? s.Text : string.Empty)).Append(',');
            sb.Append("\"label\":").Append(JsStr(MacroActionLabel(action)));
            sb.Append('}');
        }

        // MacroType/MacroSubType enum names look like "Open"/"MSC_Anatomy"; drop
        // the MSC_ prefix so the action list reads cleanly.
        private static string Readable(string enumName)
        {
            if (string.IsNullOrEmpty(enumName))
            {
                return enumName;
            }
            if (enumName.StartsWith("MSC_", StringComparison.Ordinal))
            {
                enumName = enumName.Substring(4);
            }
            // Humanize CamelCase for display: insert a space at each lowercase/
            // digit -> uppercase boundary (WarPeace -> "War Peace", MagicArrow ->
            // "Magic Arrow"). Player report 2026-07-18 was the Skills list (now
            // sourced from skills.mul); this covers the SIBLINGS — the Spells sub
            // list and the Macros editor's MacroType dropdown. The JS side matches
            // on norm() (strips non-alphanumerics), so the spaces never break the
            // value round-trip.
            var hb = new StringBuilder(enumName.Length + 8);
            for (int i = 0; i < enumName.Length; i++)
            {
                char c = enumName[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(enumName[i - 1]) || char.IsDigit(enumName[i - 1])))
                {
                    hb.Append(' ');
                }
                hb.Append(c);
            }
            return hb.ToString();
        }

        // ── Agents engine — real item read/move/equip primitives ────────────────
        // Back the rail Agents panels (Dress / Organizer / Autoloot) with REAL game
        // state + actions instead of UI stubs. TazUO uses the World.Instance
        // singleton. Reads enumerate worn gear + a container's contents; actions
        // equip / move / grab via the same GameActions the desktop client uses.

        public static string GetEquippedItemsJson()
        {
            var p = World.Instance?.Player;
            if (p == null)
            {
                return "[]";
            }

            var sb = new StringBuilder(256);
            sb.Append('[');
            bool first = true;
            for (LinkedObject i = p.Items; i != null; i = i.Next)
            {
                var it = (Item)i;
                Layer lay = it.Layer;
                if (lay == Layer.Invalid || lay == Layer.Backpack || lay == Layer.Mount ||
                    lay == Layer.Hair || lay == Layer.Beard || lay == Layer.Face ||
                    (byte)lay >= (byte)Layer.ShopBuyRestock)
                {
                    continue;
                }
                if (!first) { sb.Append(','); }
                first = false;
                AppendItemJson(sb, it);
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Per-slot durability of worn gear — the REAL backing for the Agents >
        // Durability panel. Reads each equipped item's OPL ("Durability cur / max",
        // cliloc 1060639). cur/max are -1 when the item has no durability line.
        // Read-only; never mutates game state.
        public static string GetEquipmentDurabilityJson()
        {
            var w = World.Instance;
            var p = w?.Player;
            if (p == null)
            {
                return "[]";
            }
            var sb = new StringBuilder(256);
            sb.Append('[');
            bool first = true;
            for (LinkedObject i = p.Items; i != null; i = i.Next)
            {
                var it = (Item)i;
                Layer lay = it.Layer;
                if (lay == Layer.Invalid || lay == Layer.Backpack || lay == Layer.Mount ||
                    lay == Layer.Hair || lay == Layer.Beard || lay == Layer.Face ||
                    (byte)lay >= (byte)Layer.ShopBuyRestock)
                {
                    continue;
                }
                int cur = -1, max = -1;
                if (w.OPL.TryGetNameAndData(it.Serial, out _, out string data))
                {
                    TryParseDurability(data, out cur, out max);
                }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append('{');
                sb.Append("\"serial\":").Append(it.Serial.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"graphic\":").Append(((int)it.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"hue\":").Append(((int)it.Hue).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"layer\":").Append(((int)lay).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"cur\":").Append(cur.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"max\":").Append(max.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"name\":").Append(JsStr(CleanItemName(it.Name)));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Scan an OPL data blob for the "Durability <cur> / <max>" line. Reads up
        // to two integers on that line ('/' and spaces are skipped); one number →
        // treated as max. No regex (AOT-friendly under Mercury).
        private static void TryParseDurability(string data, out int cur, out int max)
        {
            cur = -1; max = -1;
            if (string.IsNullOrEmpty(data))
            {
                return;
            }
            int idx = data.IndexOf("Durability", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return;
            }
            int i = idx;
            int a = ReadIntOnLine(data, ref i);
            if (a < 0)
            {
                return;
            }
            int b = ReadIntOnLine(data, ref i);
            if (b >= 0) { cur = a; max = b; } else { max = a; }
        }

        // Advance i to the next run of digits on the CURRENT line (stop at '\n');
        // returns the parsed int, or -1 if the line ends first.
        private static int ReadIntOnLine(string s, ref int i)
        {
            while (i < s.Length && !char.IsDigit(s[i]))
            {
                if (s[i] == '\n') { return -1; }
                i++;
            }
            if (i >= s.Length || !char.IsDigit(s[i]))
            {
                return -1;
            }
            int v = 0;
            while (i < s.Length && char.IsDigit(s[i]))
            {
                v = v * 10 + (s[i] - '0');
                i++;
            }
            return v;
        }

        public static string GetContainerItemsJson(double serial)
        {
            var w = World.Instance;
            if (w?.Player == null)
            {
                return "[]";
            }

            uint s = serial <= 0 ? BackpackSerial() : (uint)serial;
            Item cont = w.Items.Get(s);
            if (cont == null)
            {
                return "[]";
            }

            var sb = new StringBuilder(512);
            sb.Append('[');
            bool first = true;
            for (LinkedObject i = cont.Items; i != null; i = i.Next)
            {
                var it = (Item)i;
                if (!first) { sb.Append(','); }
                first = false;
                AppendItemJson(sb, it);
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static double GetBackpackSerial()
        {
            return BackpackSerial();
        }

        public static bool EquipItem(double serial)
        {
            var w = World.Instance;
            if (w?.Player == null || serial <= 0 || Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                return false;
            }
            if (!GameActions.PickUp(w, (uint)serial, 0, 0, 1))
            {
                return false;
            }
            GameActions.Equip(w);
            return true;
        }

        public static bool MoveItem(double serial, double dest, int amount)
        {
            var w = World.Instance;
            if (w?.Player == null || serial <= 0 || Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                return false;
            }
            uint d = dest <= 0 ? BackpackSerial() : (uint)dest;
            if (d == 0)
            {
                return false;
            }
            if (!GameActions.PickUp(w, (uint)serial, 0, 0, amount > 0 ? amount : -1))
            {
                return false;
            }
            GameActions.DropItem((uint)serial, 0xFFFF, 0xFFFF, 0, d);
            return true;
        }

        public static bool GrabItem(double serial, int amount, double bag)
        {
            var w = World.Instance;
            if (w?.Player == null || serial <= 0)
            {
                return false;
            }
            uint b = bag <= 0 ? BackpackSerial() : (uint)bag;
            if (b == 0)
            {
                return false;
            }
            Item it = w.Items.Get((uint)serial);
            ushort amt = (ushort)(amount > 0 ? amount : (it != null ? it.Amount : (ushort)1));
            GameActions.GrabItem(w, (uint)serial, amt, b);
            return true;
        }

        // ── Target picker — official autoloot "+" enters TARGET mode; the user
        // clicks an item and its TYPE is added. TazUO's SetTargeting takes an
        // Action<object> (the picked entity, or null on cancel).
        private static string _railTargetResult;
        private static bool _railTargetActive;

        public static bool RequestRailTarget()
        {
            var w = World.Instance;
            if (w?.Player == null)
            {
                return false;
            }
            _railTargetResult = null;
            _railTargetActive = true;
            w.TargetManager.SetTargeting((object obj) =>
            {
                _railTargetActive = false;
                if (obj is Item it)
                {
                    _railTargetResult = TargetPickJson("item", it.Serial, (int)it.Graphic, (int)it.Hue, it.Name);
                }
                else if (obj is Mobile mob)
                {
                    _railTargetResult = TargetPickJson("mobile", mob.Serial, (int)mob.Graphic, (int)mob.Hue, mob.Name);
                }
                else
                {
                    _railTargetResult = "{\"cancelled\":true}";
                }
            });
            return true;
        }

        // JSON for a picked target — "kind" lets the rail tell items from mobiles
        // (autoloot/restock want items; the Lists agent wants mobiles).
        private static string TargetPickJson(string kind, uint serial, int graphic, int hue, string name)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"kind\":\"").Append(kind).Append("\",");
            sb.Append("\"serial\":").Append(serial.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"graphic\":").Append(graphic.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"hue\":").Append(hue.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"name\":").Append(JsStr(CleanItemName(name)));
            sb.Append('}');
            return sb.ToString();
        }

        public static string PollRailTargetJson()
        {
            if (_railTargetResult != null)
            {
                string r = _railTargetResult;
                _railTargetResult = null;
                return r;
            }
            var w = World.Instance;
            if (!_railTargetActive || (w != null && !w.TargetManager.IsTargeting))
            {
                _railTargetActive = false;
                return "{\"cancelled\":true}";
            }
            return "{\"pending\":true}";
        }

        public static void CancelRailTarget()
        {
            _railTargetActive = false;
            var w = World.Instance;
            if (w != null && w.TargetManager.IsTargeting)
            {
                w.TargetManager.CancelTarget();
            }
        }

        // REAL item art (static graphic) as base64 RGBA + dims. Raw ArtLoader
        // (FileManager.Arts, NOT the renderer wrapper). 0xAABBGGRR → RGBA bytes.
        public static string GetItemArtJson(int graphic)
        {
            try
            {
                var arts = Client.Game?.UO?.FileManager?.Arts;
                if (arts == null || graphic <= 0)
                {
                    return "{}";
                }
                var info = arts.GetArt((uint)(graphic + 0x4000));
                int w = info.Width, h = info.Height;
                if (w <= 0 || h <= 0 || info.Pixels.Length < w * h)
                {
                    return "{}";
                }
                var bytes = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    uint p = info.Pixels[i];
                    bytes[i * 4]     = (byte)(p & 0xFF);
                    bytes[i * 4 + 1] = (byte)((p >> 8) & 0xFF);
                    bytes[i * 4 + 2] = (byte)((p >> 16) & 0xFF);
                    bytes[i * 4 + 3] = (byte)((p >> 24) & 0xFF);
                }
                return "{\"w\":" + w + ",\"h\":" + h + ",\"rgba\":\"" + Convert.ToBase64String(bytes) + "\"}";
            }
            catch
            {
                return "{}";
            }
        }

        private static string CleanItemName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(name.Length);
            bool inTag = false;
            foreach (char c in name)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) { sb.Append(c); }
            }
            return sb.ToString().Trim();
        }

        // ── Lists agent (Friends / Enemies) ─────────────────────────────────────
        // TazUO has a real FriendsListManager (singleton) AND an Ignore list, so
        // both columns are live here. AddFriend(Mobile)/RemoveFriend auto-save.
        public static bool ListsHasFriends()
        {
            return true;
        }

        public static string GetFriendsJson()
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            bool first = true;
            foreach (var f in FriendsListManager.Instance.GetFriends())
            {
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("{\"serial\":").Append(f.Serial.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"name\":").Append(JsStr(f.Name)).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static bool AddFriend(double serial)
        {
            var m = World.Instance?.Get((uint)serial) as Mobile;
            if (m == null)
            {
                return false;
            }
            return FriendsListManager.Instance.AddFriend(m);
        }

        public static bool RemoveFriendBySerial(double serial)
        {
            return FriendsListManager.Instance.RemoveFriend((uint)serial);
        }

        // Ignore list ("Enemies") — World.IgnoreManager (SaveIgnoreList not called
        // internally, so persist explicitly).
        public static string GetIgnoredJson()
        {
            var w = World.Instance;
            if (w == null)
            {
                return "[]";
            }
            var sb = new StringBuilder(128);
            sb.Append('[');
            bool first = true;
            foreach (string name in w.IgnoreManager.IgnoredCharsList)
            {
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("{\"name\":").Append(JsStr(name)).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static bool AddIgnore(double serial)
        {
            var w = World.Instance;
            var e = w?.Get((uint)serial);
            if (e == null)
            {
                return false;
            }
            int before = w.IgnoreManager.IgnoredCharsList.Count;
            w.IgnoreManager.AddIgnoredTarget(e);
            w.IgnoreManager.SaveIgnoreList();
            return w.IgnoreManager.IgnoredCharsList.Count > before;
        }

        public static bool RemoveIgnore(string name)
        {
            var w = World.Instance;
            if (w == null || string.IsNullOrEmpty(name))
            {
                return false;
            }
            w.IgnoreManager.RemoveIgnoredTarget(name);
            w.IgnoreManager.SaveIgnoreList();
            return true;
        }

        // ── UOAM World Map peers (native in-game markers) ───────────────────────
        // The rail's /uoam WS client pushes the live peer list here; WorldMapGump
        // draws them next to party members via DrawWMEntity. Records are a compact
        // delimiter-separated string (record sep \x1e, field sep \x1f:
        // name\x1fx\x1fy\x1fmap) — no JSON serializer (AOT-safe under Mercury).
        // Synthetic serials (0xF0000000+) never collide with real entities.
        internal static List<WMapEntity> UoamPeers = new List<WMapEntity>();

        public static void SetUoamPeers(string data)
        {
            var list = new List<WMapEntity>();
            if (!string.IsNullOrEmpty(data))
            {
                uint syn = 0xF0000000;
                foreach (string rec in data.Split('\x1e'))
                {
                    string[] f = rec.Split('\x1f');
                    if (f.Length < 4) { continue; }
                    if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) { continue; }
                    if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) { continue; }
                    if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int map)) { continue; }
                    list.Add(new WMapEntity(syn++) { Name = f[0], X = x, Y = y, Map = map, IsGuild = false });
                }
            }
            UoamPeers = list;
        }

        // ── Filters agent (sound filtering) — TazUO SoundFilterManager ──────────
        // Block annoying sound effects: the rail shows recently-played sounds
        // (Client.Game.Audio.LastPlayedSounds) + the current filter list; AddFilter
        // / RemoveFilter auto-persist. TUO-only (CUO has no SoundFilterManager —
        // see the CUO RailBridgeApi, FiltersHasEngine() = false there).
        public static bool FiltersHasEngine()
        {
            return true;
        }

        public static string GetRecentSoundsJson()
        {
            var sb = new StringBuilder(160);
            sb.Append('[');
            var audio = Client.Game?.Audio;
            if (audio != null)
            {
                bool first = true;
                foreach ((int id, string name) in audio.LastPlayedSounds.GetItems())
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append("{\"id\":").Append(id.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"name\":").Append(JsStr(name ?? string.Empty)).Append('}');
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string GetSoundFiltersJson()
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            bool first = true;
            foreach (int id in SoundFilterManager.Instance.FilteredSounds)
            {
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append(id.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static void AddSoundFilter(int id)
        {
            SoundFilterManager.Instance.AddFilter(id);
        }

        public static void RemoveSoundFilter(int id)
        {
            SoundFilterManager.Instance.RemoveFilter(id);
        }

        // ── Chat agent (native UO conference/chat system) ───────────────────────
        // The UO "Chat" runs over the game socket and exists in both clients. State
        // (ChatIsEnabled / Channels / CurrentChannelName) is server-driven; the rail
        // reads it and sends the standard 0xB3/0xB5 chat packets (network-only, so
        // safe from the bridge/deputy thread — no UIManager). Messages still surface
        // in the in-game journal. When the server hasn't enabled chat the rail says so.
        public static string GetChatStateJson()
        {
            var cm = World.Instance?.ChatManager;
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"status\":").Append(cm != null ? ((int)cm.ChatIsEnabled).ToString(CultureInfo.InvariantCulture) : "0");
            sb.Append(",\"current\":").Append(JsStr(cm?.CurrentChannelName ?? string.Empty));
            sb.Append(",\"channels\":[");
            if (cm != null)
            {
                bool first = true;
                foreach (KeyValuePair<string, ChatChannel> kv in cm.Channels)
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    string nm = kv.Value?.Name ?? kv.Key;
                    bool pw = kv.Value != null && kv.Value.HasPassword;
                    sb.Append("{\"name\":").Append(JsStr(nm)).Append(",\"pw\":").Append(pw ? "true" : "false").Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static bool ChatRegisterName(string name)
        {
            var cm = World.Instance?.ChatManager;
            if (cm == null || cm.ChatIsEnabled == ChatStatus.Disabled) { return false; }
            AsyncNetClient.Socket.Send_OpenChat(name ?? string.Empty);
            return true;
        }

        public static bool ChatJoinChannel(string name, string password)
        {
            var cm = World.Instance?.ChatManager;
            if (cm == null || cm.ChatIsEnabled == ChatStatus.Disabled || string.IsNullOrEmpty(name)) { return false; }
            AsyncNetClient.Socket.Send_ChatJoinCommand(name, string.IsNullOrEmpty(password) ? null : password);
            return true;
        }

        public static bool ChatLeaveChannel()
        {
            var cm = World.Instance?.ChatManager;
            if (cm == null || cm.ChatIsEnabled == ChatStatus.Disabled) { return false; }
            AsyncNetClient.Socket.Send_ChatLeaveChannelCommand();
            return true;
        }

        public static bool ChatSend(string msg)
        {
            var cm = World.Instance?.ChatManager;
            if (cm == null || cm.ChatIsEnabled == ChatStatus.Disabled || string.IsNullOrEmpty(msg)) { return false; }

            // SECURITY: the SAME gate as Say(), and for the same reason. The audit that added
            // that gate reasoned about window.UORailBridge.say(); this export was missed, and it
            // has identical reach - LegionScripting maps PartyMsg/GuildMsg/AllyMsg/GlobalMsg
            // straight onto chatSend, so a player's own macro can put arbitrary text on the wire
            // as the logged-in account. Whether the shard's chat parser can reach a command is
            // not a property this client should depend on: a gate that holds only while a server
            // setting stays off is not a gate. No legitimate chat message begins with a command
            // sigil, so refusing them costs nothing.
            //
            // NOTE FOR A FUTURE SYNC: this fork calls AsyncNetClient where cuo/mini call
            // NetClient, which is why a single search-and-replace across the three trees fixed
            // only two of them. Check all three by GREPPING FOR THE GATE, not for the send.
            var t = msg.TrimStart();
            if (t.Length > 0 && System.Array.IndexOf(_cmdSigils, t[0]) >= 0)
            {
                // 🚨 NO WHITELIST HERE, and that is deliberate: this client's Say() refuses EVERY
                // command sigil because a full client never needs to emit a minigame verb. The
                // mini declares _allowedVerbs for `.td`/`.rm`; importing that here would LOOSEN
                // this gate. Caught by the compiler when the first version of this patch
                // referenced a symbol that only exists in the mini - mirror each client's own
                // Say(), never assume the three files agree.
                return false;
            }

            AsyncNetClient.Socket.Send_ChatMessageCommand(msg);
            return true;
        }

        // ── Perception / navigation / UI verbs (operator 2026-06-11) ─────────────
        // Broaden the JS-macro surface beyond inventory/skills into: reading the
        // journal, scanning nearby entities, detecting a target cursor, pathfinding
        // to a coordinate, reading + replying to server gumps, and a "natural
        // human" mouse path (move the cursor, then click). Read verbs are pure
        // World/UI reads; action verbs reuse the native client's own code paths.
        // In-world only. All are gated by the rail allow-list + per-shard policy.

        // Recent journal lines, oldest→newest. max clamped 1..200 (default 50).
        public static string GetJournalJson(int max)
        {
            var entries = JournalManager.Entries;
            if (entries == null || entries.Count == 0)
            {
                return "[]";
            }
            int take = max <= 0 ? 50 : (max > 200 ? 200 : max);
            int total = entries.Count;
            int start = total > take ? total - take : 0;
            var sb = new StringBuilder(take * 48);
            sb.Append('[');
            bool first = true;
            for (int i = start; i < total; i++)
            {
                JournalEntry e = entries[i];
                if (e == null) { continue; }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append('{');
                sb.Append("\"text\":").Append(JsStr(e.Text)).Append(',');
                sb.Append("\"name\":").Append(JsStr(e.Name)).Append(',');
                sb.Append("\"hue\":").Append(((int)e.Hue).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"type\":").Append(((int)e.TextType).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"time\":").Append(JsStr(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Nearby entities within `range` tiles (Chebyshev) of the player. kind:
        // 0 = mobiles+items, 1 = mobiles only, 2 = items only. Capped at 200.
        public static string ScanWorldJson(int range, int kind)
        {
            var w = World.Instance;
            var p = w?.Player;
            if (p == null)
            {
                return "[]";
            }
            int r = range <= 0 ? 12 : (range > 64 ? 64 : range);
            int px = p.X, py = p.Y;
            int count = 0;
            const int CAP = 200;
            var sb = new StringBuilder(1024);
            sb.Append('[');
            bool first = true;

            if (kind != 2 && w.Mobiles != null)
            {
                foreach (Mobile m in w.Mobiles.Values)
                {
                    if (m == null || m.Serial == p.Serial) { continue; }
                    int dist = Math.Max(Math.Abs(m.X - px), Math.Abs(m.Y - py));
                    if (dist > r) { continue; }
                    if (count >= CAP) { break; }
                    if (!first) { sb.Append(','); }
                    first = false;
                    count++;
                    int hp = m.HitsMax > 0 ? (m.Hits * 100 / m.HitsMax) : 0;
                    sb.Append("{\"kind\":\"mobile\",");
                    sb.Append("\"serial\":").Append(m.Serial.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"name\":").Append(JsStr(CleanItemName(m.Name))).Append(',');
                    sb.Append("\"graphic\":").Append(((int)m.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"hue\":").Append(((int)m.Hue).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"x\":").Append(m.X).Append(',');
                    sb.Append("\"y\":").Append(m.Y).Append(',');
                    sb.Append("\"dist\":").Append(dist).Append(',');
                    sb.Append("\"hp\":").Append(hp).Append(',');
                    sb.Append("\"isHuman\":").Append(m.IsHuman ? "true" : "false").Append(',');
                    sb.Append("\"notoriety\":").Append((int)m.NotorietyFlag).Append(',');
                    sb.Append("\"dead\":").Append(m.IsDead ? "true" : "false");
                    sb.Append('}');
                }
            }

            if (kind != 1 && w.Items != null)
            {
                foreach (Item it in w.Items.Values)
                {
                    if (it == null || it.OnGround == false) { continue; }
                    int dist = Math.Max(Math.Abs(it.X - px), Math.Abs(it.Y - py));
                    if (dist > r) { continue; }
                    if (count >= CAP) { break; }
                    if (!first) { sb.Append(','); }
                    first = false;
                    count++;
                    sb.Append("{\"kind\":\"item\",");
                    sb.Append("\"serial\":").Append(it.Serial.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"name\":").Append(JsStr(CleanItemName(it.Name))).Append(',');
                    sb.Append("\"graphic\":").Append(((int)it.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"hue\":").Append(((int)it.Hue).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"amount\":").Append(((int)it.Amount).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"x\":").Append(it.X).Append(',');
                    sb.Append("\"y\":").Append(it.Y).Append(',');
                    sb.Append("\"dist\":").Append(dist);
                    sb.Append('}');
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        // True while a target cursor is up.
        public static bool IsTargeting()
        {
            var w = World.Instance;
            return w?.Player != null && w.TargetManager != null && w.TargetManager.IsTargeting;
        }

        // Pathfind-walk to a map coordinate. distance = how close to stop.
        public static bool WalkTo(int x, int y, int distance)
        {
            var p = World.Instance?.Player;
            if (p == null || p.Pathfinder == null)
            {
                return false;
            }
            return p.Pathfinder.WalkTo(x, y, p.Z, distance < 0 ? 0 : distance);
        }

        // Stop any active auto-walk.
        public static bool StopWalk()
        {
            var p = World.Instance?.Player;
            if (p == null || p.Pathfinder == null)
            {
                return false;
            }
            p.Pathfinder.StopAutoWalk();
            return true;
        }

        // Open SERVER gumps with their reply buttons + visible text.
        public static string GetGumpsJson()
        {
            if (UIManager.Gumps == null)
            {
                return "[]";
            }
            var sb = new StringBuilder(1024);
            sb.Append('[');
            bool first = true;
            foreach (var gi in UIManager.Gumps)
            {
                if (!(gi is Gump g) || g.IsDisposed || g.ServerSerial == 0) { continue; }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append('{');
                sb.Append("\"server\":").Append(((uint)g.ServerSerial).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"local\":").Append(((uint)g.LocalSerial).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"id\":").Append(((int)g.GumpType).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"x\":").Append(g.X).Append(',');
                sb.Append("\"y\":").Append(g.Y).Append(',');
                sb.Append("\"buttons\":[");
                bool fb = true;
                AppendGumpButtons(sb, g.Children, ref fb, 0);
                sb.Append("],\"text\":[");
                bool ft = true;
                AppendGumpText(sb, g.Children, ref ft, 0);
                sb.Append("]}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        // TUO note: Control.Children is List<IGui> (CUO uses List<Control>); IGui
        // itself exposes Children + the concrete controls pattern-match fine.
        private static void AppendGumpButtons(StringBuilder sb, List<IGui> children, ref bool first, int depth)
        {
            if (children == null || depth > 4) { return; }
            foreach (IGui c in children)
            {
                if (c is Button b && b.ButtonAction == ButtonAction.Activate)
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append("{\"id\":").Append(b.ButtonID).Append('}');
                }
                if (c.Children != null && c.Children.Count > 0)
                {
                    AppendGumpButtons(sb, c.Children, ref first, depth + 1);
                }
            }
        }

        private static void AppendGumpText(StringBuilder sb, List<IGui> children, ref bool first, int depth)
        {
            if (children == null || depth > 4) { return; }
            foreach (IGui c in children)
            {
                string t = null;
                if (c is Label lbl) { t = lbl.Text; }
                else if (c is HtmlControl html) { t = html.Text; }
                if (!string.IsNullOrEmpty(t))
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append(JsStr(CleanItemName(t)));
                }
                if (c.Children != null && c.Children.Count > 0)
                {
                    AppendGumpText(sb, c.Children, ref first, depth + 1);
                }
            }
        }

        // Reply to a server gump's button — the SAME path the native gump takes.
        public static bool GumpReply(double gumpServerSerial, int button)
        {
            if (UIManager.Gumps == null || gumpServerSerial <= 0)
            {
                return false;
            }
            uint target = (uint)gumpServerSerial;
            foreach (var gi in UIManager.Gumps)
            {
                if (gi is Gump g && !g.IsDisposed && g.ServerSerial == target)
                {
                    g.OnButtonClick(button);
                    return true;
                }
            }
            return false;
        }

        // ── Mouse — the "natural human" alternative to serial-based verbs ─────────
        // Reliable pattern: mouseMove(x, y); pause(150); mouseClick();
        // Coordinates are game-canvas pixels (force-device-scale-factor=1 ⇒ 1:1).
        public static void MouseMove(int x, int y)
        {
            if (World.Instance?.Player == null) { return; }
            Mouse.Position.X = x;
            Mouse.Position.Y = y;
            try
            {
                var handle = Client.Game?.Window?.Handle ?? IntPtr.Zero;
                if (handle != IntPtr.Zero) { SDL.SDL_WarpMouseInWindow(handle, x, y); }
            }
            catch { }
        }

        public static void MouseClick(bool rightButton)
        {
            PushMouseButton(rightButton ? (byte)MouseButtonType.Right : (byte)MouseButtonType.Left, 1);
        }

        public static void MouseDoubleClick(bool rightButton)
        {
            byte btn = rightButton ? (byte)MouseButtonType.Right : (byte)MouseButtonType.Left;
            PushMouseButton(btn, 1);
            PushMouseButton(btn, 2);
        }

        private static void PushMouseButton(byte button, byte clicks)
        {
            if (World.Instance?.Player == null) { return; }
            float fx = Mouse.Position.X, fy = Mouse.Position.Y;
            var down = new SDL.SDL_Event { button = new SDL.SDL_MouseButtonEvent { type = SDL.SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN, button = button, down = true, clicks = clicks, x = fx, y = fy } };
            SDL.SDL_PushEvent(ref down);
            var up = new SDL.SDL_Event { button = new SDL.SDL_MouseButtonEvent { type = SDL.SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP, button = button, down = false, clicks = clicks, x = fx, y = fy } };
            SDL.SDL_PushEvent(ref up);
        }

        // Whatever the last rendered frame hit-tested under the cursor.
        public static string ObjectAtCursorJson()
        {
            var obj = SelectedObject.Object;
            if (obj is Item it)
            {
                return "{\"kind\":\"item\",\"serial\":" + it.Serial.ToString(CultureInfo.InvariantCulture) +
                       ",\"graphic\":" + ((int)it.Graphic).ToString(CultureInfo.InvariantCulture) +
                       ",\"name\":" + JsStr(CleanItemName(it.Name)) + "}";
            }
            if (obj is Mobile mob)
            {
                return "{\"kind\":\"mobile\",\"serial\":" + mob.Serial.ToString(CultureInfo.InvariantCulture) +
                       ",\"graphic\":" + ((int)mob.Graphic).ToString(CultureInfo.InvariantCulture) +
                       ",\"name\":" + JsStr(CleanItemName(mob.Name)) + "}";
            }
            return "{\"kind\":\"none\"}";
        }

        private static uint BackpackSerial()
        {
            var bp = World.Instance?.Player?.FindItemByLayer(Layer.Backpack);
            return bp?.Serial ?? 0u;
        }

        private static void AppendItemJson(StringBuilder sb, Item it)
        {
            sb.Append('{');
            sb.Append("\"serial\":").Append(it.Serial.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"graphic\":").Append(((int)it.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"hue\":").Append(((int)it.Hue).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"amount\":").Append(((int)it.Amount).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"layer\":").Append(((int)it.Layer).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"name\":").Append(JsStr(CleanItemName(it.Name)));
            sb.Append('}');
        }

        private static string JsStr(string s)
        {
            if (s == null)
            {
                return "\"\"";
            }

            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // LegionScript API completion (2026-07-24). Faithful, WASM-safe mirrors of
        // TazUO's LegionScripting/LegionAPI.cs so the web rail's Python (Pyodide)
        // LS runtime reaches the same surface. Numbers arrive from JS as double;
        // objects/lists come back as JSON strings (parsed into Entity/list on the
        // worker). These run on the game (deputy) thread already (invoked via the
        // WasmRailBridge [JSExport]) — no OnMain marshaling, matching Attack/Walk.
        // Every method null-guards the world so a pre-login call is a safe no-op.
        // ═══════════════════════════════════════════════════════════════════════

        // ── combat / character state ─────────────────────────────────────────────
        public static void SetWarMode(bool enabled)
        {
            var w = World.Instance;
            if (w?.Player == null) { return; }
            GameActions.RequestWarMode(w.Player, enabled);
        }

        public static bool BandageSelf()
        {
            var w = World.Instance;
            if (w?.Player == null) { return false; }
            return GameActions.BandageSelf(w);
        }

        public static void ToggleAbility(string ability)
        {
            var w = World.Instance;
            if (w?.Player == null || string.IsNullOrEmpty(ability)) { return; }
            switch (ability.ToLowerInvariant())
            {
                case "primary": GameActions.UsePrimaryAbility(w); break;
                case "secondary": GameActions.UseSecondaryAbility(w); break;
                case "stun": AsyncNetClient.Socket.Send_StunRequest(); break;
                case "disarm": AsyncNetClient.Socket.Send_DisarmRequest(); break;
            }
        }

        public static bool PrimaryAbilityActive()
        {
            var w = World.Instance;
            return w?.Player != null && ((byte)w.Player.PrimaryAbility & 0x80) != 0;
        }

        public static bool SecondaryAbilityActive()
        {
            var w = World.Instance;
            return w?.Player != null && ((byte)w.Player.SecondaryAbility & 0x80) != 0;
        }

        public static string CurrentAbilityNamesJson()
        {
            var w = World.Instance;
            if (w?.Player == null) { return "[]"; }
            var sb = new StringBuilder(64);
            sb.Append('[');
            sb.Append(JsStr(w.Player.PrimaryAbility.GetName())).Append(',');
            sb.Append(JsStr(w.Player.SecondaryAbility.GetName()));
            sb.Append(']');
            return sb.ToString();
        }

        public static string KnownAbilityNamesJson()
        {
            var names = Enum.GetNames<Ability>();
            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append(JsStr(names[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static bool IsGlobalCooldownActive() => GlobalActionCooldown.IsOnCooldown;

        public static string ActiveBuffsJson()
        {
            var w = World.Instance;
            if (w?.Player == null) { return "[]"; }
            var sb = new StringBuilder(256);
            sb.Append('[');
            bool first = true;
            foreach (BuffIcon buff in w.Player.BuffIcons.Values)
            {
                if (buff == null) { continue; }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append('{');
                sb.Append("\"type\":").Append(((int)buff.Type).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"graphic\":").Append(((int)buff.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"timer\":").Append(buff.Timer.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"title\":").Append(JsStr(buff.Title)).Append(',');
                sb.Append("\"text\":").Append(JsStr(buff.Text));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static bool BuffExists(string buffName)
        {
            var w = World.Instance;
            if (string.IsNullOrEmpty(buffName) || w?.Player == null) { return false; }
            foreach (BuffIcon buff in w.Player.BuffIcons.Values)
            {
                if (buff == null) { continue; }
                if ((buff.Title != null && buff.Title.IndexOf(buffName, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (buff.Text != null && buff.Text.IndexOf(buffName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }
            return false;
        }

        public static void SetSkillLock(string skill, string mode)
        {
            var w = World.Instance;
            if (w?.Player == null || string.IsNullOrEmpty(skill)) { return; }
            skill = skill.ToLowerInvariant();
            ClassicUO.Game.Data.Lock status = ClassicUO.Game.Data.Lock.Up;
            switch (mode)
            {
                case "down": status = ClassicUO.Game.Data.Lock.Down; break;
                case "locked": status = ClassicUO.Game.Data.Lock.Locked; break;
            }
            for (int i = 0; i < w.Player.Skills.Length; i++)
            {
                if (w.Player.Skills[i].Name.ToLowerInvariant().Contains(skill))
                {
                    Skill sk = w.Player.Skills[i];
                    sk.Lock = status;
                    GameActions.ChangeSkillLockStatus((ushort)sk.Index, (byte)sk.Lock);
                    break;
                }
            }
        }

        public static void SetStatLock(string stat, string mode)
        {
            if (string.IsNullOrEmpty(stat)) { return; }
            stat = stat.ToLowerInvariant();
            ClassicUO.Game.Data.Lock status = ClassicUO.Game.Data.Lock.Up;
            switch (mode)
            {
                case "down": status = ClassicUO.Game.Data.Lock.Down; break;
                case "locked": status = ClassicUO.Game.Data.Lock.Locked; break;
            }
            byte statB = 0;
            switch (stat)
            {
                case "dex": statB = 1; break;
                case "int": statB = 2; break;
            }
            GameActions.ChangeStatLock(statB, status);
        }

        public static void Virtue(string virtue)
        {
            if (string.IsNullOrEmpty(virtue)) { return; }
            switch (virtue.ToLowerInvariant())
            {
                case "honor": AsyncNetClient.Socket.Send_InvokeVirtueRequest(0x01); break;
                case "sacrifice": AsyncNetClient.Socket.Send_InvokeVirtueRequest(0x02); break;
                case "valor": AsyncNetClient.Socket.Send_InvokeVirtueRequest(0x03); break;
            }
        }

        public static void DisplayRange(int distance, int hue)
        {
            var prof = ProfileManager.CurrentProfile;
            if (prof == null) { return; }
            if (distance <= 0)
            {
                prof.DisplayRadius = false;
                return;
            }
            prof.DisplayRadius = true;
            prof.DisplayRadiusDistance = (ushort)distance;
            prof.DisplayRadiusHue = (ushort)hue;
        }

        public static void TrackingArrow(int x, int y, double identifier)
        {
            var w = World.Instance;
            if (w == null) { return; }
            uint id = identifier < 0 ? uint.MaxValue : (uint)identifier;
            UIManager.GetGump<QuestArrowGump>(id)?.Dispose();
            if (x > 0 && y > 0)
            {
                var arrow = new QuestArrowGump(w, id, x, y) { CanCloseWithRightClick = true };
                UIManager.Add(arrow);
            }
        }

        public static string ClearLeftHand() => ClearHandLayer(Layer.OneHanded);
        public static string ClearRightHand() => ClearHandLayer(Layer.TwoHanded);

        private static string ClearHandLayer(Layer layer)
        {
            var w = World.Instance;
            var p = w?.Player;
            Item i = p?.FindItemByLayer(layer);
            if (i == null) { return "null"; }
            Item bp = p.Backpack;
            if (bp != null)
            {
                MoveItem((double)i.Serial, (double)bp.Serial, 0);
            }
            var sb = new StringBuilder(96);
            AppendItemJson(sb, i);
            return sb.ToString();
        }

        public static void Logout()
        {
            var w = World.Instance;
            if (w == null) { return; }
            GameActions.Logout(w);
        }

        public static void Rename(double serial, string name)
        {
            if (serial <= 0 || string.IsNullOrEmpty(name)) { return; }
            GameActions.Rename((uint)serial, name);
        }

        // ── mount / fly ──────────────────────────────────────────────────────────
        public static void Mount(double serial)
        {
            var w = World.Instance;
            if (w?.Player == null) { return; }
            uint s = serial <= 0 ? (ProfileManager.CurrentProfile?.SavedMountSerial ?? 0u) : (uint)serial;
            if (s == 0) { return; }
            GameActions.DoubleClick(w, s, true);
        }

        public static void Dismount()
        {
            var w = World.Instance;
            if (w?.Player == null) { return; }
            if (w.Player.FindItemByLayer(Layer.Mount) != null)
            {
                GameActions.DoubleClick(w, w.Player, true);
            }
        }

        public static void SetMount(double serial)
        {
            var prof = ProfileManager.CurrentProfile;
            if (prof != null) { prof.SavedMountSerial = (uint)Math.Max(0, serial); }
        }

        public static void ToggleFly()
        {
            var w = World.Instance;
            if (w?.Player != null && w.Player.Race == RaceType.GARGOYLE)
            {
                AsyncNetClient.Socket.Send_ToggleGargoyleFlying();
            }
        }

        // ── cursor / item movement ───────────────────────────────────────────────
        public static double GetHeldItem()
        {
            var hold = Client.Game?.UO?.GameCursor?.ItemHold;
            return (hold != null && hold.Enabled) ? hold.Serial : 0;
        }

        public static void PickUpToCursor(double serial, int amt)
        {
            var w = World.Instance;
            if (w == null) { return; }
            uint s = (uint)Math.Max(0, serial);
            if (s == 0)
            {
                var hold = Client.Game?.UO?.GameCursor?.ItemHold;
                if (hold != null && hold.Enabled) { s = hold.Serial; }
                else { return; }
            }
            GameActions.PickUp(w, s, 0, 0, amt, skipQueue: true);
        }

        public static void DropFromCursor(double serial, int x, int y, int z, double container)
        {
            var w = World.Instance;
            if (w?.Map == null) { return; }
            uint s = (uint)Math.Max(0, serial);
            if (s == 0)
            {
                var hold = Client.Game?.UO?.GameCursor?.ItemHold;
                if (hold != null && hold.Enabled) { s = hold.Serial; }
                else { return; }
            }
            uint cont = container < 0 ? uint.MaxValue : (uint)container;
            if (cont == uint.MaxValue && z == sbyte.MaxValue && x != ushort.MaxValue && y != ushort.MaxValue)
            {
                w.Map.GetMapZ(x, y, out sbyte landZ, out sbyte staticZ);
                z = Math.Max(landZ, staticZ);
            }
            GameActions.DropItem(s, x, y, z, cont, force: true);
        }

        public static void MoveItemOffset(double serial, int amt, int x, int y, int z, bool osi)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Map == null || serial <= 0) { return; }
            w.Map.GetMapZ(w.Player.X + x, w.Player.Y + y, out sbyte gz, out sbyte gz2);
            bool useCalculatedZ = false;
            if (gz > z) { z = gz; useCalculatedZ = true; }
            if (gz2 > z) { z = gz2; useCalculatedZ = true; }
            if (!useCalculatedZ) { z = w.Player.Z + z; }
            GameActions.PickUp(w, (uint)serial, 0, 0, amt);
            GameActions.DropItem((uint)serial, w.Player.X + x, w.Player.Y + y, z, osi ? uint.MaxValue : 0);
        }

        public static void ClearMoveQueue()
        {
            ObjectActionQueue.Instance?.ClearByPriority(ActionPriority.MoveItem);
        }

        public static bool IsProcessingMoveQueue()
        {
            return ObjectActionQueue.Instance != null && !ObjectActionQueue.Instance.IsEmpty;
        }

        public static bool IsProcessingUseItemQueue()
        {
            return ObjectActionQueue.Instance != null && !ObjectActionQueue.Instance.IsEmpty;
        }

        public static void RequestOPLData(string serialsCsv)
        {
            var w = World.Instance;
            if (w == null || string.IsNullOrEmpty(serialsCsv)) { return; }
            foreach (var tok in serialsCsv.Split(','))
            {
                if (uint.TryParse(tok.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint s) && s != 0)
                {
                    w.OPL.Contains(s);
                }
            }
        }

        public static string ItemNameAndProps(double serial)
        {
            var w = World.Instance;
            if (w == null || serial <= 0) { return ""; }
            uint s = (uint)serial;
            if (!w.OPL.Contains(s)) { w.OPL.Contains(s); }
            if (w.OPL.TryGetNameAndData(s, out string name, out string data))
            {
                if (string.IsNullOrEmpty(data)) { return name ?? ""; }
                return (name ?? "") + "\n" + data;
            }
            return "";
        }

        // ── gumps / context menus ────────────────────────────────────────────────
        public static bool CloseGump(double id)
        {
            var w = World.Instance;
            if (w?.Player == null || id == 0) { return false; }
            uint gumpId = id < 0 ? w.Player.LastGumpID : (uint)id;
            Gump gump = UIManager.GetGumpServer(gumpId);
            if (gump == null) { return false; }
            if (gump.CanCloseWithRightClick)
            {
                gump.InvokeMouseCloseGumpWithRClick();
                return true;
            }
            gump.Dispose();
            return true;
        }

        public static bool GumpContains(string text, double id)
        {
            var w = World.Instance;
            if (w?.Player == null || string.IsNullOrEmpty(text)) { return false; }
            uint gumpId = id < 0 ? w.Player.LastGumpID : (uint)id;
            Gump g = UIManager.GetGumpServer(gumpId);
            if (g == null) { return false; }
            foreach (var ctrl in g.Children)
            {
                if (ctrl is ClassicUO.Game.UI.Controls.Label lbl &&
                    lbl.Text != null && lbl.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                if (ctrl is ClassicUO.Game.UI.Controls.HtmlControl html &&
                    html.Text != null && html.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static void ContextMenu(double serial, int entry)
        {
            if (serial <= 0) { return; }
            uint s = (uint)serial;
            PopupMenuGump.CloseNext = s;
            AsyncNetClient.Socket.Send_RequestPopupMenu(s);
            AsyncNetClient.Socket.Send_PopupMenuSelection(s, (ushort)entry);
        }

        public static void CloseContextMenus()
        {
            UIManager.ContextMenu?.Dispose();
            MenuGump mg = UIManager.GetGump<MenuGump>();
            while (mg != null)
            {
                mg.Dispose();
                mg = UIManager.GetGump<MenuGump>();
            }
        }

        // ── world reads (map / tiles / statics / party / path) ───────────────────
        public static int GetMap()
        {
            var w = World.Instance;
            return w?.MapIndex ?? -1;
        }

        public static string GetTileJson(int x, int y)
        {
            var w = World.Instance;
            if (w?.Map == null) { return "null"; }
            GameObject t = w.Map.GetTile(x, y);
            if (t == null) { return "null"; }
            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"graphic\":").Append(((int)t.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"x\":").Append(x.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"y\":").Append(y.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"z\":").Append(((int)t.Z).ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendStaticsAt(StringBuilder sb, int x, int y, ref bool first)
        {
            var w = World.Instance;
            if (w?.Map == null) { return; }
            ClassicUO.Game.Map.Chunk chunk = w.Map.GetChunk(x, y, false);
            if (chunk == null) { return; }
            GameObject obj = chunk.GetHeadObject(x % 8, y % 8);
            while (obj != null)
            {
                if (obj is Static st)
                {
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append('{');
                    sb.Append("\"graphic\":").Append(((int)st.Graphic).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"x\":").Append(st.X.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"y\":").Append(st.Y.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"z\":").Append(((int)st.Z).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append("\"hue\":").Append(((int)st.Hue).ToString(CultureInfo.InvariantCulture));
                    sb.Append('}');
                }
                obj = obj.TNext;
            }
        }

        public static string GetStaticsAtJson(int x, int y)
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            bool first = true;
            AppendStaticsAt(sb, x, y, ref first);
            sb.Append(']');
            return sb.ToString();
        }

        public static string GetStaticsInAreaJson(int x1, int y1, int x2, int y2)
        {
            int minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
            int minY = Math.Min(y1, y2), maxY = Math.Max(y1, y2);
            // Bound the sweep so a runaway range can't stall the game thread.
            if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 4096) { return "[]"; }
            var sb = new StringBuilder(256);
            sb.Append('[');
            bool first = true;
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    AppendStaticsAt(sb, x, y, ref first);
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static double GetPartyLeader()
        {
            var w = World.Instance;
            return w?.Party?.Leader ?? 0;
        }

        public static string GetPartyMemberSerialsJson()
        {
            var w = World.Instance;
            var sb = new StringBuilder(128);
            sb.Append('[');
            bool first = true;
            uint mySerial = w?.Player?.Serial ?? 0;
            var members = w?.Party?.Members;
            if (members != null)
            {
                foreach (var member in members)
                {
                    if (member == null || member.Serial == 0 || member.Serial == mySerial) { continue; }
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append(member.Serial.ToString(CultureInfo.InvariantCulture));
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string GetPathJson(int x, int y, int z, int distance)
        {
            var w = World.Instance;
            if (w?.Map == null || w.Player == null) { return "null"; }
            if (z == int.MinValue) { z = w.Map.GetTileZ(x, y); }
            var path = w.Player.Pathfinder.GetPathTo(x, y, z, distance);
            if (path == null) { return "null"; }
            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) { sb.Append(','); }
                sb.Append("{\"x\":").Append(path[i].X.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"y\":").Append(path[i].Y.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"z\":").Append(path[i].Z.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static bool Pathfinding()
        {
            var w = World.Instance;
            if (w?.Player == null) { return false; }
            return w.Player.Pathfinder.AutoWalking || WorldMapPathfinder.IsRunning;
        }

        public static void CancelPathfinding()
        {
            var w = World.Instance;
            w?.Player?.Pathfinder?.StopAutoWalk();
            WorldMapPathfinder.Cancel();
        }

        // ── ground tile markers (TileMarkerManager — draws on the map floor) ──────
        public static void MarkTile(int x, int y, int hue, int map)
        {
            var w = World.Instance;
            if (w?.Map == null) { return; }
            if (map < 0) { map = w.Map.Index; }
            TileMarkerManager.Instance.AddTile(x, y, map, (ushort)hue);
        }

        public static void RemoveMarkedTile(int x, int y, int map)
        {
            var w = World.Instance;
            if (w?.Map == null) { return; }
            if (map < 0) { map = w.Map.Index; }
            TileMarkerManager.Instance.RemoveTile(x, y, map);
        }

        // ── dress agent ──────────────────────────────────────────────────────────
        public static void Dress(string name)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            var cfg = DressAgentManager.Instance?.CurrentPlayerConfigs?
                .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (cfg != null) { DressAgentManager.Instance.DressFromConfig(cfg); }
        }

        public static void Undress(string name)
        {
            if (string.IsNullOrEmpty(name)) { return; }
            var cfg = DressAgentManager.Instance?.CurrentPlayerConfigs?
                .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (cfg != null) { DressAgentManager.Instance.UndressFromConfig(cfg); }
        }

        public static void UndressAll(bool kr)
        {
            DressAgentManager.Instance?.UndressAll(kr);
        }

        public static string GetAvailableDressOutfitsJson()
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            var cfgs = DressAgentManager.Instance?.CurrentPlayerConfigs;
            if (cfgs != null)
            {
                bool first = true;
                foreach (var c in cfgs)
                {
                    if (c == null) { continue; }
                    if (!first) { sb.Append(','); }
                    first = false;
                    sb.Append(JsStr(c.Name));
                }
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ── auto systems (loot / follow) ─────────────────────────────────────────
        public static void ToggleAutoLoot()
        {
            var prof = ProfileManager.CurrentProfile;
            if (prof != null) { prof.EnableAutoLoot ^= true; }
        }

        public static void AutoLootContainer(double container)
        {
            if (container <= 0) { return; }
            AutoLootManager.Instance?.ForceLootContainer((uint)container);
        }

        public static void AutoFollow(double mobile)
        {
            var w = World.Instance;
            if (w == null || mobile <= 0) { return; }
            if (w.Mobiles.Get((uint)mobile) is Mobile m) { m.Follow(false); }
        }

        public static void CancelAutoFollow()
        {
            var prof = ProfileManager.CurrentProfile;
            if (prof != null) { prof.FollowingMode = false; }
        }

        // ── relative targeting ───────────────────────────────────────────────────
        public static void TargetLandRel(int xOffset, int yOffset)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Map == null || !w.TargetManager.IsTargeting) { return; }
            ushort x = (ushort)(w.Player.X + xOffset);
            ushort y = (ushort)(w.Player.Y + yOffset);
            w.Map.GetMapZ(x, y, out sbyte gZ, out sbyte sZ);
            w.TargetManager.Target(0, x, y, gZ);
        }

        public static void TargetTileRel(int xOffset, int yOffset, int graphic)
        {
            var w = World.Instance;
            if (w?.Player == null || w.Map == null || !w.TargetManager.IsTargeting) { return; }
            ushort x = (ushort)(w.Player.X + xOffset);
            ushort y = (ushort)(w.Player.Y + yOffset);
            short z = w.Player.Z;
            ushort g = graphic < 0 ? ushort.MaxValue : (ushort)graphic;
            GameObject go = w.Map.GetTile(x, y);
            if (g == ushort.MaxValue && go != null)
            {
                g = go.Graphic;
                z = go.Z;
            }
            w.TargetManager.Target(g, x, y, z);
        }

        public static void TargetResource(double itemSerial, int resource)
        {
            if (itemSerial <= 0) { return; }
            AsyncNetClient.Socket.Send_TargetByResource((uint)itemSerial, (ushort)resource);
        }

        // ── finders ──────────────────────────────────────────────────────────────
        // FindLayer(layerName, serial) — the item worn on a named layer, as item
        // JSON (or "null"). Faithful to LegionAPI but maps the layer NAME via the
        // client Layer enum (Enum.TryParse) instead of the LegionScripting Utility.
        public static string FindLayerJson(string layer, double serial)
        {
            var w = World.Instance;
            if (w?.Player == null || string.IsNullOrEmpty(layer)) { return "null"; }
            if (!Enum.TryParse<Layer>(layer, true, out Layer matched)) { return "null"; }
            Mobile m = serial <= 0 ? w.Player : w.Mobiles.Get((uint)serial);
            Item item = m?.FindItemByLayer(matched);
            if (item == null) { return "null"; }
            var sb = new StringBuilder(96);
            AppendItemJson(sb, item);
            return sb.ToString();
        }

    }
}
