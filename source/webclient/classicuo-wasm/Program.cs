// P4d.1 probe: the asset pipeline is proven (P4c), now try the full
// Bootstrap.Main path with the SDL3→sdl3_* prefix shim in place.
// Expected progression:
//   • GameController.Initialize() previously died on
//     `SDL_SetEventFilter` (DllNotFoundException: SDL3). With the
//     shim, that call resolves to an empty weak stub and returns —
//     the code should proceed to LoadContent().
//   • LoadContent does the heavy lifting: asset loading, Audio init,
//     Scene construction. Expect the next blocker to surface there.

using System;
using System.Collections.Generic;
using System.IO;

// FNA picks backend via env var before any FNA type is touched.
Environment.SetEnvironmentVariable("FNA_PLATFORM_BACKEND", "SDL2");

Console.WriteLine("[P4d-probe] boot");
Console.WriteLine($"[P4d-probe] Directory.Exists('/uo') = {Directory.Exists("/uo")}");

// P5 — the JS host pushes WASM_UO_WS_URL when the WebSocket proxy
// is reachable (main.js derives it from window.location.hostname).
// If unset, fall back to default tcp://127.0.0.1:2593 (will fail
// in the browser, but keeps the argument surface stable for probes).
var wsUrl = Environment.GetEnvironmentVariable("WASM_UO_WS_URL");
var hasWs = !string.IsNullOrEmpty(wsUrl);
// Parse ws://host:port/path into (host, port). CUO's NetClient.Connect
// only takes (ip, port), so we hand it `ws://host/path` as ip and the
// port separately. It builds `{ip}:{port}` internally; the resulting
// URI parses correctly because the WS scheme URI tolerates a port
// suffix on host — e.g. `ws://localhost/ws:8080`. We avoid that
// ambiguity by passing the host-with-scheme as ip and port separately.
var (cuoIp, cuoPort) = ParseWsUrl(wsUrl, fallbackIp: "127.0.0.1", fallbackPort: "2593");

Console.WriteLine($"[P4d-probe] WASM_UO_WS_URL='{wsUrl ?? "<unset>"}' -> ip='{cuoIp}' port={cuoPort}");

// v0.3.5: per-shard UO client version. main.js stashes the chosen
// shard's `clientVersion:` (from servers/<slug>.yaml) into the
// WASM_UO_CLIENT_VERSION env var before module init. Falls back to
// the previous hardcoded default if the env var is unset (legacy
// loaders) or somehow blank.
var clientVersion = Environment.GetEnvironmentVariable("WASM_UO_CLIENT_VERSION");
if (string.IsNullOrWhiteSpace(clientVersion)) clientVersion = "7.0.45.1";
Console.WriteLine($"[P4d-probe] clientversion='{clientVersion}'");

var cuoArgsList = new List<string>
{
    "-uopath", "/uo",
    "-clientversion", clientVersion,
    "-language", "ENU",
    // Audio is now wired through Web Audio (see Sound.cs
    // BROWSER_WASM branch + wasm_play_* shims in SDL3.c +
    // wireWasmAudio in main.js). Enable login music — Chrome's
    // autoplay policy still requires a user gesture before the
    // AudioContext unlocks, so the first sound will only be
    // audible after the first click / keydown.
    "-login_music", "true",
    // Skip plugins — Razor.dll isn't available in the WASM bundle.
    "-plugins", "",
    // Point the login flow at the WebSocket proxy. ip starts with
    // ws:// so NetClient.Connect routes to WebSocketWrapper.
    "-ip", cuoIp,
    "-port", cuoPort,
    // The proxy rewrites 0x8C game-server relay, so the client
    // keeps talking to the same proxy for the game phase. No need
    // to ignore the relay — the rewrite handles it.
    // Autologin intentionally NOT passed — default OFF, so real players land on
    // the LoginGump -> character list and opt in via the in-client "Autologin"
    // checkbox (which now persists, since nothing re-forces it every boot).
};

