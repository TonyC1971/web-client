// SPDX-License-Identifier: BSD-2-Clause
//
// Lifecycle-event bridge from the C# deputy worker to main.js
// listeners on the main thread. Replaces the prior pattern of
// emitting magic trace strings via Console.Error.WriteLine and
// having main.js sniff console.error/log for them — the sniff was
// fragile (relied on console never being silenced or buffered) and
// leaked the trace strings into the deployed bundle's DevTools.
//
// Pipeline:
//
//   C#:        WasmSignal.Send("login-gump-added")
//          -> [DllImport("SDL3", "wasm_signal_event")]
//   C:         wasm_signal_event(name)  (source/webclient/native-shims/SDL3.c)
//          -> MAIN_THREAD_EM_ASM proxies deputy-worker -> main thread
//   JS main:   globalThis.__uo_signal(name)
//          -> window.dispatchEvent(new CustomEvent("cuo:" + name))
//   JS subs:   window.addEventListener("cuo:login-gump-added", ...)
//
// Event names are part of the public contract main.js consumes;
// renaming requires updating both ends. Current names:
//   - "login-gump-added"          (LoginGump appeared)
//   - "entering-britannia"        (post character-select)
//   - "gamescene-active"          (post LoginComplete scene transition)
//   - "draw-heartbeat"            (every 60 frames during gameplay)
//
// Public so consuming projects (ClassicUO.Client, ClassicUO.Renderer,
// ClassicUO.IO) can call without InternalsVisibleTo gymnastics.
//
// On desktop (no BROWSER_WASM) this is a no-op so callers don't need
// per-call #if guards.

using System.Runtime.InteropServices;

public static class WasmSignal
{
#if BROWSER_WASM
    [DllImport("SDL3", EntryPoint = "wasm_signal_event", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void wasm_signal_event(string name);

    public static void Send(string name)
    {
        try { wasm_signal_event(name); } catch { /* best-effort */ }
    }
#else
    public static void Send(string name) { /* desktop no-op */ }
#endif
}
