// Lifecycle-event bridge from the C# deputy worker to main.js listeners on
// the main thread — TazUO (TUO) edition.
//
// Binds the NON-BLOCKING native entrypoint (wasm_signal_event_async ->
// MAIN_THREAD_ASYNC_EM_ASM). The BLOCKING wasm_signal_event (MAIN_THREAD_EM_ASM)
// deadlocks TUO's deputy at world-entry (iter61). The async variant posts and
// returns immediately, so the deputy never blocks.
//
// HISTORY: this emit was briefly reverted 2026-05-29 after a report that it
// "broke entering Britannia". That was a MISDIAGNOSIS — the hang was ModernUO's
// account-already-online behaviour (logging the SAME account in a 2nd time while
// the 1st session lingers → "Verifying Account"), NOT this code. The operator
// confirmed (2026-05-30) that with a FRESH account the resize works. Re-added.
//
// Pipeline:
//   C#:      WasmSignal.Send("gamescene-active")
//        ->  [DllImport("SDL3", "wasm_signal_event_async")]
//   C:       wasm_signal_event_async(name)  (source/webclient/native-shims/SDL3.c)
//        ->  MAIN_THREAD_ASYNC_EM_ASM (non-blocking) proxies deputy -> main
//   JS main: globalThis.__uo_signal(name) -> window.dispatchEvent("cuo:" + name)
//   JS subs: main.js's "cuo:gamescene-active" listener calls
//            WasmViewport.ResizeGame to grow the canvas from 640x480 to the window.
//
// Only "gamescene-active" is emitted today (the canvas-resize trigger), from
// LoginComplete AFTER the GameScene transition. On desktop (no BROWSER_WASM)
// this is a no-op so callers need no #if guards.

using System.Runtime.InteropServices;

public static class WasmSignal
{
#if BROWSER_WASM
    [DllImport("SDL3", EntryPoint = "wasm_signal_event_async", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void wasm_signal_event_async(string name);

    public static void Send(string name)
    {
        try { wasm_signal_event_async(name); } catch { /* best-effort */ }
    }
#else
    public static void Send(string name) { /* desktop no-op */ }
#endif
}
