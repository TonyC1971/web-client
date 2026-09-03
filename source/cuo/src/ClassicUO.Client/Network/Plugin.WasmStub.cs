// SPDX-License-Identifier: BSD-2-Clause
//
// WASM-only stub for the Plugin class. The desktop `Plugin.cs` loads
// external .dll plugins (Razor, UOSteam clones, etc.) via the
// `cuoapi.dll` native helper + Windows DLL loader. Neither is
// available in a browser WebAssembly target, and plugins make no
// practical sense there anyway.
//
// This file replaces `Plugin.cs` on the WASM build. The csproj adds
// `<Compile Remove="Network/Plugin.cs"/>` under
// `Condition="'$(IsBrowserWasm)' == 'true'"` so only one Plugin class
// is compiled per target.
//
// Member list + signatures match the real Plugin.cs line-for-line
// so the rest of ClassicUO compiles unchanged. Every body is a
// no-op returning `default`.

#if BROWSER_WASM

using System;
using Microsoft.Xna.Framework.Graphics;
using SDL3;

namespace ClassicUO.Network
{
    internal sealed class Plugin
    {
        public static Plugin Create(string path) => null;

        internal static void Tick() { }
        // Same sign-inversion trap as ProcessHotkeys and ProcessSendPacket
        // (PacketHandlers.cs:114): `if (!allowPlugins || Plugin.ProcessRecvPacket(...))`
        // runs AnalyzePacket only when ProcessRecvPacket returns true.
        // Returning false here swallowed 0xA8 ServerList silently -- the
        // packet reached the read stream, was parsed out by
        // GetPacketInfo, but the dispatcher skipped. Return true so the
        // real handler fires. Audit point: any other Plugin.Process*
        // call-site that gates "proceed normally" on a true return.
        internal static bool ProcessRecvPacket(byte[] data, ref int length) => true;
        // NetClient.Send short-circuits when this returns false
        // (line 253-256). Return true so every send actually reaches
        // _sendStream.Enqueue -- same sign inversion that bit
        // ProcessHotkeys (keystrokes) and ProcessRecvPacket (inbound
        // 0xA8 swallowed). The stub always "lets it through".
        internal static bool ProcessSendPacket(ref Span<byte> message) => true;
        internal static void OnClosing() { }
        internal static void OnFocusGained() { }
        internal static void OnFocusLost() { }
        internal static void OnConnected() { }
        internal static void OnDisconnected() { }
        // NOTE: the real Plugin.ProcessHotkeys (Plugin.cs:651) returns
        // `true` to mean "no plugin claimed this key — let the scene /
        // UI process it" and `false` to mean "plugin consumed it as a
        // hotkey, skip the downstream InvokeKeyDown + TEXT_INPUT".
        // Returning `false` here blocked every keystroke from reaching
        // the login textbox because GameController.HandleSdlEvent sets
        // _ignoreNextTextInput = true when ProcessHotkeys is false —
        // so SDL_EVENT_TEXT_INPUT gets swallowed. Return true so the
        // wasm build treats every key as "not a plugin hotkey".
        internal static bool ProcessHotkeys(int key, int mod, bool ispressed) => true;
        internal static void ProcessMouse(int button, int wheel) { }
        internal static void ProcessDrawCmdList(GraphicsDevice device) { }
        internal static unsafe int ProcessWndProc(SDL.SDL_Event* e) => 0;
        internal static void UpdatePlayerPosition(int x, int y, int z) { }

        // Called from PluginHost.cs ctor that wires the host bindings.
        internal static bool RequestMove(int dir, bool run) => false;
        internal static bool GetPlayerPosition(out int x, out int y, out int z)
        {
            x = 0;
            y = 0;
            z = 0;
            return false;
        }
        internal static bool OnPluginRecv_new(IntPtr buffer, ref int length) => false;
        internal static bool OnPluginSend_new(IntPtr buffer, ref int length) => false;
    }
}

#endif
