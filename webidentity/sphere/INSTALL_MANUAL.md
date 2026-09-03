# Manual install of the WebIdentity patch on Sphere Source-X

Manual for shards that **don't use our Dockerfile**. If your shard already
clones Source-X by hand or builds it from Visual Studio / your own script,
follow this document — you'll copy and paste code chunks into **5 source
files**.

> **What does this get me?** When a player connects through ClassicUO Web
> (a WebAssembly client behind a proxy), your Sphere records the player's
> **real** IP instead of the proxy's IP. Without this patch, every web
> player shows up under the same IP in `[information`, in
> `MaxConnectRequestsPerIP`, in `account.LastIP`, etc., because they all
> share the proxy's single outbound TCP connection.

> **B28 (improvement 2026-05-04)**: the shared secret is **read from
> `sphere.ini`** (key `WebIdentitySecret=`). You no longer need to
> recompile to rotate it: edit `sphere.ini` + type `[r` in the Sphere
> console and the rotation is instant. The compile-time
> `WEBIDENTITY_SECRET` macro still works as a **fallback** for sealed
> Docker builds that prefer to bake the secret in.

---

## When do you NOT need this patch?

- If your shard is NOT behind a proxy → not needed.
- If your shard only allows the classic desktop UO client → not needed.

The patch is **harmless when `WebIdentitySecret=` is empty in your
sphere.ini AND the `WEBIDENTITY_SECRET` macro is not defined at compile
time**. With both empty, upstream behaviour (the classic `skip(148)`) is
left intact. Applying it "just in case" breaks nothing.

---

## 1. Before you start

You need:

1. **The Sphere Source-X source code**. If you don't have it:
   ```
   git clone https://github.com/Sphereserver/Source-X
   ```

   **Upstream compatibility (v0.3.20):** this patch is tested against
   Source-X `main` periodically, but the anchors `apply_webidentity.py`
   searches for (the `RC_W*` enums, the keyword strings, the
   `IpHistoryManager::CheckPing` signature) can move at any time. If the
   script fails with "file X doesn't contain the expected anchor", upstream
   most likely reordered the section. Workaround: go back to a known-good
   commit:
   ```
   cd Source-X
   git log --oneline | head -10           # list recent commits
   git checkout <known-good-hash>          # the last one that applied cleanly for your deploy
   ```
   Until a stable canonical commit is documented, operators report failures
   in `docs/shared/WEBIDENTITY.md` § Upstream drift so the anchors can be rebuilt.

2. **A text editor that respects UTF-8 and Unix line endings**
   (Notepad++, VSCode, vim, kate). **Avoid Windows Notepad**.

3. **A 24-byte hex secret**. Generate one like this:
   ```
   openssl rand -hex 24
   ```
   Example: `<YOUR_48_HEX_WEBIDENTITY_SECRET>`. Store it somewhere safe —
   if it leaks, anyone can forge real IPs on your shard.

---

## 2. The files you'll touch

**Six** files:

| File | What changes |
|---|---|
| `src/network/CNetState.h` | +1 line: `setPeerAddress` setter |
| `src/game/CServerConfig.h` | +2 lines: `m_sWebIdentitySecret` (B28) + `m_sWebIdentityTrustedProxy` (B29) fields |
| `src/game/CServerConfig.cpp` | +4 entries: 2 RC enum + 2 keyword table (to parse `WebIdentitySecret=` and `WebIdentityTrustedProxy=` from sphere.ini) |
| `src/network/receive.cpp` | Replaces `PacketSystemInfo::onReceive` and adds helpers (`GetWebIdentitySecret`, `TryProcessWebIdentityPreSeed`) |
| `src/network/CNetworkInput.cpp` | +3 blocks: B17 pre-auth memcpy hardening + 0xA4 detection + preamble dispatch |
| `src/network/CNetworkManager.cpp` | +2 blocks (B29): parse `WebIdentityTrustedProxy=` + bypass the 5 per-IP counters when the peer is a trusted proxy |

Make a **backup** of all six files before you start.

---

## 3. Step 1 — `src/network/CNetState.h`

Find:

