# Sphere Source-X patches

Unified-diff patches applied to `Sphereserver/Source-X` master during
the spheresvr Docker build (see sibling `Dockerfile`). Each `.patch`
file is documented in its own header — read the patch first, then this
README, before deciding to apply.

## Why patches and not a fork

We do not fork Source-X. Each rebuild pulls fresh upstream master and
applies these diffs on top via `patch -p1`. If upstream rearranges a
touched file enough that the patch's context no longer matches,
`patch` fails with a clear hunk-mismatch message and the docker build
aborts — better than silently shipping a broken image.

To regenerate a patch after upstream drift:
```bash
git clone https://github.com/Sphereserver/Source-X /tmp/source-x-fresh
cd /tmp/source-x-fresh
# apply the existing patch manually OR re-implement the change
# then:
git diff > /path/to/spheresvr/patches/<name>.patch
```

## Available patches

### `webidentity.patch` — v0.3.15-B WebIdentity 0xA4 intercept

Repurposes the legacy `PacketSystemInfo` (packet ID `0xA4`, 149 bytes)
so that when a trusted upstream web proxy emits a 0xA4 frame whose
body opens with the ASCII magic `"CUOWEB"`, the proxy's pre-shared
secret is validated and the real client IP carried in the body
overwrites `CNetState::m_peerAddress`. After that override every
existing per-IP heuristic in Sphere (`MaxPings`,
`MaxConnectRequestsPerIP`, `account.LastIP`, audit logs) sees the
real public IP instead of the docker-bridge IP that all
wasm-webclient sessions share.

