// SPDX-License-Identifier: BSD-2-Clause
//
// Browser clipboard bridge for the WASM build.
//
// CUO's text boxes (StbTextBox) drive Ctrl+C / Ctrl+X / Ctrl+V and the
// "Paste" macro through SDL2's clipboard API. On wasm SDL's clipboard
// is an in-process buffer with no connection to the browser / OS
// clipboard, so a player could neither copy a journal line out to
// another app nor paste a sentence in from one. These shims connect
// the two:
//
//   C#:        WasmClipboard.SetText(sel) / GetText()
//          -> [DllImport("SDL3", "wasm_clipboard_set"/"wasm_clipboard_get")]
//   C:         wasm_clipboard_* (source/webclient/native-shims/SDL3.c)
//          -> MAIN_THREAD_EM_ASM proxies deputy-worker -> main thread
//   JS main:   globalThis.__wasm_clipboard_set / __wasm_clipboard_get
//          -> navigator.clipboard.writeText + a mirror fed by the DOM
//             `paste` event (the only sync, permission-free clipboard
//             read). See main.js wireWasmClipboard().
//
// Both P/Invokes use only blittable `byte[]` + `int` parameters — the
// exact signature shape of the long-proven sdl3_drain_events shim — so
// no string marshalling and no `unsafe` is involved. UTF-8 encoding is
// done in managed code; the C side never sees a C# string.
//
// Public so consuming projects (ClassicUO.Client) can call without
// InternalsVisibleTo gymnastics — same convention as WasmSignal.cs.
// On desktop (no BROWSER_WASM) every method is a no-op; callers keep
// their direct SDL clipboard path behind their own #if.

#if BROWSER_WASM
using System.Runtime.InteropServices;
using System.Text;
#endif

public static class WasmClipboard
{
#if BROWSER_WASM
    [DllImport("SDL3", EntryPoint = "wasm_clipboard_set", CallingConvention = CallingConvention.Cdecl)]
    private static extern void wasm_clipboard_set(byte[] utf8, int len);

    [DllImport("SDL3", EntryPoint = "wasm_clipboard_get", CallingConvention = CallingConvention.Cdecl)]
    private static extern int wasm_clipboard_get([Out] byte[] buf, int max);

    // Upper bound on a single clipboard transfer. Chat lines and even
    // a multi-line journal copy sit far below this; oversized text is
    // truncated rather than risking an unbounded allocation.
    private const int CAP = 16 * 1024;

    public static void SetText(string text)
    {
        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
            wasm_clipboard_set(utf8, utf8.Length);
        }
        catch { /* clipboard is best-effort */ }
    }

    public static string GetText()
    {
        try
        {
            byte[] buf = new byte[CAP];
            int n = wasm_clipboard_get(buf, CAP);
            if (n <= 0)
                return string.Empty;
            if (n > CAP)
                n = CAP;
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        catch
        {
            return string.Empty;
        }
    }
#else
    public static void SetText(string text) { /* desktop: callers use SDL */ }

    public static string GetText() => string.Empty;
#endif
}