```cpp
    CClient* getClient(void) const { return m_client; } // get linked client
```

Right **after** it, add:

```cpp
    void setPeerAddress(const CSocketAddress& addr) { m_peerAddress = addr; } // B28 WebIdentity override
```

Save. Done.

---

## 4. Step 2 — `src/game/CServerConfig.h`

Find this block in the `// network settings` section:

```cpp
    int  _iMaxConnectRequestsPerIP; // Maximum number of connection requests before rejecting/blocking IP.
    int64 _iTimeoutIncompleteConnectionMs; // Maximum time in milliseconds to wait before closing a connection request wich did not make it into a successful login
	int	 m_iNetMaxQueueSize;        // max packets to hold per queue (comment out for unlimited)
```

And add TWO new lines between `_iTimeoutIncompleteConnectionMs` and
`m_iNetMaxQueueSize`:

```cpp
    int  _iMaxConnectRequestsPerIP; // Maximum number of connection requests before rejecting/blocking IP.
    int64 _iTimeoutIncompleteConnectionMs; // Maximum time in milliseconds to wait before closing a connection request wich did not make it into a successful login
	CSString m_sWebIdentitySecret;  // B28: pre-shared secret for WebIdentity 0xA4 intercept. Empty = disabled. Live-reloadable via [r]. WEBIDENTITY_SECRET macro is fallback if empty.
	CSString m_sWebIdentityTrustedProxy; // B29: comma-separated list of proxy IPs allowed to bypass the per-IP rate-limit checks at TCP-accept. WebIdentity rewrites m_peerAddress only post-0xA4, so without this every web user counts against the proxy bridge IP. Empty = disabled (legacy behaviour). Live-reloadable via [r].
	int	 m_iNetMaxQueueSize;        // max packets to hold per queue (comment out for unlimited)
```

---

## 5. Step 3 — `src/game/CServerConfig.cpp` (2 sub-steps)

### 5.a — Add an entry to the `RC_*` enum

Find this block (alphabetically between the `WALK*` and `WOOL*` entries):

```cpp
	RC_WALKBUFFER,
	RC_WALKREGEN,
	RC_WOOLGROWTHTIME,			// m_iWoolGrowthTime
```

Insert TWO new lines between `RC_WALKREGEN` and `RC_WOOLGROWTHTIME`:

```cpp
	RC_WALKBUFFER,
	RC_WALKREGEN,
	RC_WEBIDENTITYSECRET,		// m_sWebIdentitySecret (B28)
	RC_WEBIDENTITYTRUSTEDPROXY,	// m_sWebIdentityTrustedProxy (B29)
	RC_WOOLGROWTHTIME,			// m_iWoolGrowthTime
```

### 5.b — Add an entry to the keyword table

In the same file, find the table with the strings `"WALKBUFFER"`,
`"WALKREGEN"`, `"WOOLGROWTHTIME"` (usually about 300 lines below the
enum):

```cpp
	{ "WALKBUFFER",				{ ELEM_INT,		static_cast<uint>OFFSETOF(CServerConfig,m_iWalkBuffer)			}},
	{ "WALKREGEN",				{ ELEM_INT,		static_cast<uint>OFFSETOF(CServerConfig,m_iWalkRegen)			}},
	{ "WOOLGROWTHTIME",			{ ELEM_INT,		static_cast<uint>OFFSETOF(CServerConfig,m_iWoolGrowthTime)		}},
```

Insert TWO new lines between `WALKREGEN` and `WOOLGROWTHTIME`:

```cpp
	{ "WALKBUFFER",				{ ELEM_INT,		static_cast<uint>OFFSETOF(CServerConfig,m_iWalkBuffer)			}},
	{ "WALKREGEN",				{ ELEM_INT,		static_cast<uint>OFFSETOF(CServerConfig,m_iWalkRegen)			}},
	{ "WEBIDENTITYSECRET",		{ ELEM_CSTRING,	static_cast<uint>OFFSETOF(CServerConfig,m_sWebIdentitySecret)	}}, // B28
	{ "WEBIDENTITYTRUSTEDPROXY",{ ELEM_CSTRING,	static_cast<uint>OFFSETOF(CServerConfig,m_sWebIdentityTrustedProxy)	}}, // B29
	{ "WOOLGROWTHTIME",			{ ELEM_INT,		static_cast<uint>OFFSETOF(CServerConfig,m_iWoolGrowthTime)		}},
```

