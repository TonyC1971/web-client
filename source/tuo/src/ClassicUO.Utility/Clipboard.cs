#nullable enable
using SDL3;

namespace ClassicUO.Utility;

public static class Clipboard
{
    public static string? GetClipboardText()
    {
#if BROWSER_WASM
        // WASM: the SDL clipboard P/Invoke is unresolved in the web build (a
        // call TRAPS the runtime — player-reported Ctrl+X crash 2026-07-18)
        // and would be disconnected from the browser clipboard anyway. Route
        // through the JS bridge (see WasmClipboard.cs).
        string s = WasmClipboard.GetText();
        return string.IsNullOrEmpty(s) ? null : s;
#else
        if (SDL.SDL_HasClipboardText() != false)
        {
            return SDL.SDL_GetClipboardText() ?? null;
        }

        return null;
#endif
    }

    public static void SetClipboardText(string text)
    {
#if BROWSER_WASM
        WasmClipboard.SetText(text);
#else
        SDL.SDL_SetClipboardText(text);
#endif
    }
}

public static partial class Extensions
{
    public static void CopyToClipboard(this string text) => Clipboard.SetClipboardText(text);
}
