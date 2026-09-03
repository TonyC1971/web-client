// ZoneLoadDiag — tiny shared stopwatch anchored at 0x1B EnterWorld entry,
// readable from any assembly. Used to trace which sub-step of the zone-load
// path eats the ~1.6-2s stall between "Entering Britannia" and first
// world-visible frame.
//
// Used to pinpoint which phase owns the ~330ms delta seen between accounts
// when transitioning into the game. Scatter `ZoneLoadDiag.Ms` reads at:
// EnterWorld entry / after CreatePlayer / after initial Sends /
// GameScene.SetScene / first N TextureAtlas allocations / first world
// DrawWorld. Desktop builds skip it entirely.

#if BROWSER_WASM
namespace ClassicUO.Utility
{
    public static class ZoneLoadDiag
    {
        private static long _anchorTicks;
        private static int _textureAllocCount;

        public static void Anchor()
        {
            _anchorTicks = System.DateTime.UtcNow.Ticks;
            _textureAllocCount = 0;
        }

        public static long Ms => (System.DateTime.UtcNow.Ticks - _anchorTicks) / 10_000;

        // Bounded counter so TextureAtlas doesn't spam 100+ traces during
        // long play sessions; we only care about allocations inside the
        // zone-load window (typically <12).
        public static int NextTextureSlot() => System.Threading.Interlocked.Increment(ref _textureAllocCount);
    }
}
#endif
