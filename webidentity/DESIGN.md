# WebIdentity — design notes, wire format and per-emulator detail

> ⚠️ **This is the reference, not the instructions.** If you just want to switch WebIdentity on for
> your shard, read [README.md](README.md) — it is a page long. This document is the packet spec, the
> reasoning behind each decision, the full per-emulator walkthroughs, and the security analysis.
>
> Sections marked **(US)** are notes for maintainers of the proxy; **(SHARD OWNER)** sections are the
> long-form version of what the README summarises.

**Project policy** that shapes this design: we do not fork emulators to make our code work. Where
upstream ClassicUO Web has published a reference implementation that uses reflection or script-level
hooks rather than engine modifications, we apply it verbatim and treat the upstream repository as the
source of truth. Where no upstream reference exists (Sphere Source-X), we publish our own as
documentation — "open this file, find this line, change it to that, here is why" — rather than as an
opaque drop-in.

## Why this exists

Without WebIdentity, every WebSocket client connecting through this
proxy hits the upstream shard from the same docker-bridge IP
(`172.22.0.1` on NAS, similar elsewhere). The shard's per-IP
defences (`MaxPings`, `MaxConnectRequestsPerIP`, account-throttle,
audit logs) all see one global IP and treat everyone like the same
person. That breaks two things:

1. A single bad actor's reconnect storm bans the proxy IP at the
   shard, locking out everyone.
2. Audit logs (who connected when, from where) cannot distinguish
   users.