> **Why two places?** Sphere keeps an enum of internal IDs (`RC_*`) in
> parallel with a keyword table that maps ini strings to struct fields.
> The table is alphabetically sorted and searched with binary search —
> that's why the order matters.

---

## 6. Step 4 — `src/network/receive.cpp` (helper + onReceive + pre-seed helper)

Find the `PacketSystemInfo::onReceive` function. Upstream version:

```cpp
bool PacketSystemInfo::onReceive(CNetState* net)
{
	ADDTOCALLSTACK("PacketSystemInfo::onReceive");
	UnreferencedParameter(net);

	skip(148);
	return true;
}
```

**Replace that block with this entire chunk** (it includes the helper
reader, the new onReceive, and the pre-seed helper):

```cpp
// B28: read WebIdentity secret from sphere.ini first, then fall back to
// the WEBIDENTITY_SECRET preprocessor macro if the ini value is empty.
// The ini route enables hot-rotation via `[r]` resync (no recompile).
// Both routes return "" if neither is configured -> intercept disabled.
static const char* GetWebIdentitySecret() noexcept
{
	const char* iniValue = g_Cfg.m_sWebIdentitySecret.GetBuffer();
	if (iniValue && iniValue[0] != '\0')
		return iniValue;
#ifdef WEBIDENTITY_SECRET
	return WEBIDENTITY_SECRET;
#else
	return "";
#endif
}

bool PacketSystemInfo::onReceive(CNetState* net)
{
	ADDTOCALLSTACK("PacketSystemInfo::onReceive");

	// WebIdentity 0xA4 intercept (post-seed path). Secret comes from
	// sphere.ini `WebIdentitySecret=` (preferred) or compile-time
	// WEBIDENTITY_SECRET macro (fallback). Empty/short -> disabled.
	const char* const kWebIdentitySecret    = GetWebIdentitySecret();
	const size_t      kWebIdentitySecretLen = strlen(kWebIdentitySecret);

	if (kWebIdentitySecretLen >= 8) {
		char clientType[7] = {0};
		readStringASCII(clientType, 6, false);

		if (memcmp(clientType, "CUOWEB", 6) == 0) {
			(void)readByte();
			(void)readInt32();

			char secret[128]  = {0}; readStringNullASCII(secret,   sizeof(secret));
			char userId[64]   = {0}; readStringNullASCII(userId,   sizeof(userId));
			char realIp[64]   = {0}; readStringNullASCII(realIp,   sizeof(realIp));
			char authProv[64] = {0}; readStringNullASCII(authProv, sizeof(authProv));
			char authUser[64] = {0}; readStringNullASCII(authUser, sizeof(authUser));
			char authId[64]   = {0}; readStringNullASCII(authId,   sizeof(authId));
			char role[32]     = {0}; readStringNullASCII(role,     sizeof(role));

			const uint remaining = getRemainingLength();
			if (remaining > 0) skip(remaining);

			const size_t suppliedLen = strlen(secret);
			if (suppliedLen != kWebIdentitySecretLen) {
				g_Log.Event(LOGM_INIT | LOGL_WARN,
					"[WebIdentity] reject: secret length mismatch (got %zu, expected %zu) userId=%s\n",
					suppliedLen, kWebIdentitySecretLen, userId);
				net->markReadClosed();
				return false;
			}
			unsigned char acc = 0;
			for (size_t i = 0; i < kWebIdentitySecretLen; ++i)
				acc |= (unsigned char)(secret[i] ^ kWebIdentitySecret[i]);
			if (acc != 0) {
				g_Log.Event(LOGM_INIT | LOGL_WARN,
					"[WebIdentity] reject: secret mismatch userId=%s ip=%s\n",
					userId, realIp);
				net->markReadClosed();
				return false;
			}

			CSocketAddress addr;
			addr.SetAddrStr(realIp);
			net->setPeerAddress(addr);

			// v0.3.25 — re-check per-IP enforcement against the REAL IP
			// now that m_peerAddress is rewritten. If the GM did
			// `[BLOCKIP <real-ip>]` or the player exceeds MaxPings, kick.
			HistoryIP& realHist = g_NetworkManager.getIPHistoryManager().getHistoryForIP(realIp);
			if (realHist.checkPing()) {
				g_Log.Event(LOGM_INIT|LOGL_WARN,
					"[WebIdentity] post-rewrite reject: real IP %s blocked or MaxPings exceeded "
					"(blocked=%d pings=%d) userId=%s\n",
					realIp, (int)realHist.m_fBlocked, realHist.m_iPings, userId);
				net->markReadClosed();
				return false;
			}

			g_Log.Event(LOGM_INIT,
				"[WebIdentity] accepted: real=%s userId=%s role=%s authProv=%s authUser=%s authId=%s\n",
				realIp, userId, role, authProv, authUser, authId);
			return true;
		}

		skip(148 - 6);
	} else {
		skip(148);
	}
	return true;
}


// WebIdentity helper for the pre-seed path (called from CNetworkInput.cpp).
bool TryProcessWebIdentityPreSeed(CNetState* net, const byte* data, uint dataLen)
{
	if (dataLen < 149) return false;
	if (data[0] != XCMD_Spy) return false;
	if (memcmp(data + 1, "CUOWEB", 6) != 0) return false;

	const char* const kWebIdentitySecret    = GetWebIdentitySecret();
	const size_t      kWebIdentitySecretLen = strlen(kWebIdentitySecret);
	if (kWebIdentitySecretLen < 8) return false;

	(void)data[7];
	const byte* p   = data + 12;
	const byte* end = data + 149;

	auto readStrz = [&](char* out, size_t cap) -> bool {
		size_t i = 0;
		while (p < end && *p != 0 && i + 1 < cap) { out[i++] = (char)*p++; }
		if (p >= end) return false;
		out[i] = 0;
		++p;
		return true;
	};

	char secret[128]  = {0}; if (!readStrz(secret,   sizeof(secret)))   return false;
	char userId[64]   = {0}; if (!readStrz(userId,   sizeof(userId)))   return false;
	char realIp[64]   = {0}; if (!readStrz(realIp,   sizeof(realIp)))   return false;
	char authProv[64] = {0}; if (!readStrz(authProv, sizeof(authProv))) return false;
	char authUser[64] = {0}; if (!readStrz(authUser, sizeof(authUser))) return false;
	char authId[64]   = {0}; if (!readStrz(authId,   sizeof(authId)))   return false;
	char role[32]     = {0}; if (!readStrz(role,     sizeof(role)))     return false;

	const size_t suppliedLen = strlen(secret);
	if (suppliedLen != kWebIdentitySecretLen) {
		g_Log.Event(LOGM_INIT | LOGL_WARN,
			"[WebIdentity] reject (pre-seed): secret length mismatch (got %zu, expected %zu) userId=%s\n",
			suppliedLen, kWebIdentitySecretLen, userId);
		net->markReadClosed();
		return true;
	}
	unsigned char acc = 0;
	for (size_t i = 0; i < kWebIdentitySecretLen; ++i)
		acc |= (unsigned char)(secret[i] ^ kWebIdentitySecret[i]);
	if (acc != 0) {
		g_Log.Event(LOGM_INIT | LOGL_WARN,
			"[WebIdentity] reject (pre-seed): secret mismatch userId=%s ip=%s\n",
			userId, realIp);
		net->markReadClosed();
		return true;
	}

	CSocketAddress addr;
	addr.SetAddrStr(realIp);
	net->setPeerAddress(addr);

	// v0.3.25 — re-check per-IP enforcement against the just-rewritten
	// real IP (same reason as in PacketSystemInfo above).
	HistoryIP& realHist = g_NetworkManager.getIPHistoryManager().getHistoryForIP(realIp);
	if (realHist.checkPing()) {
		g_Log.Event(LOGM_INIT|LOGL_WARN,
			"[WebIdentity] post-rewrite reject (pre-seed): real IP %s blocked or MaxPings exceeded "
			"(blocked=%d pings=%d) userId=%s\n",
			realIp, (int)realHist.m_fBlocked, realHist.m_iPings, userId);
		net->markReadClosed();
		return true;
	}

	g_Log.Event(LOGM_INIT,
		"[WebIdentity] accepted (pre-seed): real=%s userId=%s role=%s authProv=%s authUser=%s authId=%s\n",
		realIp, userId, role, authProv, authUser, authId);
	return true;
}
```

