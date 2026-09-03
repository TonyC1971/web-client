SDL2 vendored headers
=====================

Source: SDL2 release-2.30.10 — https://github.com/libsdl-org/SDL/releases/tag/release-2.30.10
License: zlib (see LICENSE.txt) — same as upstream SDL2.

Why vendored
------------
The `Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64` workload pack ships
SDL 1.3 headers under `em_cache/sysroot/include/SDL/` but **not** SDL2 under
`include/SDL2/`. FNA3D's source code does `#include <SDL.h>` and the build
flags add `-iwithsysroot/include/SDL2`, so the build relies on that directory
existing in `em_cache`.

Two off-the-shelf paths failed:

1. **`-sUSE_SDL=2` ports**: Mercury's targets opt out of FROZEN_CACHE so emcc
   *can* fetch the SDL2 port from GitHub on first compile. But the order of
   operations in `dotnet publish` triggers `fakesdl/SDL.h` (a stub that yells
   "use -sUSE_SDL!") to be staged before SDL2 is fetched, breaking the
   compile of `native-shims/SDL2.c` and never reaching the port-download
   step. Re-running unsticks it once a port has been downloaded — but a
   fresh `rm -rf em_cache` puts you back to square one.

2. **System `libsdl2-dev` headers**: On Debian/Ubuntu, `/usr/include/SDL2/`
   uses a multiarch wrapper — `SDL_config.h` does
   `#include <SDL2/_real_SDL_config.h>` and the real config lives under
   `/usr/include/x86_64-linux-gnu/SDL2/`. That trick is desktop-x86_64 only;
   under a wasm cross-compile sysroot it fails outright with "file not
   found". And even if it worked, you'd be compiling against an x86_64
   config (SSE/AVX defines) for a wasm target — nonsense.

Vendoring upstream's pristine headers + having `wasm-fna-native-mercury.targets`
add `-I` to this directory bypasses both issues:
- `-I` wins over `-iwithsysroot/include/fakesdl` (priority order in clang)
- No network required at build time
- No dependency on em_cache state (`rm -rf em_cache` doesn't break the build)
- No multiarch wrapper, just the upstream `SDL_config.h` redirector that
  picks `SDL_config_emscripten.h` when `__EMSCRIPTEN__` is defined.

Updating
--------
To bump to a newer SDL2 release:

```bash
cd /tmp
wget https://github.com/libsdl-org/SDL/releases/download/release-X.Y.Z/SDL2-X.Y.Z.tar.gz
tar xzf SDL2-X.Y.Z.tar.gz
rm -rf source/vendor/sdl2-headers/include/SDL2
mkdir -p source/vendor/sdl2-headers/include/SDL2
cp /tmp/SDL2-X.Y.Z/include/*.h source/vendor/sdl2-headers/include/SDL2/
cp /tmp/SDL2-X.Y.Z/LICENSE.txt source/vendor/sdl2-headers/
```

Stay on the SDL2 line (2.x). The `mercury-statics/SDL2.a` is a sdl2-compat
build that bundles SDL3 internally; bumping headers to SDL3 here would break
the API the FNA3D source compiles against.