// Encryption selector. main.js reads `?encrypt=X` (or localStorage
// fallback) and maps it to a byte matching CUO's EncryptionType enum
// (NONE=0, OLD_BFISH=1, BLOWFISH__1_25_36=2, BLOWFISH=3,
// BLOWFISH__2_0_3=4, TWOFISH_MD5=5). We pass it as a normal CLI arg
// — Main.cs:418 handles `-encryption <byte>` and writes into
// Settings.GlobalSettings.Encryption. Unset → inherits whatever is
// in settings.json (default 0 = NONE, fine for ModernUO).
var wasmEncryption = Environment.GetEnvironmentVariable("WASM_UO_ENCRYPTION");
if (!string.IsNullOrEmpty(wasmEncryption))
{
    if (byte.TryParse(wasmEncryption, out var encByte) && encByte <= 5)
    {
        cuoArgsList.Add("-encryption");
        cuoArgsList.Add(encByte.ToString());
        Console.WriteLine($"[P4d-probe] encryption selector: {encByte} (from WASM_UO_ENCRYPTION env var)");
    }
    else
    {
        Console.WriteLine($"[P4d-probe] WARN: WASM_UO_ENCRYPTION='{wasmEncryption}' not parseable as byte 0..5, ignoring");
    }
}

// -reconnect (operator 2026-07-07 "el -reconnect nativo de cuo y tuo"): enable CUO's native
// auto-reconnect so a dropped connection (backgrounded tab throttles the keepalive ping → the WS
// idle-times-out upstream) resumes instead of parking a dead "Connection lost" box. On disconnect
// GameScene.SocketOnDisconnected → LoginScene{Reconnect=true}, whose Update loop re-Connect()s and,
// with saved credentials, re-logs-in (native CUO Reconnect behaviour). Passed as an ARG (not
// GlobalSettings before Bootstrap) because Main.cs reloads settings from file after Program.cs;
// ReadSettingsFromArgs (case "reconnect") applies it AFTER that reload so it sticks. NOT passing
// -reconnect_time: the default 1s retry is correct and the arg has a unit-mismatch trap (see mini-wasm).
cuoArgsList.Add("-reconnect");
cuoArgsList.Add("true");

var cuoArgs = cuoArgsList.ToArray();

// Ignore the IP the proxy's 0x8C rewrite hands us (always
// 127.0.0.1 for loopback-friendliness). Browsers on other LAN
// boxes interpret 127.0.0.1 as their own loopback -- NOT the
// proxy's box -- so they'd fail the game-phase reconnect with
// SocketError. HandleRelayServerPacket sees IgnoreRelayIp=true
// and reconnects to the original Settings.IP (the
// ws://host:port/ws we came in on), which is always correct no
// matter which machine the browser runs on. The seed + 0x91
// still use the 0x8C's authKey.
//
// ClassicUO's Main.cs doesn't parse -ignore_relay_ip as a CLI
// arg, so we set Settings.GlobalSettings directly before
// Bootstrap.Main runs. Settings is a static singleton; the
// value sticks.
ClassicUO.Configuration.Settings.GlobalSettings.IgnoreRelayIp = true;

static (string ip, string port) ParseWsUrl(string url, string fallbackIp, string fallbackPort)
{
    if (string.IsNullOrEmpty(url)) return (fallbackIp, fallbackPort);
    try
    {
        var uri = new Uri(url);
        // Rebuild as `scheme://host[/path][?query]` without the port;
        // pass the port separately so CUO's `{ip}:{port}` concatenation
        // produces a valid URI.
        //
        // v0.3.9 — preserve uri.Query (e.g. `?server=eternal`). Earlier
        // versions dropped it because there was always exactly one shard
        // registered, so the proxy fell back to defaultSlug() and the
        // missing query string was harmless. Adding a second shard
        // (e.g. spheresvr) makes defaultSlug() return null, and the
        // proxy then 404s the WS upgrade with "unknown server slug",
        // showing up as `WebSocket connection ... 404` + Disconnected
        // immediately after the LoginGump appears.
        var pathPart = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
        var queryPart = uri.Query ?? string.Empty;   // includes leading '?'
        var ip = $"{uri.Scheme}://{uri.Host}{pathPart}{queryPart}";
        var port = (uri.Port > 0 ? uri.Port : (uri.Scheme == "wss" ? 443 : 80)).ToString();
        return (ip, port);
    }
    catch
    {
        return (fallbackIp, fallbackPort);
    }
}

Console.WriteLine("[P4d-probe] calling ClassicUO.Bootstrap.Main(args)");
try
{
    ClassicUO.Bootstrap.Main(cuoArgs);
    Console.WriteLine("[P4d-probe] Bootstrap.Main returned");
}
catch (Exception ex)
{
    Console.WriteLine($"[P4d-probe] top-level failure: {ex.GetType().FullName}: {ex.Message}");
    Console.WriteLine($"[P4d-probe] stack:\n{ex.StackTrace}");
    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        Console.WriteLine($"[P4d-probe] inner: {inner.GetType().FullName}: {inner.Message}");
}

Console.WriteLine("[P4d-probe] Main() done");