> **Don't want to apply the patch?** See the
> [no-patch alternative](../../WEBIDENTITY.md#alternative-skip-the-webidentity-integration)
> in `WEBIDENTITY.md` — disabling the five per-IP counters in
> `sphere.ini` lets the bridge through without recompiling, at the
> cost of losing real-IP moderation for web users (every web user
> appears as `172.22.0.1` to scripts and audit logs, and
> `[BLOCKIP <real-ip>]` no longer works for web). The same doc covers
> the equivalent ModernUO `accountHandler.maxAccountsPerIP=999999`
> workaround.

**B28 (2026-05-04)** — secret now read from **sphere.ini** key
`WebIdentitySecret=` at runtime. Hot-rotation via `[r]` resync (no
recompile). The compile-time `WEBIDENTITY_SECRET` macro is honoured
as a fallback when sphere.ini value is empty (used by sealed Docker
images that bake the secret at image-build time).

**v0.3.25 (2026-05-05) — B29 widened bypass + post-rewrite re-check.**
Two related changes ship together:

1. **B29 bypass widened to wrap `ip.checkPing()`.** Earlier rev kept
   `checkPing()` enforced for trusted proxies as defense-in-depth
   (intent: "operator could ban a compromised proxy"). Observed at
   scale (~10 web logins/min through the bridge): the bridge IP
   trips `MaxPings` within seconds → `m_fBlocked=1`, ttl=300s →
   every subsequent web user rejected. The intended "ban the bridge"
   threat was theoretical (banning the bridge is self-DOS, not
   enforcement; if you need to kill web traffic, stop the proxy
   container). Now the entire `if (...)` rate-limit block is wrapped
   in `(!fTrustedProxy && ...)` — bridge becomes invisible at
   TCP-accept.

2. **Post-rewrite `checkPing()` on the real IP.** Now that the
   bridge is invisible at TCP-accept, the engine never per-IP-checks
   the real client IP — which would silently break
   `[BLOCKIP <real-ip>]` for web users (still works for desktop).
   Fix: after `setPeerAddress(addr)` in both WebIdentity dispatch
   paths (pre-seed + post-seed), look up `IPHistoryManager` for the
   real IP and call `checkPing()`. If `m_fBlocked` is set, or
   `m_iPings` exceeds `MaxPings`, kick the connection. Restores
   parity between desktop and web for IP-based moderation.

   - Desktop client: TCP-accept checkPing on real IP — unchanged.
   - Web client: TCP-accept checkPing skipped (bridge); post-0xA4
     dispatch → real IP rewrite → checkPing on real IP — equivalent
     to a desktop client connecting from that IP.
   - Per-IP counters (`m_iPings`, etc.) for the real IP accumulate
     normally via the `m_iPings++` side effect of `checkPing()`.
   - Proxy-side rate limiting (token-bucket per JWT sub or per IP
     in `UOProxy.ts`) provides the first-line defense; this engine
     check is the second line that survives a compromised proxy
     (per-real-IP throttling can no longer be bypassed by funneling
     through one trusted-proxy whitelist).

**Wire format:** mirrors
[ClassicUO/packets/WebIdentity.ksy](https://github.com/ClassicUO/packets/blob/main/WebIdentity.ksy)
byte-for-byte. 149 bytes total = `0xA4` + 6-byte ASCII `"CUOWEB"` +
1-byte version + 4-byte BE timestamp + seven null-terminated UTF-8
strings (secret, userId, connectingIp, externalAuthProvider,
externalAuthUsername, externalAuthId, role) + zero padding. See
[`webidentity/DESIGN.md`](../DESIGN.md) for the
detailed spec + security considerations.

**Falls through cleanly for non-web clients.** A real desktop OSI UO
client sends a legitimate SystemInfo packet whose first 6 bytes are
NOT `"CUOWEB"`; the patched handler `memcmp`s against the magic, sees
it doesn't match, and falls through to `skip(148)` (the original
upstream behaviour). No regression for desktop play.

**Ten hunks across six source files (B17 + B28 + B29 + v0.3.25):**

| File | Hunks |
|---|---|
| `src/network/CNetState.h` | **1.** Add public `setPeerAddress(const CSocketAddress&)`. |
| `src/game/CServerConfig.h` | **1** (B28+B29). Add two `CSString` fields side-by-side: `m_sWebIdentitySecret` and `m_sWebIdentityTrustedProxy`. |
| `src/game/CServerConfig.cpp` | **2** (B28+B29). RC enum + keyword table — both extended with `WEBIDENTITYSECRET` and `WEBIDENTITYTRUSTEDPROXY` so sphere.ini parser maps both keys to their fields. |
| `src/network/receive.cpp` | **1** (multi-edit region). Replace body of `PacketSystemInfo::onReceive`. Add `GetWebIdentitySecret()` helper (reads ini → falls back to macro), `TryProcessWebIdentityPreSeed()` exported for CNetworkInput.cpp, AND **v0.3.25 post-rewrite `checkPing()`** on the real IP in both dispatch paths (kicks if blocked or `MaxPings` exceeded, mirrors desktop TCP-accept behaviour). |
| `src/network/CNetworkInput.cpp` | **3.** (a) **B17**: replace the Release-no-op `ASSERT(buffer->getRemainingLength() <= sizeof(CEvent))` in the encryption-setup path with a real bounds check (riskiest pre-auth memcpy in the audit). (b) Detect 0xA4 preamble before the `INT8_MAX` cap. (c) Dispatch `else if (fWebIdentity)` to the helper before stream-cipher init. |
| `src/network/CNetworkManager.cpp` | **2** (B29 + v0.3.25). (a) In `acceptNewConnection()` after the EXC_SET_BLOCK("ip history"), parse `WebIdentityTrustedProxy` (comma-separated list, exact match) into a `bool fTrustedProxy` flag. (b) **v0.3.25**: wrap the ENTIRE per-IP rate-limit `if` (including `ip.checkPing()` itself, which covers `m_fBlocked` + `m_iPings >= MaxPings`) with `(!fTrustedProxy && ...)` so the bridge is fully invisible to TCP-accept enforcement. Real-IP enforcement happens post-0xA4 in `receive.cpp` (see above). |

**Secret rotation (B28 path).** Edit sphere.ini's `WebIdentitySecret=`
key on disk and issue `[r` from the Sphere console (or restart the
container). Update the matching `webIdentity.secret` in the proxy YAML
and restart the proxy. **No recompile.**

```bash
# 1. Generate fresh secret
openssl rand -hex 24
# 2. Edit sphere.ini → WebIdentitySecret=<new value>
# 3. Edit servers/<slug>.yaml → webIdentity.secret: <new value>
# 4. From Sphere console:  [r
# 5. Restart proxy:
sudo docker compose -f /path/to/your/uonexus-minimal/docker-compose.yml \
  restart proxy
```

**Legacy path (compile-time macro).** Still supported as fallback —
useful for sealed Docker images that bake the secret at build time
via `-DCMAKE_CXX_FLAGS="... -DWEBIDENTITY_SECRET=\"\\\"${SECRET}\\\"\""`.
If both are set, sphere.ini wins.

Empty in both → intercept disabled (kWebIdentitySecretLen >= 8 short
circuits, handler behaves identically to upstream).

**Verification after a fresh build.** Boot the spheresvr container and
look for the `[WebIdentity]` log lines on a wasm-client connect:

```
[WebIdentity] accepted: real=203.0.113.5 userId=12345... role=user authProv=Discord
```

The disconnect line that follows should also show the real IP:

```
... Client disconnected ... IP='203.0.113.5'
```

If you see the bridge IP instead, the patch didn't apply or the proxy
isn't sending the preamble — check `docker compose build spheresvr`'s
"Before/After patch 3" log lines to confirm the inject ran.

## Extrapolating to your own Sphere shard

You have **three options**, listed easiest → most-control:

### Option 1 — Automated installer (recommended)

This folder ships `apply_webidentity.py` plus thin OS wrappers
(`apply_webidentity.bat` for Windows, `apply_webidentity.sh` for
Linux/macOS). It edits the 3 source files in-place, makes timestamped
backups, generates a secret, and prints the exact compiler flag.
Idempotent — re-running on a patched tree is a no-op.

```bash
# Linux / macOS / WSL:
./apply_webidentity.sh /path/to/your/Source-X

# Windows (CMD/PowerShell):
apply_webidentity.bat C:\path\to\your\Source-X
```

Requires Python 3.8+. After it finishes, recompile Sphere with the
`WEBIDENTITY_SECRET` flag it printed. See
[`INSTALL_MANUAL.md`](INSTALL_MANUAL.md) section 7 for the
exact CMake / Visual Studio invocation.

### Option 2 — Classic `patch -p1` (CI / Docker)

If you build inside a Dockerfile or a CI script that already has the
GNU `patch` utility installed, just copy `webidentity.patch` into
your source tree and apply it:

```bash
patch -p1 < webidentity.patch
```

This is what our own [`Dockerfile`](../Dockerfile) does at build
time. The patch is unified-diff format and follows the standard
`-p1` convention.

### Option 3 — Manual edit

If neither Python nor `patch` are available (e.g. you're editing on
a Windows box without Python), follow [`INSTALL_MANUAL.md`](INSTALL_MANUAL.md)
step by step. It walks through the 8 hunks file by file with
copy-paste blocks.

### Common to all three options

- **Generate a per-shard secret.** Don't reuse <share>'s — per-shard
  secrets contain blast radius if one leaks. The automated installer
  generates one with `secrets.token_hex(24)`; manual: `openssl rand -hex 24`.
- **Drop the SAME secret into your proxy's per-shard YAML.** If you run
  the uonexus-minimal proxy (this repo), that's the
  `webIdentity.secret` field in `servers/<slug>.yaml`.
- **Bump the pre-accept connection counters in `sphere.ini`** — see
  next section. WebIdentity rewrites the IP **after** the 0xA4 frame
  is parsed; three counters fire at TCP-accept (before that), so
  every connection from your proxy's bridge IP looks identical to
  Sphere and the defaults rate-limit you out almost immediately.
- **Verify the first wasm-client connect**: `[WebIdentity] accepted:
  real=<ip>` line should appear in the Sphere log. If it doesn't,
  see "Troubleshooting" in `INSTALL_MANUAL.md`.

## Required `sphere.ini` companion change (WebIdentityTrustedProxy)

WebIdentity rewrites `m_peerAddress` **after** the 0xA4 frame is read
and the secret validated. Five Sphere connection counters care about
that field — but only **two** are checked after the rewrite (`MaxPings`,
`MaxConnectRequestsPerIP` — both decay-counted but the engine reads
them post-0xA4 in some paths). Three more (`ConnectingMaxIp`,
`ClientMaxIP`, `ConnectingMax`) are evaluated at TCP-`accept()`,
before any byte is read, so they always see your proxy's docker
bridge IP (e.g. `172.22.0.1`). All legitimate users share that IP,
so the defaults rate-limit them as if a single attacker were flooding.

**B29 fixes this with an engine-level whitelist.** The patch above
adds a new sphere.ini key `WebIdentityTrustedProxy=` (comma-separated
list of IPs allowed to bypass the per-IP rate-limit checks at
TCP-accept). Every legit web user comes from the bridge IP, so adding
it to the whitelist means the engine skips ALL per-IP checks
(including `ip.checkPing()` and its `MaxPings` / `m_fBlocked`
sub-checks) for those connections.

**v0.3.25 closes the moderation gap.** Once the bridge is invisible at
TCP-accept, the engine never per-IP-checks the real client IP — which
would silently break `[BLOCKIP <real-ip>]` for web users (still works
for desktop). The patch now also calls `checkPing()` on the rewritten
real IP **after** the 0xA4 dispatch, in both code paths:

- `PacketSystemInfo::onReceive` (post-seed path)
- `TryProcessWebIdentityPreSeed` (pre-seed path, used by encrypted
  shards e.g. Sphere Source-X / Sphere 2.0.3 with Blowfish cipher)

If the real IP is in `m_fBlocked` state or has tripped `MaxPings`, the
connection is closed via `markReadClosed()` and a `[WebIdentity]
post-rewrite reject` line is logged. Per-IP throttle accumulates per
real IP via `m_iPings++` (the side effect of `checkPing()`).

Real per-IP enforcement happens in three layers:
- **(a) Proxy layer** — token-bucket rate limit + admin ban list in
  `UOProxy.ts`. First line of defense for typical abuse patterns.
- **(b) Engine post-rewrite** — `checkPing()` on the real IP in
  `receive.cpp` (this is the v0.3.25 addition). Catches a compromised
  proxy that bypasses (a); `[BLOCKIP]` from the GM console works
  uniformly across desktop and web.
- **(c) Scripts** — `account.LastIP` / `<SOCKETIP>` see the rewritten
  real IP. Custom rate-limit / abuse-detection scripts work the same
  for web and desktop.

Non-proxy clients (desktop UO direct to port 2593) keep upstream's
per-IP rate-limiting unchanged: their TCP-accept already sees their
real IP.

**Apply.** Edit your `sphere.ini`:

```ini
// Companion to WebIdentitySecret. Comma-separated list of proxy IPs
// allowed to bypass the per-IP rate-limit checks at TCP-accept.
// Empty = disabled (legacy behaviour). Live-reloadable via [r].
WebIdentityTrustedProxy=172.22.0.1
```

(Replace `172.22.0.1` with your proxy's actual bridge IP. For docker
deploys, run `docker network inspect <network>` on the proxy's
network to find it. Multiple proxies: comma-separated, no spaces
required: `172.22.0.1,10.0.0.5`.)

**The connection limits stay at upstream defaults** — no need to
bump `ClientMaxIP`/`ConnectingMax`/`ConnectingMaxIp` to 999999 like
the old v0.3.15-B-rev4 workaround required. The whitelist makes
the bypass surgical (proxy only) rather than global.

**Symptom if you skip this** (ie. set `WebIdentitySecret=` but leave
`WebIdentityTrustedProxy=` empty): a burst of ~5 simultaneous
logins (one client login + one relay reconnect = two TCP connects
each) trips `ConnectingMaxIp=8`, Sphere fires `f_onserver_connectreq_ex`,
the bridge IP gets banned for `NetTTL` seconds (default 300s = 5 min),
and **every user behind the proxy** gets rejected with:

```
ERROR: Blocked connection from '172.22.0.1' [pings=9, connecting=9, connected=9].
ERROR: Reject reason: CONNECTINGMAXIP reached 9/8.
```

`WebIdentitySecret` is hot-reloadable via `[r` (the patch wires it
into the keyword table), and so is `WebIdentityTrustedProxy`. No
restart needed for either after the initial deploy.

## Reporting issues

If the patch fails to apply against a fresh upstream `Sphereserver/Source-X`
master — meaning upstream refactored the touched files enough that the
context lines drift — open an issue at
<https://github.com/rootmancer/uonexus-minimal/issues> with
the `Before patch 3` / `After patch 3` build log lines and we'll
refresh the patch context.

If you write a similar patch for **a different Sphere fork** (Sphere
56b, Sphere 0.55, JuiceUO's Sphere derivative, etc.), please open a
PR — we'll add the per-fork patch as a sibling here and link it from
[the WebIdentity notes](https://github.com/rootmancer/uonexus-minimal/blob/main/webidentity/DESIGN.md).

## License

`webidentity.patch` is a derivative work of `Sphereserver/Source-X`,
licensed under GPL-3.0 (same as upstream Source-X). Reproducing the
patch in another shard's source tree is allowed under that license;
pre-built binaries containing the patched code must comply with
GPL-3.0 distribution requirements (provide source on request).
