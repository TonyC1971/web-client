# TazUO upstream vendoring

This directory is a **vendored snapshot** of the upstream TazUO project.
It is NOT a git submodule of the main `uonexus-webclient` repo —
the files live here directly so the WebClient multi-client build can
patch them independently per its WASM port without affecting the
upstream.

Companion to `source/cuo/` (the equivalent vendored snapshot for
ClassicUO).

## Upstream tracking

- **Source repo**: https://github.com/PlayTazUO/TazUO
- **License**: BSD 2-Clause (copy in `LICENSE.md` at the root of this tree).
  Compatible with our existing distribution model.
- **Vendored from commit**: `a49f6b235529ab53c1c916ac7c3e3437d99c2cb0`
- **Vendor date**: 2026-05-26
- **Release at vendor time**: v5.1.0 (2026-04-06)

## What was stripped on vendor

Desktop-only native binary subtrees removed from `external/` because they
are useless under WebAssembly + Mercury MT:

- `external/osx/` — macOS native binaries
- `external/osx-arm/` — Apple Silicon native binaries
- `external/x64/` — Windows x64 native binaries
- `external/lib64/` — Linux x64 native binaries
- `external/iplib/` — IP geolocation database
- `external/vulkan/` — Vulkan natives
- `external/cuoapi/` — CUO API native shim

What is kept (the managed C# bits that compile to WASM):

- `external/FNA/` — graphics core (FNA-XNA/FNA, same upstream as CUO)
- `external/FileEmbed/` — build-time resource embedding
- `external/MP3Sharp/` — managed MP3 decode
- `external/Myra/` — UI framework (bittiez/Myra fork). **WASM viability
  unknown — to be tested in Phase 2.**
- `external/FontStashSharp/` — font rendering (managed). Likely
  WASM-compatible.
- `external/XNAssets/` — asset content pipeline. WASM viability unknown.

## Re-vendoring (how to refresh against TazUO upstream)

When the operator says "actualiza TUO a vX.Y.Z":

```sh
# 1. Clone fresh into a temp location
cd /tmp
rm -rf tazuo-fresh
git clone https://github.com/PlayTazUO/TazUO.git tazuo-fresh
cd tazuo-fresh
git submodule update --init --recursive
TAZUO_SHA=$(git rev-parse HEAD)

# 2. Mirror over source/tuo/, preserving our WASM patches via a diff first
cd <repo>/source/tuo
git diff -- . > /tmp/tuo-wasm-patches.diff  # capture our patches
cp -r /tmp/tazuo-fresh/* .                  # overwrite with upstream
rm -rf .git .github .claude
rm -rf external/{osx,osx-arm,x64,lib64,iplib,vulkan,cuoapi}
git apply --reject /tmp/tuo-wasm-patches.diff  # re-apply our patches

# 3. Update this file with the new SHA + date

# 4. Build TUO WASM bundle and run smoke per-client
```

## Why vendored instead of git submodule?

Vendoring is a deliberate choice for THIS repo:

1. TazUO has 6 nested submodules (FNA, MP3Sharp, FileEmbed, Myra,
   FontStashSharp, XNAssets), each with their own further nesting in
   the case of FNA (SDL2-CS, SDL3-CS, Theorafile). Adding all of them
   to our top-level `.gitmodules` would be heavy.
2. We need to patch the upstream source for WASM compatibility
   (BROWSER_WASM blocks, JSImport bridges, Mercury MT bootstrap). Patching
   a vendored copy is simpler than maintaining a fork branch on every
   submodule.
3. Same pattern as `source/cuo/` (FNA is vendored there too even though
   MP3Sharp/FileEmbed are submodules — for consistency we vendor
   everything in `source/tuo/`).
4. `source/tuo/.git` is intentionally absent so the main repo controls
   diff visibility uniformly.

## What lives elsewhere

Companion docs that describe the multi-client build:

- `docs/tuo/` — per-client docs for TUO (WASM port, perf, releases).
- `docs/shared/` — infra docs shared by both clients (deploy, nginx,
  Cloudflare, security, etc.).
- `docs/cuo/` — per-client docs for CUO.
- `docs/TAZUO_MULTICLIENT_VIABILITY.md` — original viability analysis
  that approved this port (Option A path).
- `source/webclient/tazuo-wasm/` — Mercury MT WASM wrapper csproj
  (TBD — Phase 2).
