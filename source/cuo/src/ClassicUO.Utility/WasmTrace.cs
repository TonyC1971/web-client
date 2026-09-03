// SPDX-License-Identifier: BSD-2-Clause
//
// Conditional trace helper for the wasm build. Every call site
// compiles to nothing (entire expression, including argument
// evaluation, is erased) when the compilation unit does NOT define
// `WASM_DEV_TRACE`. Production builds (`publish-p4-cuo.bat prod`)
// strip the symbol from the wasm sub-projects via Directory.Build.props
// so internal trace strings (`[cuo-trace]`, `[zonediag]`, `[fbo-test]`,
// account names, world coords, packet IDs, gump names) never make it
// to the browser console.
//
// Lives in the global namespace deliberately — every cuo/* file that
// previously called `Console.Error.WriteLine` can switch to
// `WasmTrace.W(...)` without adding `using ...;` lines. The
// [Conditional] attribute is consumed at the CALLER's compile, so
// each project that references this symbol must inherit the
// `WASM_DEV_TRACE` constant — Directory.Build.props handles that for
// every cuo sub-project under BROWSER_WASM.

using System.Diagnostics;

public static class WasmTrace
{
    [Conditional("WASM_DEV_TRACE")]
    public static void W(string s) => System.Console.Error.WriteLine(s);
}
