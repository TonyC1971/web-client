// Registers the identifier "__Native" as a valid DllImport module for
// the .NET WASM PInvoke-table generator. FNA's SDL2_FNAPlatform uses
// `[DllImport("__Native")]` for two Emscripten runtime calls:
//
//   emscripten_set_main_loop(func, fps, simulate_infinite_loop)
//   emscripten_cancel_main_loop()
//
// Both live in Emscripten's libc.a which is linked in by the SDK's
// default link step. The file basename `__Native` matches what the
// `_WasmPInvokeModules Include="%(FileName)"` MSBuild item expects
// (see WasmApp.Common.targets:748). Keep-alive references below ensure
// the symbols aren't DCE'd before the pinvoke table can resolve them.

#include <emscripten.h>

EMSCRIPTEN_KEEPALIVE
void p3_native_keepalive(void)
{
    // Force-reference the Emscripten runtime symbols FNA calls so
    // wasm-ld keeps them exported.
    emscripten_set_main_loop(0, 0, 0);
    emscripten_cancel_main_loop();
}