Save.

---

## 7. Step 5 — `src/network/CNetworkInput.cpp` (pre-seed dispatch)

Same change in two spots. **Change 1**: add `fWebIdentity` and the
detection. Find:

```cpp
    bool fHTTPReq = false;
    const uint uiOrigRemainingLength = buffer->getRemainingLength();
    const byte* const pOrigRemainingData = buffer->getRemainingData();
    if (state->m_seeded == false)
    {
        fHTTPReq = (uiOrigRemainingLength >= 5 && memcmp(pOrigRemainingData, "GET /", 5) == 0) ||
            (uiOrigRemainingLength >= 6 && memcmp(pOrigRemainingData, "POST /", 6) == 0);
    }
    if (!fHTTPReq && (uiOrigRemainingLength > INT8_MAX))
```

Replace with:

```cpp
    bool fHTTPReq = false;
    bool fWebIdentity = false; // B28: WebIdentity 0xA4 plaintext preamble
    const uint uiOrigRemainingLength = buffer->getRemainingLength();
    const byte* const pOrigRemainingData = buffer->getRemainingData();
    if (state->m_seeded == false)
    {
        fHTTPReq = (uiOrigRemainingLength >= 5 && memcmp(pOrigRemainingData, "GET /", 5) == 0) ||
            (uiOrigRemainingLength >= 6 && memcmp(pOrigRemainingData, "POST /", 6) == 0);

        // B28: detect WebIdentity 0xA4 preamble before INT8_MAX cap (149 > 127).
        fWebIdentity = (uiOrigRemainingLength >= 149
                        && pOrigRemainingData[0] == XCMD_Spy
                        && memcmp(pOrigRemainingData + 1, "CUOWEB", 6) == 0);
    }
    if (!fHTTPReq && !fWebIdentity && (uiOrigRemainingLength > INT8_MAX))
```

