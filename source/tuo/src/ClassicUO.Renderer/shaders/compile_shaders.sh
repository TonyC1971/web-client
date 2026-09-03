#!/bin/bash
#winetricks dxsdk_jun2010
wine fxc.exe /T fx_2_0 /O3 IsometricWorld.fx
wine fxc.exe /T fx_2_0 /O3 /Fo IsometricWorld.fxc IsometricWorld.fx
wine fxc.exe /T fx_2_0 /O3 /Fo xBR.fxc xBR.fx
# WASM variant — MUST be recompiled too or edits to .wasm.fx never reach the
# build (FileEmbed embeds the .fxc, build.sh does NOT recompile .fx->.fxc).
wine fxc.exe /T fx_2_0 /O3 /Fo IsometricWorld.wasm.fxc IsometricWorld.wasm.fx
