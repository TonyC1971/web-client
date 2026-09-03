// Stub companion that registers the identifier "SDL2" with the .NET
// WASM PInvoke-table generator. Its basename (SDL2) becomes a valid
// DllImport module name (see
// `WasmApp.Common.targets:748 _WasmPInvokeModules Include="%(FileName)"`).
//
// FNA's `SDL2-CS.cs` declares 659 P/Invokes as `[DllImport("SDL2", ...)]`;
// this file is what makes the table generator accept that library name.
// The actual SDL2 implementation is linked in from Emscripten's bundled
// port via `-sUSE_SDL=2` (see csproj). Each C# P/Invoke call here
// resolves to the real SDL symbol at link time.
//
// No forwarders needed: emcc's linker finds `SDL_Init`, `SDL_CreateWindow`
// etc. inside libSDL2.a and drops them into the final wasm module. The
// PInvoke table maps the `(SDL2, SDL_Init)` pair straight to that address.

#include <SDL.h>
#include <emscripten.h>

// Having a single keepalive reference ensures libSDL2.a's symbols are
// NOT dead-code-stripped before the pinvoke table can resolve them.
EMSCRIPTEN_KEEPALIVE
const char* p3_sdl2_keepalive(void)
{
    return SDL_GetError();
}