**Change 2**: insert the `else if (fWebIdentity)` block right before the
`// check for new seed` comment:

Find:

```cpp
        // check for new seed (sometimes it's received on its own)
        else if (uiOrigRemainingLength == 1 && pOrigRemainingData[0] == XCMD_NewSeed)
```

Replace with:

```cpp
        // B28: WebIdentity 0xA4 plaintext preamble - process before any
        // seed/cipher logic so encrypted shards still receive real-IP overrides.
        else if (fWebIdentity)
        {
            EXC_SET_BLOCK("webidentity preamble");
            extern bool TryProcessWebIdentityPreSeed(CNetState* net, const byte* data, uint dataLen);
            (void)TryProcessWebIdentityPreSeed(state, pOrigRemainingData, uiOrigRemainingLength);
            buffer->skip(149);
            return true;
        }

        // check for new seed (sometimes it's received on its own)
        else if (uiOrigRemainingLength == 1 && pOrigRemainingData[0] == XCMD_NewSeed)
```

Save. Done with CNetworkInput.cpp.

---

## 7.5. Step 5.5 — `src/network/CNetworkManager.cpp` (B29 + v0.3.25)

This step patches the engine so the **proxy whitelist** declared in
`sphere.ini` (key `WebIdentityTrustedProxy=`, see Step 6.5) **skips ALL
per-IP checks** evaluated at TCP `accept()` — including `ip.checkPing()`
(which covers `m_fBlocked` and `MaxPings`). Without this, every web user
counts against the docker bridge IP; within seconds the bridge exceeds
`MaxPings`, goes into `m_fBlocked=1` with TTL=300s, and all web traffic is
rejected for 5 minutes.