The official ClassicUO Web client at `play.classicuo.org` solved this
in 2024 with the WebIdentity packet — a 149-byte preamble emitted on
every upstream socket carrying real IP plus a pre-shared secret,
validating proxy → shard authenticity. We mirror their public
[`WebIdentity.ksy`](https://github.com/ClassicUO/packets/blob/main/WebIdentity.ksy)
spec byte-for-byte and reuse their ServUO/ModernUO/RunUO reference
implementations directly. Shards already integrated for
play.classicuo.org work with this proxy unchanged (with their secret
moved to our config).

## Alternative: skip the WebIdentity integration

Some shard owners may not want to apply the WebIdentity layer (they
don't want to recompile Sphere, don't want to maintain a custom script
in ModernUO, or don't need real-IP visibility). The minimum-viable
workaround is to **disable the per-IP defences that the proxy bridge
would otherwise trip**. Both engines support this, with different
trade-offs.

### Sphere (Source-X) — limits-disabled workaround

Edit `sphere.ini` and set the five per-IP counters to 0:

```ini
MaxPings=0
MaxConnectRequestsPerIP=0
ClientMaxIP=0
ConnectingMaxIp=0
ConnectingMax=0
```

Hot-reload via `[r]` from the GM console — no restart needed.

| Aspect | With WebIdentity patch (B28+B29+v0.3.25) | Without patch + limits=0 |
|---|---|---|
| Web users connect without auto-ban | ✓ | ✓ |
| Audit log shows real player IP | ✓ | ✗ — always shows the bridge IP |
| `[BLOCKIP <real-ip>]` works for web | ✓ — post-rewrite re-check fires | ✗ — every web user shares the bridge IP |
| Per-account bans | ✓ | ✓ |
| Scripts using `<SRC.IP>` | sees real IP | sees bridge IP |
| Engine recompile required | yes | no |
| Patch maintenance burden when upstream drifts | yes | none |
| Per-IP throttling for real web users | yes (engine `checkPing()` post-rewrite) | none |
| Per-IP throttling for direct desktop users | unchanged (TCP-accept default) | **DISABLED for everyone, including desktop** |

**Caveat — desktop traffic is collateral damage.** Setting these
counters to 0 disables them globally. If your shard accepts both web
and desktop traffic on the same listening port, desktop attackers
also bypass the throttle. The WebIdentity patch keeps desktop
unchanged (its TCP-accept already sees the real IP) and only relaxes
the bridge-IP path; the no-patch workaround does not have that
discrimination.

### ModernUO — limits-disabled workaround

ModernUO's default `accountHandler.maxAccountsPerIP` is small
(typically 1). With every web user sharing the proxy bridge IP, only
one account can be online at a time — the second simultaneous web
user is rejected at login.

To skip the WebIdentity custom script and just open the gate, edit
`Distribution/Configuration/modernuo.json`:

```json
{
    "accountHandler.maxAccountsPerIP": "999999",

    // also leave WebIdentity disabled (or simply do not deploy
    // the custom script in Projects/UOContent/Custom/ClassicUO/):
    "ClassicUO.WebIdentitySecret": "",
    "ClassicUO.WebIdentityKickOnBadSecret": "False",
    "ClassicUO.WebIdentityIpLimitWorkaround": "False"
}
```

Restart ModernUO — config changes are not hot-reloaded.

| Aspect | With WebIdentity custom script | Without script + maxAccountsPerIP=999999 |
|---|---|---|
| Web users connect without auto-throttle | ✓ | ✓ |
| `NetState.Address` reflects real player IP | ✓ — Reflection rewrite | ✗ — always bridge IP |
| Audit log shows real player IP | ✓ | ✗ — always bridge IP |
| `Account.Banned=true` (per-account ban) | ✓ | ✓ |
| Custom scripts using `from.NetState.Address` | sees real IP | sees bridge IP |
| Engine recompile required | no — drop-in custom script | no |
| Maintenance burden | trivial — one .cs file in `Custom/ClassicUO/` | none |
| Per-IP throttling for real web users | possible via account.LastIP-based scripts | none |
| Per-IP throttling for direct desktop users | unchanged | **DISABLED for everyone** |

### Why ModernUO is structurally easier than Sphere

ModernUO's plugin architecture lets the WebIdentity 0xA4 handler ship
as a **drop-in custom script**: copy
[`Projects/UOContent/Custom/ClassicUO/WebIdentity.cs`](https://github.com/ClassicUO/ClassicUO.WebClient/blob/main/server/UOContent/Custom/ClassicUO/WebIdentity.cs)
into your shard's `Custom/` tree, set the secret in `modernuo.json`,
restart. The plugin uses Reflection to override `NetState.Address`
post-packet, achieving the same outcome as the Sphere C++ patch
without touching the engine binary.

Sphere Source-X does not expose comparable hooks to scripts, which is
why the Sphere route requires a binary patch
([`patches/webidentity.patch`](../sphere-source-x-adapted/patches/webidentity.patch))
applied during the docker build. The patch maintenance burden falls
on whoever vendors the shard image; upstream Sphere drift can
require regenerating the patch context.

ServUO is in the same family as ModernUO (script-based), and
[ClassicUO publishes a ServUO drop-in](https://github.com/ClassicUO/ClassicUO.WebClient/tree/main/server)
that we reuse unchanged.

### Recommendation matrix

- **Production shard with moderation needs (per-IP bans, real-IP
  audit logs, mixed desktop+web)**: apply the WebIdentity layer for
  your engine. Sphere via the C++ patch, ModernUO via the custom
  script.
- **Hobby / trusted-only / web-only shard**: the limits-disabled
  workaround is acceptable. Document the trade-off (no per-IP
  moderation for web) to your players.
- **Mixed desktop + web traffic where you must keep desktop
  protection**: WebIdentity is the only path. The no-patch
  workaround un-protects desktop too because the rate-limit settings
  are global, not per-listener.

## Upstream reference

The authoritative source for everything in this doc:

- **Spec**: [`ClassicUO/packets/WebIdentity.ksy`](https://github.com/ClassicUO/packets/blob/main/WebIdentity.ksy)
- **ServUO/ModernUO/RunUO drop-in implementations**:
  [`ClassicUO/packets/implementations/RunUO-like/`](https://github.com/ClassicUO/packets/tree/main/implementations/RunUO-like)
- **Vendored snapshot in this repo:**
  [`docs/vendor/classicuo-packets/`](../vendor/classicuo-packets/)
  — full upstream tree mirrored on 2026-05-04 (commit `d7731ad`) so
  this doc stays self-contained against upstream disappearance. See
  [`docs/vendor/README.md`](../vendor/README.md) for the refresh
  procedure.

If upstream revises the spec or refactors the implementations, we
follow them. Diverging would break compatibility for shard owners who
already integrated for play.classicuo.org.

**Coverage (verified 2026-05-04):** upstream ships drop-ins for
ServUO, ModernUO, and RunUO-derivatives only. **Sphere Source-X has
no upstream reference implementation** — the `(SHARD OWNER) Sphere
Source-X integration` section below is our own work, written against
the same `WebIdentity.ksy` spec. If the upstream repo grows a Sphere
impl in the future, refresh the vendored snapshot and rewrite our
Sphere section to match.

## Architecture (3-step ship)

### v0.3.15-A — proxy emits 0xA4 (TypeScript, this repo)

- Per-shard YAML opt-in in `servers/<slug>.yaml`:
  ```yaml
  webIdentity:
    enabled: false                # default — proxy stays bridge-IP behaviour
    secret:  ""                   # 32+ ASCII chars, SAME on proxy + shard
    role:    "user"               # optional override; default "user" for guests, "admin" for DISCORD_ADMIN_IDS
  ```
- When `enabled: true`, the proxy injects a 149-byte 0xA4 frame as the
  **first** bytes on each upstream TCP socket — before any 0x80 / 0x91
  the wasm client would otherwise send. The wasm client never sees
  this packet; it's a server-bound preamble.
- Default `enabled: false` keeps every shard backward-compatible — a
  shard owner who hasn't installed a handler sees no behavioural
  change. A shard with the upstream reference impl installed but
  proxy-side disabled also works (no 0xA4 arrives, fall through to
  bridge-IP-as-real-IP).

### v0.3.15-B — Sphere Source-X handler (no upstream reference, we ship our own)

The shard owner adds three small chunks to their Source-X fork and
rebuilds. ~70 LOC total. We apply the same chunks to our spheresvr
Dockerfile via `RUN perl -i` so our deploy stays patched on every
rebuild. Same patch is published below as copy-paste documentation
for any other Sphere operator.

### v0.3.15-C — ModernUO drop-in (upstream reference)

For shard owners running ModernUO: copy three files from
`ClassicUO/packets/implementations/RunUO-like/` into your shard's
`UOContent/Custom/ClassicUO/` (or equivalent). No engine
modifications — the upstream impl uses reflection on
`NetState.<Address>k__BackingField` to inject the real IP without
touching `Server/Network/`. Compatible with the
"never modify ModernUO engine" rule (CLAUDE.md), so we apply this
to our example-modernuo as well.

### v0.3.15-D — ServUO drop-in (upstream reference)

Same shape as v0.3.15-C but with the ServUO-specific helper file. Not
something we deploy ourselves (no ServUO shard on <share>), included
here as a pointer for shard owners.

## Packet 0xA4 — wire format

Mirrors `WebIdentity.ksy` verbatim. 0xA4 was the legacy `SystemInfo`
packet that retail UO clients emitted for telemetry; emulators
(Sphere, Source-X, ModernUO, RunUO, ServUO) all silently discard it
when no handler is installed. That makes it safe to repurpose: a
shard without our handler swallows the bytes and continues; a shard
WITH our handler reads them and trusts the contained IP.

```
+--------+---------------------------------------------------+
| 1B id  |                  148B body                        |
| 0xA4   |                  (see table)                      |
+--------+---------------------------------------------------+
                                                  total = 149 bytes
```

The 148-byte body opens with three fixed-width fields and then
streams a sequence of **null-terminated UTF-8 strings**, padded to
the 148-byte total with zeros at the tail.

| Field                    | Width        | Description |
|---|---|---|
| `client_type`            | 6 bytes      | ASCII string `"CUOWEB"`. Identifies the proxy implementation; future clients with a similar feature use a different string so a shard's handler can fall through to its original SystemInfo handler when the bytes don't match. |
| `version`                | 1 byte (u1)  | Spec version. Current = `0x01`. Forward-compatibility: handlers should warn but continue when `version > 1`. |
| `timestamp`              | 4 bytes (u4 BE) | Unix seconds when the proxy emitted the packet. Short-lived — upstream reference impl rejects if older than 30 s. |
| `secret`                 | strz         | The pre-shared key. **Sent in plaintext in the packet body.** Must match the shard's configured secret exactly. |
| `user_id`                | strz         | Proxy-internal stable ID for the user. Discord-authed → Discord snowflake. Guests → per-session token. |
| `connecting_ip`          | strz         | The user's real IP as a textual address ("203.0.113.5" or "2001:db8::1"). |
| `external_auth_provider` | strz         | `"Discord"` for Discord-authed sessions, empty for guests. |
| `external_auth_username` | strz         | e.g. `"blank#9244"` for Discord, empty for guests. |
| `external_auth_id`       | strz         | e.g. Discord ID `100000000000000002`, empty for guests. |
| `role`                   | strz         | One of `"user"`, `"admin"`, `"shard-owner"`. Lets a shard fast-path admin Discord IDs into in-game admin handles. |
| (padding)                | bytes        | Zero-padded to total body length 148. |

**Why the spec is plaintext, not HMAC-signed:** the proxy and shard
are assumed to live in a shared trust boundary (same Docker host,
internal LAN, VPN). The connection between them is not user-reachable.
The threat model is "stop the wasm client from forging an arbitrary
IP", not "stop a network-level MITM between proxy and shard". A
capture of one 0xA4 packet leaks the secret — operators must keep the
proxy↔shard hop private. (For shard owners deploying the proxy and
shard on different hosts across the public Internet, wrap the
connection in TLS or a WireGuard tunnel before relying on the secret
in the clear.)

The 30-second freshness check defends against replay over a captured
secret only. It does NOT defend against a fully captured secret. If
your secret leaks, rotate it — see "Security considerations" below.

## (US) Proxy implementation notes

The proxy code lives in `server/src/`. Files affected by v0.3.15-A:

- **`server/src/serverRegistry.ts`** — extend YAML parser to accept
  `webIdentity: { enabled, secret, role? }`. Validate that `secret` is
  ≥32 ASCII chars when enabled; fail boot otherwise (loud warning, no
  silent fallback to disabled — that would silently neuter the
  defence on a typo).
- **`server/src/webIdentity.ts`** (new) — single function:
  ```ts
  export function buildWebIdentityFrame(opts: {
    secret:   string;
    userId:   string;
    realIp:   string;
    discord?: { username: string; id: string } | null;
    role?:    'user' | 'admin' | 'shard-owner';
  }): Buffer
  ```
  Returns the exact 149-byte packet:
  - byte 0 = `0xA4`
  - bytes 1-6 = ASCII `"CUOWEB"`
  - byte 7 = `0x01`
  - bytes 8-11 = current Unix seconds, big-endian u32
  - bytes 12+ = the seven UTF-8 strz fields in order
  - tail = `0x00` padding to 149 total
- **`server/src/UOProxy.ts`** — inside `Session.connectTcp()` after the
  TCP `connect` event fires, before any other write:
  ```ts
  if (this.target.webIdentity?.enabled) {
    const frame = buildWebIdentityFrame({...});
    this.tcpSocket.write(frame);
    recordC2sBytes(this.tcpSocket, frame.length); // so the v0.3.14
                                                  // shard-cooldown logic
                                                  // doesn't false-trigger
  }
  ```

Order on the upstream socket becomes:

```
[proxy emits 149-byte 0xA4 frame] → [client's first 0x80 / 0xEF] → normal flow
```

For shards with `enabled: false`, the 0xA4 frame is omitted entirely;
no behavioural change.

**Testing the proxy side without a real shard handler:** boot a
netcat listener (`nc -l 2593`), connect a wasm client through the
proxy with `webIdentity.enabled: true`, hexdump the bytes — first 149
must match the 0xA4 spec. The `xxd -c 32` output should show
`a4 43 55 4f 57 45 42 01 …` where `43 55 4f 57 45 42` = `"CUOWEB"`,
`01` = version, then big-endian timestamp, then the strz blocks.

## (SHARD OWNER) ModernUO / ServUO / RunUO — copy upstream files (with caveats for current ModernUO)

Upstream ClassicUO Web has a ready-to-use reference implementation
that requires **no engine modifications**. It uses reflection to
inject the real IP into `NetState.<Address>k__BackingField`, so it
drops cleanly into your `Scripts/` folder.

> **⚠️ Current ModernUO master needs four small adaptations to the
> upstream files** — the reference impl was written against an older
> ModernUO API and silently no-ops as of 2026 without these fixes. See
> the **"ModernUO master adaptations (2026-05-04)"** sub-section
> below before pasting. ServUO's adaptation is unaffected.

### Steps

1. Open the upstream impl folder:
   <https://github.com/ClassicUO/packets/tree/main/implementations/RunUO-like>
   (or use the vendored snapshot at
   [`docs/vendor/classicuo-packets/`](../vendor/classicuo-packets/))
2. Copy these files to your shard's scripts folder:

   **For ServUO**:
   ```
   Scripts/ClassicUO/
     ├── Network.cs
     ├── Network.ServUO.cs
     └── WebIdentity.cs
   ```

   **For ModernUO**:
   ```
   Projects/UOContent/Custom/ClassicUO/      (or your "Scripts" equivalent)
     ├── Network.cs
     ├── Network.ModernUO.cs
     └── WebIdentity.cs
   ```

   The shared `Network.cs` + `WebIdentity.cs` work on both; the
   `Network.<emulator>.cs` is the per-emulator glue. Don't take the
   files for the wrong emulator.

3. Restart the shard. On boot, `WebIdentity.Configure()` registers a
   handler that intercepts 0xA4 packets — but only if `client_type`
   is `"CUOWEB"`. Other 0xA4 packets (from a desktop OSI client)
   transparently fall through to the original handler.

### ModernUO master adaptations (2026-05-04)

The upstream RunUO-like reference impl was written against an older
ModernUO API. As of upstream ModernUO master at the time of writing,
four small edits to the pasted files are required for the handler to
actually fire. Without them, `Configure()` runs and reports
"registering 0xA4 handler" but the handler is never invoked at
runtime — silent no-op.

**Adaptation 1 — `using` namespace.** ModernUO renamed
`Server.Network.CircularBufferReader` to `System.Buffers.SpanReader`.
Replace in `Network.cs`:
```diff
- using Server.Network;
- // and the alias `using PacketReader = Server.Network.CircularBufferReader;`
+ using System.Buffers;
+ using Server.Network;
```
And in `WebIdentity.cs`:
```diff
- #if !ServUO
- using PacketReader = Server.Network.CircularBufferReader;
- #endif
+ using System.Buffers;
+ using PacketReader = System.Buffers.SpanReader;
```

**Adaptation 2 — function pointer signature.** ModernUO dropped the
`int packetLength` argument from packet handlers and switched the
return type from `bool` to `void`. In `Network.cs`:
```diff
- public delegate bool HandlerFn(NetState ns, PacketReader reader, int packetLength);
+ public delegate bool HandlerFn(NetState ns, SpanReader reader);
```
And in `Network.ModernUO.cs`:
```diff
- private static readonly delegate*<NetState, CircularBufferReader, int, void> OnReceive = &_OnReceive;
- private static void _OnReceive(NetState state, CircularBufferReader reader, int packetLength)
+ private static readonly delegate*<NetState, SpanReader, void> OnReceive = &_OnReceive;
+ private static void _OnReceive(NetState state, SpanReader reader)
```
And update `WebIdentity.cs`:
```diff
- private static bool WebIdentityInterceptSystemInfo(NetState ns, PacketReader reader, int packetLength)
+ private static bool WebIdentityInterceptSystemInfo(NetState ns, SpanReader reader)
```

**Adaptation 3 — don't re-read packet ID in `_OnReceive`.** Upstream
`_OnReceive` does:
```cs
reader.Seek(0, SeekOrigin.Begin);
var id = reader.ReadByte();
if (Handlers[id]?.Invoke(state, reader) is null or false) { ... }
```
Current ModernUO's `NetState.HandlePacket` already advanced past the
1-byte packet ID and slices a SpanReader pointing at the BODY before
calling our `OnReceive`. So `reader.ReadByte()` reads the first
**body** byte (`'C'` from `"CUOWEB"`, = `0x43`), looks up
`Handlers[0x43]` (null), and silently drops the packet.

Fix: hard-code the intercept ID and skip the ReadByte:
```cs
private static void _OnReceive(NetState state, SpanReader reader)
{
    const int InterceptId = 0xA4;
    if (Handlers[InterceptId]?.Invoke(state, reader) is null or false)
    {
        reader.Seek(0, SeekOrigin.Begin);
        OriginalHandlers[InterceptId]?.OnReceive(state, reader);
    }
}
```

**Adaptation 4 — `[CallPriority(100)]` on `Configure()`.**
ModernUO's `IncomingPlayerPackets.Configure()` already registers a
default 0xA4 SystemInfo handler. Both Configure methods share the
default priority 50 so order is arbitrary; ModernUO's frequently runs
after ours, **silently overwriting our 0xA4 registration**. Fix: pin
our Configure to priority 100 so it runs after every default-priority
registration:
```cs
[CallPriority(100)]
public static void Configure()
{
    RegisterHandler(0xA4, 149, false, WebIdentityInterceptSystemInfo);
}
```

After all four edits, `Configure()` runs late enough that our handler
sticks, the 0xA4 reaches our `WebIdentityInterceptSystemInfo`, the
secret is validated, and the reflection IP override fires. The
disconnect log line on the shard side then shows the real client IP
instead of the docker-bridge IP.

### Why ServUO doesn't need adaptations

The four issues above are all ModernUO API drift: ServUO retains the
older API the upstream reference impl was written against
(`CircularBufferReader`, `delegate*<…, int, void>` signature, packet-id
reader at byte 0, no `IncomingPlayerPackets` default 0xA4 handler).
The shipped `Network.ServUO.cs` matches verbatim — drop in, restart.

4. Configure the secret. ServUO: create `Config/ClassicUO.cfg` with:
   ```ini
   ClassicUO.WebIdentitySecret=<32+ ASCII chars, SAME as proxy>
   ClassicUO.WebIdentityKickOnBadSecret=true
   ClassicUO.WebIdentityIpLimitWorkaround=true
   ```
   ModernUO: edit `modernuo.json`, add to `settings`:
   ```json
   {
     "ClassicUO.WebIdentitySecret": "<32+ ASCII chars>",
     "ClassicUO.WebIdentityKickOnBadSecret": true,
     "ClassicUO.WebIdentityIpLimitWorkaround": true
   }
   ```

5. Restart the shard. Ask the proxy operator to set the matching
   `secret` in their `servers/<your-slug>.yaml` and restart the
   proxy.

### What the reference impl does (so you can audit before pasting)

`Network.cs` defines a chain-of-responsibility wrapper around the
emulator's packet handler registry: when you call `RegisterHandler`,
it stores the original handler and installs a new one that calls
yours first; if yours returns `false`, the original is invoked
instead. That's how "fall through to OSI SystemInfo handler when
client_type isn't CUOWEB" is implemented without changing any
emulator core code.

`WebIdentity.cs` registers a handler for packet ID `0xA4`, length 149,
not-in-game (`Configure()` is called once at boot). The handler:

1. Reads `client_type` (6 bytes ASCII). If not `"CUOWEB"`, returns
   false → original SystemInfo handler runs, no impact on
   non-CUO-Web clients.
2. Reads `version`. Logs a warning if > 1 (forward compat); does not
   reject.
3. Deserialises the rest of the body via
   `ClassicUOWebIdentityEventArgs.DeserializeFromPacket(reader)`.
4. Validates `secret == config.Secret`. If wrong AND
   `WebIdentityKickOnBadSecret`, calls `state.Disconnect(...)` with
   a reason and returns true (consumed). Otherwise logs and falls
   through.
5. Validates `(now - timestamp).TotalSeconds <= 30`. If older,
   disconnects.
6. If `WebIdentityIpLimitWorkaround` is on AND the secret was valid,
   reflectively sets `NetState.<Address>k__BackingField` to the
   parsed `connecting_ip`, and updates the `_toString` /
   `m_ToString` cached field so logs show the real IP.
7. Fires `OnWebIdentityReceived` event so your shard scripts can
   read auxiliary fields (UserId, Role, ExternalAuth*) and act on
   them — e.g. auto-grant in-game admin handles to Discord-authed
   shard-owners.

That's it. ~150 LOC across the three files. Read them before pasting
— it's good security hygiene.

### Why no engine fork is needed

`<Address>k__BackingField` is the compiler-generated backing field
for an auto-property in C#. ServUO and ModernUO declare
`NetState.Address` as a public auto-property; the C# compiler emits
a private backing field with this stable name. Reflection with
`BindingFlags.Instance | BindingFlags.NonPublic` reaches it without
modifying source.

**Caveat:** if upstream ServUO/ModernUO ever change `Address` from
an auto-property to a manually-coded property with a custom backing
field name, the reflection lookup returns null and the IP override
silently no-ops. Watch the upstream commits; the reference impl
warns about this in its XML doc comments.

### (US) Applied to example-modernuo

CLAUDE.md prohibits modifying `Projects/Server/`
(ModernUO engine). The reference impl files all live under
`Server.ClassicUO` namespace but go in the SCRIPTS folder, not the
engine — for ModernUO that means `Projects/UOContent/Custom/`. That
path is allowed per CLAUDE.md ("Custom game content lives in
`Projects/UOContent/Custom/` — that one can be edited").

So our deploy DOES install the v0.3.15-C drop-in. Path:
```
Projects/UOContent/Custom/ClassicUO/
  ├── Network.cs              (copy from upstream)
  ├── Network.ModernUO.cs     (copy from upstream)
  └── WebIdentity.cs          (copy from upstream)
```

Configure via `Distribution/modernuo.json`:
```json
{
  "settings": {
    "ClassicUO.WebIdentitySecret": "<32+ chars, also set on proxy>",
    "ClassicUO.WebIdentityKickOnBadSecret": true,
    "ClassicUO.WebIdentityIpLimitWorkaround": true
  }
}
```

Restart ModernUO via the project's bat (per `feedback_dotnet_build_before_modernuo_restart.md`
+ `feedback_always_taskkill_before_launch.md` rules).

## (SHARD OWNER) Sphere Source-X integration

Upstream has no Sphere reference impl, so we wrote our own
(SHIPPED v0.3.15-B 2026-05-04). Same spec, same wire format, same
secret semantics — different language and integration point. Lives
in this repo at
[`docs/sphere-source-x-adapted/`](../sphere-source-x-adapted/) ready to
drop into your own Sphere fork.

> **Both plaintext and encrypted Sphere shards supported as of rev2
> (shipped 2026-05-04).** The patch grew a third hunk in
> `src/network/CNetworkInput.cpp` that detects 0xA4 PRE-seed (before
> Sphere's stream cipher initialises). Plaintext shards process 0xA4
> via `PacketSystemInfo::onReceive` (post-seed). Encrypted shards
> process it via `TryProcessWebIdentityPreSeed` invoked from
> `CNetworkInput.cpp`'s pre-seed branch. Same wire format, same
> secret, same IP override — only the dispatch point differs.

### What the patch does

Eight hunks across five files (B17 oversized-login hardening + B28
WebIdentity, runtime-rotatable secret in sphere.ini + plaintext +
encrypted Sphere shard support). The whole patch is a real `git
diff -u` output — apply with `patch -p1` (or the
`apply_webidentity.py` installer) from your Source-X clone root:

| File | Hunks |
|---|---|
| `src/network/CNetState.h` | **1 hunk.** Public accessor `setPeerAddress(const CSocketAddress&)` so both pre- and post-seed handlers can mutate the protected `m_peerAddress` field. |
| `src/game/CServerConfig.h` | **1 hunk.** Adds `CSString m_sWebIdentitySecret` field on the global config object (alongside the existing `_iMaxConnectRequestsPerIP` etc network knobs). |
| `src/game/CServerConfig.cpp` | **2 hunks.** New `RC_WEBIDENTITYSECRET` enum entry + matching `WEBIDENTITYSECRET` row in the keyword table mapping the `sphere.ini` key to `ELEM_CSTRING`. Wires the existing CResource keyword parser to populate `g_Cfg.m_sWebIdentitySecret` from `sphere.ini` at boot + on every `[r` resync. |
| `src/network/receive.cpp` | **1 hunk** (three sub-edits in one contiguous region). (a) `static const char* GetWebIdentitySecret()` reads `g_Cfg.m_sWebIdentitySecret` first, falls back to compile-time `WEBIDENTITY_SECRET` macro when ini is empty, returns `""` when both are unset = WebIdentity disabled. (b) Replaces `PacketSystemInfo::onReceive` body with the WebIdentity intercept (post-seed path — for plaintext shards). (c) Adds `TryProcessWebIdentityPreSeed(net, data, len)` for the pre-seed path. Both call `GetWebIdentitySecret()` for the secret. |
| `src/network/CNetworkInput.cpp` | **3 hunks.** (a) **B17**: replaces the Release-no-op `ASSERT(buffer->getRemainingLength() <= sizeof(CEvent))` in the encryption-setup path with a real bounds check that disconnects + warns — the riskiest pre-auth memcpy in the network audit. (b) Adds an `fWebIdentity` flag in the pre-seed branch + exempts 149-byte 0xA4 frames from the existing `INT8_MAX` (127-byte) length cap. (c) Dispatches matching frames to `TryProcessWebIdentityPreSeed` BEFORE the stream cipher initialises so encrypted shards still receive plaintext IP overrides. |

We do NOT add a new `CMD_WebIdentity` enum entry or touch `packet.h` —
0xA4 already has a Sphere-side dispatch slot (`XCMD_Spy`) registered
in `CPacketManager::registerStandardPackets`. We just replace what
runs there.

### Drop-in instructions

The full ready-to-use files are in
[`docs/sphere-source-x-adapted/`](../sphere-source-x-adapted/) of this
repo. The `README.md` there walks through three install paths:

1. **One-click installer** — `patches/apply_webidentity.{py,sh,bat}`
   edits the 5 source files in-place, makes timestamped backups,
   generates a fresh per-shard secret. Idempotent. Recommended for
   most operators.
2. **Classic `patch -p1`** — for CI / Docker pipelines that already
   have GNU `patch`. The unified-diff is also vendored at
   `patches/webidentity.patch`.
3. **AI-assisted** — paste `patches/AI_INSTALL_PROMPT.md` into
   Claude/ChatGPT/Gemini along with your source files when the
   regular installer fails on a heavily-drifted upstream fork.

After install:

1. Generate a per-shard secret (`openssl rand -hex 24` or let the
   Python installer generate one).
2. Add to your `sphere.ini`:
   ```ini
   WebIdentitySecret=<the hex>
   ```
3. **Set `WebIdentityTrustedProxy=` in the same `sphere.ini`** —
   see "Required `sphere.ini` companion key" below. Without this,
   your bridge IP gets rate-limited within ~5 logins.
4. Drop the SAME secret into the proxy YAML's
   `webIdentity.secret` field in `servers/<your-slug>.yaml`.
5. Restart Sphere (one time after the patch is first applied).
   Both `WebIdentitySecret` and `WebIdentityTrustedProxy` are
   hot-reloadable via `[r]` afterwards (the patch wires both
   into the keyword table).
6. Restart proxy.
7. Verify via the `[WebIdentity] accepted: real=...` log line on the
   shard side.

#### Required `sphere.ini` companion key (B29 trusted-proxy bypass)

The 0xA4 frame rewrites `m_peerAddress` **after** Sphere has already
incremented three connection counters at TCP-`accept()` against your
proxy's docker bridge IP (e.g. `172.22.0.1`). All legitimate users
share that bridge, so without this companion key the defaults
rate-limit them as if a single attacker were flooding.

The B29 engine patch (bundled in `webidentity.patch` since
v0.3.15-B-rev5) adds a sphere.ini key `WebIdentityTrustedProxy=`
that whitelists the proxy bridge IP at TCP-accept. The 5 per-IP
counters now behave as follows:

| `sphere.ini` key | Default | When checked | Sees IP | B29 bypass for trusted proxy |
|---|---|---|---|---|
| `MaxPings` | 15 | TCP-accept | bridge | yes |
| `MaxConnectRequestsPerIP` | 50 | TCP-accept | bridge | yes |
| `ClientMaxIP` | 16 | TCP-accept | bridge | yes |
| `ConnectingMax` | 32 | TCP-accept (global) | n/a | yes |
| `ConnectingMaxIp` | 8 | TCP-accept | bridge | yes |

**Apply.** Set the bridge IP in the new key:

```ini
WebIdentityTrustedProxy=172.22.0.1
```

(Replace with your proxy's actual bridge IP. Multiple proxies:
`172.22.0.1,10.0.0.5` — exact-match comma-separated list.)

The other 5 keys can stay at upstream defaults — the bypass is
surgical (proxy bridge only), not global. Non-proxy clients (desktop
UO direct to port 2593) keep the per-IP rate-limit upstream intends.

**Symptom if you skip the bypass key.** A burst of ~5 simultaneous
logins trips `ConnectingMaxIp=8`, the bridge IP gets banned for
`NetTTL` seconds (default 300s), and EVERY user behind the proxy
is rejected with `ERROR: Reject reason: CONNECTINGMAXIP reached 9/8`.
(We tripped this at <share> in the v0.3.15-B-rev3 audit — 12/12
smoke loops failed at run #4 until the bypass key was added in rev5;
empirically reproduced 12/12 IN_GAME afterwards.)

Real per-real-IP enforcement still happens (a) at the proxy layer
(token-bucket rate limit + admin ban list), and (b) post-WebIdentity
via `account.LastIP` / `<SOCKETIP>` in scripts. The proxy IS the
trusted gateway, so per-bridge limits are nonsensical at the engine
level.

### Encrypted shard handling (rev2 — SHIPPED)

Sphere's encryption (`ENC_BTFISH`, `ENC_TFISH`, etc) is a stream
cipher initialised AFTER the seed packet. v0.3.15-B-rev2 (shipped
2026-05-04) handles this by:

- **Proxy side**: when `target.encrypt !== 'none'` the proxy emits
  the 149-byte 0xA4 plaintext frame as the FIRST bytes on the
  upstream socket — BEFORE any wasm client byte. Code path:
  `Session.connectTcp` → `sendWebIdentityIfEncryptedShard()` (called
  inside the TCP `connect` callback).
- **Sphere side**: a new pre-seed branch in
  `src/network/CNetworkInput.cpp` detects `0xA4` + `"CUOWEB"` magic
  on the first incoming bytes and dispatches to a free function
  `TryProcessWebIdentityPreSeed(net, data, len)` (defined alongside
  `PacketSystemInfo::onReceive` in `receive.cpp`). The pre-seed
  branch consumes 149 bytes WITHOUT setting `m_seeded`, so the next
  call to `processUnknownClientData` reads the wasm's encrypted
  seed bytes through the existing seed-handling code unchanged.

Wire ordering on the encrypted upstream socket:

```
[proxy: 149-byte 0xA4 frame plaintext]
[client: 4-byte seed encrypted]
[client: 0xEF login encrypted]
...
```

The patch also updates the existing `INT8_MAX` (127-byte) length cap
in `CNetworkInput.cpp` to exempt 149-byte 0xA4 frames; otherwise the
length cap would reject our frame before the magic could even be
checked.

Plaintext shards (e.g. ModernUO with `encrypt: none`) continue to
use the post-seed `PacketSystemInfo::onReceive` path — their 0xA4
emission stays AFTER the first wasm c2s so ModernUO's TcpServer
peek-validation passes first.

Verified live 2026-05-04 against `example-spheresvr` (Sphere
Source-X master, `encrypt: blowfish_2_0_3`): the
`[WebIdentity] accepted (pre-seed): real=...` log line fires on
every wasm-client connect, and subsequent `Client disconnected.
Account: ... IP=...` log lines show the **real public IP** instead
of `172.22.0.1`.

### Detailed why-each-change (for shard owners adapting to a Sphere fork that drifted from upstream master)

If your fork's source has rearranged enough that
`patches/webidentity.patch` doesn't apply cleanly, here's the
rationale of each hunk so you can re-derive the changes manually:

### Change 1 — declare the new handler

**File:** `src/network/packet.h`

Find the `enum NETWORK_PACKETS` block where each packet ID is listed
(e.g. `CMD_GameLogin = 0x91, CMD_AnsiMessage = 0x1C, …`).

Add `CMD_WebIdentity = 0xA4` next to the other extended packets.

**Why:** Source-X's network dispatcher uses this enum to route an
incoming byte to the right `Packet*::onReceive` method. Adding the
constant is the minimum to make the packet "exist" in the engine's
type system.

### Change 2 — implement the handler

**File:** `src/network/packet.cpp`

Find the registration block where existing `Packet*` instances are
added to the `m_packets[]` array (search for `m_packets[CMD_` to
locate it).

Add a new class `PacketWebIdentity` whose body parses the
fixed-width header + null-terminated strings out of the 148-byte
body:

```cpp
class PacketWebIdentity : public Packet
{
public:
    PacketWebIdentity() : Packet(149) {}    // fixed-length per spec

    bool onReceive(NetState* net) override
    {
        if (getLength() != 149) {
            net->markReadClosed();
            return false;
        }

        const std::string& expectedSecret = g_Cfg.m_sWebIdentitySecret;
        if (expectedSecret.empty()) {
            // Owner enabled the handler but didn't set the secret — fail
            // closed so an attacker can't bypass during empty-secret window.
            net->markReadClosed();
            return false;
        }

        skip(1);                            // packet id (0xA4)

        // client_type[6] — fixed width, ASCII "CUOWEB" (no trailing null).
        char clientType[6];
        for (int i = 0; i < 6; ++i) clientType[i] = (char)getByte();
        if (memcmp(clientType, "CUOWEB", 6) != 0) {
            // Real OSI desktop SystemInfo, not a WebIdentity packet —
            // fall through. Source-X has no original 0xA4 handler so
            // we just consume + ignore quietly.
            return true;
        }

        const byte ver = getByte();
        if (ver > 1) {
            // Forward-compat: log + continue (mirror upstream RunUO-like
            // ref impl behaviour). Newer proxies may extend the body
            // but the parts we read stay backward-compatible.
            g_Log.Event(LOGM_INIT, "[WebIdentity] received v%d, expected v1\n", ver);
        }

        const uint32 ts = getUInt32BE();    // unix seconds
        const time_t now = time(nullptr);
        const int64 skew = (int64)now - (int64)ts;
        if (skew < -10 || skew > 30) {      // upstream uses 30 s
            net->markReadClosed();
            return false;
        }

        std::string secret      = readStrz();
        std::string userId      = readStrz();
        std::string connectIp   = readStrz();
        std::string authProv    = readStrz();
        std::string authUser    = readStrz();
        std::string authId      = readStrz();
        std::string role        = readStrz();

        // Constant-time compare to defend against timing side-channels.
        // Upstream reference impl uses C# string equality (variable-time)
        // — we go stricter on the C++ side.
        if (!constant_time_strequal(secret, expectedSecret)) {
            net->markReadClosed();
            return false;
        }

        CSocketAddress addr;
        if (!addr.SetFromString(connectIp.c_str())) {
            net->markReadClosed();
            return false;
        }
        net->getClient()->m_PeerName = addr;

        // Stash auxiliary fields for Sphere scripts (TAG.* convention).
        CClient* cli = net->getClient();
        cli->m_TagDefs.SetStr("WEBIDENT_USER_ID",  true, userId.c_str());
        cli->m_TagDefs.SetStr("WEBIDENT_ROLE",     true, role.c_str());
        cli->m_TagDefs.SetStr("WEBIDENT_AUTHPROV", true, authProv.c_str());
        cli->m_TagDefs.SetStr("WEBIDENT_AUTHID",   true, authId.c_str());

        g_Log.Event(LOGM_INIT,
            "[WebIdentity] accepted: bridge=%s real=%s userId=%s role=%s\n",
            net->getPeerName().GetAddrStr(), addr.GetAddrStr(),
            userId.c_str(), role.c_str());

        return true;
    }
};
```

Helpers (in the same file or a shared header):

```cpp
// Linear strz reader for a Packet body. Walks until 0x00 inclusive.
static inline std::string readStrz(Packet* p)
{
    std::string s;
    while (true) {
        byte b = p->getByte();
        if (b == 0) break;
        s.push_back((char)b);
        if (s.size() > 256) break;     // safety cap
    }
    return s;
}

// Constant-time string compare. Defends against timing oracles on the
// secret. ::memcmp early-exits on first mismatch, which leaks secret
// bytes to a careful attacker.
static inline bool constant_time_strequal(const std::string& a, const std::string& b)
{
    if (a.size() != b.size()) return false;
    unsigned char d = 0;
    for (size_t i = 0; i < a.size(); ++i) d |= (unsigned char)(a[i] ^ b[i]);
    return d == 0;
}
```

Register the new class via:
```cpp
m_packets[CMD_WebIdentity] = new PacketWebIdentity();
```

**Why each piece**:
- `Packet(149)` declares a fixed-length 149-byte handler.
- `markReadClosed()` is Sphere's "drop this connection" verb. Critical
  to use it (not `return false` alone) on every failure path.
- The 6-byte `client_type` mismatch returns `true` (consumed) instead
  of dropping — matches the upstream RunUO-like impl's "fall through
  to original SystemInfo handler" behaviour. Source-X has no
  original 0xA4 handler, so consuming + ignoring is fine.
- `constant_time_strequal` defends against timing side-channels.
- `m_PeerName` is the field every Sphere subsystem reads when it
  needs "the IP of this connection".
- The clock-skew check (`-10 ≤ skew ≤ 30`) matches the upstream
  reference impl's 30-second freshness window.

### Change 3 — config the secret

**File:** `src/sphere/CResource.h`

Add:
```cpp
std::string m_sWebIdentitySecret;
```

**File:** `src/sphere/CResource.cpp`

Find the `[OPTIONS]` section parser. Add an entry that reads
`WebIdentitySecret` from `sphere.ini` into
`g_Cfg.m_sWebIdentitySecret`. ~5 lines mirroring any other
string-typed option.

**`sphere.ini`** then sets:
```ini
[OPTIONS]
WebIdentitySecret=<32+ ASCII chars, SAME as proxy YAML's `secret`>
```

### Optional but recommended — skip 0xA4 on direct connections

Players using a desktop UO client connect to your shard directly,
not through the web proxy. They never send a 0xA4. If your
`sphere.ini` has `WebIdentitySecret` set but a desktop client
connects, the handler isn't invoked at all — no special case needed.

### Building

```bash
cd Source-X
cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -S . -B build
ninja -C build
```

Standard Source-X build flow. No new dependencies.

### (US) Applied to example-spheresvr via Dockerfile

SHIPPED v0.3.15-B-rev3 (B28) 2026-05-04. Our spheresvr deploy uses
`patches/webidentity.patch` (vendored at
`docs/sphere-source-x-adapted/patches/`) applied during the build:

```dockerfile
COPY patches/webidentity.patch /tmp/webidentity.patch
RUN echo "Before patch 3: ..." && patch -p1 < /tmp/webidentity.patch \
 && echo "After patch 3: ..." && rm /tmp/webidentity.patch
# No build-time secret arg — secret lives in sphere.ini at runtime.
RUN cmake -G Ninja -DCMAKE_CXX_FLAGS="-fpch-preprocess" ...
```

The secret lives in our bind-mounted `sphere.ini` (`WebIdentitySecret=`
in `[OPTIONS]`) and is read at runtime by the patched
`GetWebIdentitySecret()`. Operator rotates by editing `sphere.ini`
+ typing `[r` in the Sphere console — no rebuild, no container
restart.

The proxy emits the 0xA4 frame for spheresvr in the `connectTcp`
callback (BEFORE any wasm client byte) because spheresvr uses
`encrypt: blowfish_2_0_3`. Sphere's pre-seed handler reads it
plaintext before the cipher initialises. Full live-validation log
on every wasm-client connect:
```
[WebIdentity] accepted (pre-seed): real=203.0.113.5 userId=... role=user
Client disconnected. Account: 'youraccount'. IP='203.0.113.5'.
```

## Security considerations

### The secret is sent in the clear

Deliberate trade-off in the upstream WebIdentity spec, not a flaw we
can fix without diverging from play.classicuo.org compatibility.
Mitigations the operator owns:

- **Keep proxy↔shard on a private network.** Same Docker host,
  internal LAN, VPN, or a WireGuard tunnel. The 0xA4 frame should
  never traverse a network where an attacker can sniff bytes.
- **Don't log packet contents.** The handler logs the parsed *fields*
  (user_id, role) but never the raw secret. Match that discipline if
  you add new logging.
- **Rotate after suspected leak.** Update `sphere.ini` (Sphere) /
  `modernuo.json` (ModernUO) / proxy YAML simultaneously and restart
  both processes (mismatched secrets = every WebIdentity packet
  rejected = no one logs in).

### Per-shard secrets are isolated

Each shard's `secret` is independent. If shard A's secret leaks,
shard B is unaffected.

### The proxy is trusted by design

This scheme assumes the proxy is authoritative on the real client IP.
Behind Cloudflare we read `CF-Connecting-IP` (validated by CF before
it hits us). Behind nginx we use `X-Forwarded-For` with a
`TRUST_PROXY_HOPS` count. If the proxy is misconfigured and trusts
attacker-supplied XFF, the attacker can spoof the IP all the way to
the shard. Mitigation: keep `TRUST_PROXY_HOPS` accurate to the actual
infrastructure.

### Replay window

The 30-second freshness check stops replays past 30 seconds. Within
the window the packet IS replayable from a captured sample. Combined
with private proxy↔shard transport this is acceptable — the same
person sending the same IP twice in 30 seconds is not an attack,
it's a reconnect.

### Reflection-based IP override (ModernUO/ServUO/RunUO)

The upstream reference impl reaches into
`NetState.<Address>k__BackingField` via reflection. This is a
compiler-generated name for an auto-property's private backing
field. Stable across C# versions but **not guaranteed**: if upstream
ServUO/ModernUO ever convert `Address` to a manually-coded property
with a different backing-field name, the reflection silently
no-ops and IP override stops working without raising errors.
Smoke-test after every ServUO/ModernUO upgrade.

### Why no HMAC, why plaintext secret

Upstream chose plaintext-secret + timestamp instead of HMAC. We could
overlay an HMAC on top (HMAC of the body keyed on a separate secret)
but diverging from the upstream spec means shards already integrated
for play.classicuo.org wouldn't accept our packets. We mirror
upstream verbatim and rely on the private transport assumption.

## Testing your integration

### Smoke procedure

1. Set the same `secret` on proxy + shard.
2. Set `enabled: true` on the proxy YAML.
3. Drop the upstream files into your shard's scripts folder
   (ModernUO/ServUO/RunUO) or apply the Sphere patch + rebuild
   (Sphere).
4. Restart shard and proxy.
5. Connect via the wasm client.
6. Look for the `WebIdentity accepted` log line on the shard.
   `real=…` should match your real public IP, not `172.x.x.x`.
7. Check whatever per-IP feature you cared about (account.LastIP /
   audit logs) — should report your real IP now.

### Failure modes to test

- **Wrong secret on shard** → connections refused at handler. Shard
  log shows `Incorrect secret` (ModernUO/ServUO) or your Sphere
  patch's reject log. Proxy log shows the upstream FIN'd with
  `c2sBytes>0` (we sent the 149-byte 0xA4 frame). The v0.3.14
  shard-cooldown detector treats this as an application close, not
  a per-IP block.
- **Empty secret on shard** → first connection's `WebIdentitySecret
  == ""` compare fails → kick. Set the secret, restart, retry.
- **Clock drift > 30 s** → all connections fail freshness check.
  Sync NTP on both hosts.
- **Mixed mode (proxy enabled, shard not patched)** → ModernUO/ServUO
  fall through to the original SystemInfo handler (which logs +
  ignores). Sphere has no original 0xA4 handler and Source-X just
  drops unknown packet IDs. Either way, real IP NOT forwarded but no
  breakage. This is the path most shard owners are on for v0.3.15-A's
  first ship.
- **Mixed mode (proxy disabled, shard patched)** → shard never sees a
  0xA4. Falls back to docker-bridge IP. No breakage.
- **Player using desktop UO client** → never sends 0xA4 at all (the
  CUO Web header). For ServUO/ModernUO/RunUO, the wrong-`client_type`
  fall-through means the original OSI SystemInfo handler runs as
  upstream intended. For Sphere, no SystemInfo handler exists; we
  consume + ignore.

## Open questions / future work

- **IPv6 client IPs end-to-end.** The packet's `connecting_ip` is a
  string and naturally handles v6. Sphere `m_PeerName` and ModernUO
  `IPAddress` already handle v6. Should "just work" but hasn't been
  smoke-tested through the full chain yet.
- **Watch upstream packets repo for spec bumps.**
  <https://github.com/ClassicUO/packets> is the source of truth. If
  they revise the .ksy or the reference impl, follow within reason.
- **Discord-role-driven shard admin.** The `role` field surfaces
  whether the proxy treats the connecting user as `admin` or
  `shard-owner`. ServUO/ModernUO emit the
  `OnWebIdentityReceived` event so content scripts can subscribe and
  auto-grant in-game admin handles based on role + Discord ID. Our
  example-modernuo could wire this up after v0.3.15 ships — out
  of scope for this initial ship but a clean follow-up.
- **PROXY protocol v2 alternative.** Some shards' upstream stacks
  speak HAProxy v2 PROXY protocol natively. For those, a parallel
  ship path is "set `proxyProtocol: true` on the YAML" and emit a
  v2 header instead of 0xA4. Not pursued unless a shard owner
  asks for it.
