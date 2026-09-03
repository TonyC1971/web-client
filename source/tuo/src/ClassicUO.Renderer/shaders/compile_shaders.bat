fxc.exe /T fx_2_0 /O3 IsometricWorld.fx
fxc.exe /T fx_2_0 /O3 /Fo IsometricWorld.fxc IsometricWorld.fx
fxc.exe /T fx_2_0 /O3 /Fo xBR.fxc xBR.fx
REM WASM variant — MUST be recompiled too or edits to .wasm.fx never reach the
REM build (FileEmbed embeds the .fxc, build.bat does NOT recompile .fx->.fxc).
fxc.exe /T fx_2_0 /O3 /Fo IsometricWorld.wasm.fxc IsometricWorld.wasm.fx

pause