**v0.3.25**: the previous B29 rev left `ip.checkPing()` outside the
trusted-proxy gate (intent: "operator can ban a compromised proxy"). In
practice that check tripped on MaxPings and auto-banned the bridge —
self-DOS, not real enforcement. Now the whole `if` is wrapped in
`(!fTrustedProxy && ...)`. The ability to ban a specific player's real IP
is **not lost** because it is reintroduced in `receive.cpp` post
WebIdentity-rewrite (Step 4); see `README.md` for the full rationale.

Inside `CNetworkManager::acceptNewConnection()` find the
`EXC_SET_BLOCK("ip history");` block (usually around line 133):

```cpp
    // check ip history
    EXC_SET_BLOCK("ip history");

    DEBUGNETWORK(("Retrieving IP history for '%s'.\n", client_addr.GetAddrStr()));
    const int maxIp = g_Cfg.m_iConnectingMaxIP;
    const int climaxIp = g_Cfg.m_iClientsMaxIP;
```

Insert, **between `EXC_SET_BLOCK("ip history");` and the
`DEBUGNETWORK("Retrieving...` line**, the following block:

```cpp
    // B29: WebIdentity trusted-proxy bypass. WebIdentity rewrites
    // m_peerAddress only AFTER the 0xA4 frame is read (post-byte-read),
    // but the rate-limit checks below run pre-byte-read at TCP-accept,
    // so without this every web user counts against the proxy bridge IP.
    // When sphere.ini's `WebIdentityTrustedProxy=` (comma-separated list,
    // exact match) contains the accepting peer's IP, skip the per-IP
    // rate-limit checks. Real per-IP enforcement still happens at:
    //   (a) the proxy layer (token-bucket rate limit + admin ban list)
    //   (b) post-0xA4 via account.LastIP / <SOCKETIP> in scripts (uses
    //       the rewritten real IP, not the bridge).
    // Empty list = disabled (legacy behaviour for non-proxy deployments).
    bool fTrustedProxy = false;
    {
        const char* const trustedList = g_Cfg.m_sWebIdentityTrustedProxy.GetBuffer();
        const char* const peerStr = client_addr.GetAddrStr();
        if (trustedList && trustedList[0] != '\0' && peerStr && peerStr[0] != '\0')
        {
            const size_t peerLen = strlen(peerStr);
            const char* p = trustedList;
            while (*p && !fTrustedProxy)
            {
                while (*p == ' ' || *p == '\t') ++p;
                const char* start = p;
                while (*p && *p != ',') ++p;
                const char* end = p;
                while (end > start && (end[-1] == ' ' || end[-1] == '\t')) --end;
                if ((size_t)(end - start) == peerLen && memcmp(start, peerStr, peerLen) == 0)
                    fTrustedProxy = true;
                if (*p == ',') ++p;
            }
        }
    }
```

**Second change** in the same file: about 30 lines below there's the `if`
that applies the 4 rate-limit checks. Upstream version:

```cpp
    // check if ip is allowed to connect
    if (ip.checkPing() ||
        (maxIp > 0 && ip.m_iPendingConnectionRequests > maxIp) ||
        (climaxIp > 0 && ip.m_iAliveSuccessfulConnections > climaxIp) ||
        (g_Cfg._iMaxConnectRequestsPerIP > 0) && (ip.m_iConnectionRequests >= g_Cfg._iMaxConnectRequestsPerIP))
    {
```

Change to:

```cpp
    // check if ip is allowed to connect (B29: trusted proxies bypass these)
    if (!fTrustedProxy &&
        (ip.checkPing() ||
        (maxIp > 0 && ip.m_iPendingConnectionRequests > maxIp) ||
        (climaxIp > 0 && ip.m_iAliveSuccessfulConnections > climaxIp) ||
        ((g_Cfg._iMaxConnectRequestsPerIP > 0) && (ip.m_iConnectionRequests >= g_Cfg._iMaxConnectRequestsPerIP))))
    {
```

(Note: add a `(` after `&&`, wrap the last `&&` in an extra set of
parentheses, and close with `)))`. The compiler warns you if you get the
parentheses wrong.)

Save. Done with the source files.

---

## 8. Step 6 — Add the secret to `sphere.ini`

