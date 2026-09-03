// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.UI.Gumps;

namespace ClassicUO.Game.Managers
{
    // FIRST-LOGIN window layout (operator 2026-07-10: "paperdoll a la derecha de
    // la game zone view, el inventory debajo del paperdoll, el status debajo del
    // gameview, y los skills y el mapa también debajo del gameview separados").
    // The preset gumps open ASYNCHRONOUSLY (paperdoll/backpack/skills are server
    // round-trips), so LoginComplete arms this and GameScene.Update ticks it: as
    // each gump appears it is snapped to its slot once, then the pass disarms.
    // Single bool gate on the hot path; hard 8s deadline so a missing gump
    // (e.g. no backpack on a broken char) can never leave it ticking forever.
    internal static class FirstLoginGumpPreset
    {
        private const int MARGIN = 8;
        private const long DEADLINE_MS = 8000;
        private const long TICK_MS = 250;

        private static bool _armed;
        private static long _deadline;
        private static long _nextTick;

        public static void Arm()
        {
            _armed = true;
            _deadline = Time.Ticks + DEADLINE_MS;
            _nextTick = 0;
        }

        public static void Update(World world)
        {
            if (!_armed)
            {
                return;
            }

            var profile = ProfileManager.CurrentProfile;

            if (Time.Ticks > _deadline || world?.Player == null || profile == null)
            {
                _armed = false;
                return;
            }

            if (Time.Ticks < _nextTick)
            {
                return;
            }

            _nextTick = Time.Ticks + TICK_MS;

            int gx = profile.GameWindowPosition.X;
            int gy = profile.GameWindowPosition.Y;
            int gw = profile.GameWindowSize.X;
            int gh = profile.GameWindowSize.Y;
            int belowY = gy + gh + MARGIN;

            // RE-ASSERT every tick until the deadline instead of one-shot flags
            // (v0.9.374 probe): the status gump is DISPOSED+RECREATED when the
            // mobile-status response lands, and the minimap when the map-change
            // packet rebinds its textures — both landed AFTER the first snap and
            // escaped it, ending back at their stock corners. Re-finding each
            // tick catches every recreated instance; the writes are idempotent
            // and the pass still hard-stops at the 8s deadline.
            var pd = UIManager.GetGump<PaperDollGump>(world.Player.Serial);

            if (pd != null)
            {
                pd.X = gx + gw + MARGIN;
                pd.Y = gy;
                pd.SetInScreen();
            }

            var bp = world.Player.FindItemByLayer(Layer.Backpack);
            var cont = bp != null ? UIManager.GetGump<ContainerGump>(bp.Serial) : null;

            if (cont != null)
            {
                cont.X = gx + gw + MARGIN;
                cont.Y = (pd != null ? pd.Y + pd.Height : gy + 420) + MARGIN;
                cont.SetInScreen();
            }

            var st = StatusGumpBase.GetStatusGump();

            if (st != null)
            {
                st.X = gx + 4;
                st.Y = belowY;
                st.SetInScreen();
            }

            Gump sk = UIManager.GetGump<StandardSkillsGump>();
            sk ??= UIManager.GetGump<SkillGumpAdvanced>();

            if (sk != null)
            {
                sk.X = gx + 320;
                sk.Y = belowY;
                sk.SetInScreen();
            }

            var mm = UIManager.GetGump<MiniMapGump>();

            if (mm != null)
            {
                mm.X = gx + 720;
                mm.Y = belowY;
                mm.SetInScreen();
            }
        }
    }
}