Edit your `sphere.ini` and add it in the network settings section (or
wherever you like, order doesn't matter):

```ini
// Pre-shared secret used by the WebIdentity 0xA4 intercept to validate
// real-IP overrides from a trusted upstream proxy. Empty = disabled.
// Min 8 chars when set; 48-char hex (24 bytes) recommended:
//   openssl rand -hex 24
WebIdentitySecret=<YOUR_48_HEX_WEBIDENTITY_SECRET>
```

> **Hot rotation**: when you want to change the secret, edit `sphere.ini`
> and type `[r` in the running Sphere console. Sphere re-reads the ini,
> the next web login uses the new secret. No recompile, no restart.

---

## 8.5. Step 6.5 — Configure `WebIdentityTrustedProxy=`

> **CRITICAL. If you skip this step, logins fail in bursts and your proxy
> gets banned for 5 min.** Learned in production 2026-05-04 — 12/12
> consecutive smokes failed from the 4th onward with
> `CONNECTINGMAXIP reached 9/8` until this key was added; after applying
> it: 12/12 IN_GAME.

**Why.** WebIdentity rewrites `m_peerAddress` **after** parsing the 0xA4
frame. Sphere has 5 per-IP connection counters, all evaluated at the
moment of TCP `accept()` — before a single byte is read from the socket.
They always see the docker bridge IP (e.g. `172.22.0.1`). Because ALL web
users go through that bridge, the defaults rate-limit them as if a single
attacker were flooding.

**The B29 patch (included in `webidentity.patch` since v0.3.15-B-rev5)
adds a surgical bypass**. A new key `WebIdentityTrustedProxy=` lists IPs
(comma-separated, exact match) that the engine skips from the 5
`accept()`-time checks. For all other clients (desktop UO connecting
directly to port 2593) rate-limiting works exactly like upstream.

| `sphere.ini` key | Upstream default | When checked | Sees which IP | B29 bypass if IP is listed |
|---|---|---|---|---|
| `MaxPings` | 15 | TCP-accept | bridge | yes |
| `MaxConnectRequestsPerIP` | 50 | TCP-accept | bridge | yes |
| `ClientMaxIP` | 16 | TCP-accept | bridge | yes |
| `ConnectingMax` | 32 | TCP-accept (global) | n/a | yes |
| `ConnectingMaxIp` | 8 | TCP-accept | bridge | yes |

Edit your `sphere.ini` and add it next to `WebIdentitySecret=`:

```ini
// Companion to WebIdentitySecret. Comma-separated list of proxy IPs
// allowed to bypass the per-IP rate-limit checks at TCP-accept.
// Empty = disabled (legacy behaviour). Live-reloadable via [r].
WebIdentityTrustedProxy=172.22.0.1
```

(Replace `172.22.0.1` with your proxy's actual docker bridge IP. Multiple
proxies: `172.22.0.1,10.0.0.5`.)

**The 5 counters above can stay at their upstream defaults** — the bypass
is surgical (proxy bridge only), not global.

> **Hot-reload OK**: both `WebIdentitySecret` and `WebIdentityTrustedProxy`
> are re-read with `[r` (the patch wires them into the keyword table). You
> only need a single restart the first time you apply the patch.

The real per-real-IP anti-DoS protection stays intact:
- At the proxy: token-bucket per IP/Discord ID + admin ban list (audit
  `WS-C1`, `H2`, `H3` in `server/src/`).
- At the shard, post-0xA4: `account.LastIP`, `<SOCKETIP>` in scripts —
  they see the real IP rewritten by WebIdentity.

The bridge IP is by definition the trusted gateway.

---

## 9. Step 7 — Build Sphere

**No special flags needed.** The patch compiles as a no-op while
`WebIdentitySecret=` is empty in sphere.ini.

```bash
# Linux / macOS / WSL:
cmake -G Ninja -DCMAKE_BUILD_TYPE=Nightly \
  -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/Linux-GNU-x86_64.cmake \
  -DCMAKE_C_FLAGS=-fpch-preprocess -DCMAKE_CXX_FLAGS=-fpch-preprocess \
  -S . -B build
ninja -C build
```

```
# Visual Studio (Windows): Build -> Rebuild Solution
```

### Optional: bake the secret into the binary (sealed Docker mode)

If you prefer NOT to depend on sphere.ini and instead embed the secret
inside the binary (typical for Docker images shipped pre-compiled), add
the compile flag:

```bash
cmake ... -DCMAKE_CXX_FLAGS='-fpch-preprocess -DWEBIDENTITY_SECRET="\"<secret>\""' ...
```

In Visual Studio:
*Project Properties → C/C++ → Preprocessor → Preprocessor Definitions →
add*  `WEBIDENTITY_SECRET="<secret>"`.

**If you define both** (sphere.ini AND the macro), **sphere.ini wins**.
The macro only applies when the ini is empty.

---

## 10. Step 8 — Configure the proxy

Set the **same** secret on your proxy. If you use
`uonexus-minimal`, edit `servers/<your-shard>.yaml`:

```yaml
webIdentity:
  enabled: true
  secret: <YOUR_48_HEX_WEBIDENTITY_SECRET>
```

Restart the proxy. Make sure it's the same string character for character
as in `sphere.ini` — a single slip = `[WebIdentity] reject: secret
mismatch` on every login.

---

## 11. Verification

When a web client connects for the first time you'll see in the Sphere
log:

```
[WebIdentity] accepted (pre-seed): real=203.0.113.5 userId=12345 role=user authProv=Discord authUser=alice authId=...
```

From then on, every disconnect log and `[information` shows the real IP:

```
... Client disconnected ... IP='203.0.113.5'
```

---

## 12. Troubleshooting

### `Undefined symbol: m_sWebIdentitySecret`

You skipped Step 4 (`CServerConfig.h`). Go back and add the field.

### `error: 'class CServerConfig' has no member named 'm_sWebIdentitySecret'`

The field is in `CServerConfig.h` but not in the `public:` section, or
your struct uses a different default visibility. Make sure it compiles
with the expected visibility. As a shortcut, you can move the field right
after an explicit `public:`.

### `setPeerAddress is not a member of CNetState`

You skipped Step 3. Go back to `CNetState.h`.

### `markReadClosed is not a member of CNetState`

Your Source-X is old. Replace the line with `net->m_socket.Close();` or
whatever close API your fork uses.

### `[WebIdentity] reject: secret length mismatch` on every login

The proxy and Sphere have different secrets. Compare byte for byte.

### `[WebIdentity]` never appears even though the web client connects

Your proxy isn't emitting the `0xA4` frame. Check your proxy docs. Also
verify `WebIdentitySecret=` isn't empty in sphere.ini AND that there are
no trailing spaces.

### Compiles but `[r` doesn't reload the secret

`[r` reloads sphere.ini but some fields require a restart depending on the
Sphere version. To be safe, restart the Sphere process.

---

## 13. Why this patch is safe

- **Fails closed.** With no secret in sphere.ini and no compiled macro,
  the entire block is skipped — the build behaves exactly like upstream.
- **Constant-time secret comparison.** An XOR loop over all bytes avoids
  timing attacks.
- **Detection by magic + length.** Only packets that start with `0xA4` +
  `"CUOWEB"` AND are at least 149 bytes activate the WebIdentity path.
- **The sphere.ini secret is on disk.** Anyone with the file can read it.
  Same threat model as any credential in sphere.ini (MySQL password, etc).

---

## 14. Removing the patch

To go back to clean upstream:

1. Empty the `WebIdentitySecret=` line in sphere.ini (or comment it out
   with `//`).
2. If you used the macro, rebuild without `-DWEBIDENTITY_SECRET=...`.
3. Restore the 5 files from their `.pre-webidentity.bak` backups if you
   also want to remove the patch source code.

---

## Appendix — Differences from the automatic "patch" version

If your workflow has `patch` (Linux, WSL, Git Bash with GnuWin32), the
automated patch is faster:

```bash
patch -p1 < webidentity.patch
```

The Python script `apply_webidentity.py` that lives next to this manual
does the same thing on any OS without depending on `patch`. See the folder
README for the three available paths.
