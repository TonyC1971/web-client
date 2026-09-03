# AI prompt — apply the WebIdentity patch to my Sphere

This document is **a complete, self-contained prompt** you can paste into
any AI assistant (Claude, ChatGPT, Gemini, Copilot, Cursor, etc.) when the
automatic script fails because your Source-X has changes relative to
upstream — renamed functions, reformatting, a divergent fork, etc.

**How to use this document**: copy everything from `=== PROMPT START ===`
to `=== PROMPT END ===` and paste it into your AI. Attach the **6** source
files when the AI asks for them (the AI must ask for them explicitly before
starting).

---

```
=== PROMPT START ===

I need you to apply a patch to 6 files of my Ultima Online server (Sphere
Source-X, fork of SphereServer/Source-X). I'm going to give you the full
context, the exact changes to apply, and the security invariants that must
NOT be broken. Then I'll pass you the source files — wait for my message
with the files before writing any code.

═══════════════════════════════════════════════════════════════════
1. WHAT WE ARE DOING
═══════════════════════════════════════════════════════════════════

My Sphere shard is behind a WebSocket proxy (WebAssembly UO clients
connect to the proxy → the proxy opens a TCP connection to Sphere). The
problem: every web player shows up with the proxy's IP in Sphere's logs
and in the `MaxConnectRequestsPerIP`, `account.LastIP`, etc. heuristics.

The solution: the proxy sends a custom identity packet to Sphere BEFORE
the seed/encryption, containing the player's real IP and a shared secret.
Sphere validates the secret in constant time and, if it passes,
overwrites the CNetState's `m_peerAddress` with the real IP. From that
moment everything that already existed in Sphere sees the real IP.

We reuse packet ID `0xA4` (the legacy PacketSystemInfo, 149 bytes) with
an ASCII magic `"CUOWEB"` to distinguish an override from a legitimate
SystemInfo.

CRITICAL DETAIL — The shared secret is read from **sphere.ini** (key
`WebIdentitySecret=`) at runtime. This lets you rotate the secret without
recompiling — just edit sphere.ini and send `[r]` from the Sphere
console. The compile-time WEBIDENTITY_SECRET macro is still valid as a
fallback (for sealed Docker images), but sphere.ini wins if defined. This
is essential for operations.

═══════════════════════════════════════════════════════════════════
2. THE FILES I'LL PASS YOU (6)
═══════════════════════════════════════════════════════════════════

  1) src/network/CNetState.h         — CNetState declaration
  2) src/game/CServerConfig.h        — global config fields
  3) src/game/CServerConfig.cpp      — RC_* enum + ini keyword table
  4) src/network/receive.cpp         — incoming packet handlers
  5) src/network/CNetworkInput.cpp   — entry point of the network flow
  6) src/network/CNetworkManager.cpp — accept() and per-IP rate-limit

Do NOT start writing code yet. Wait for me to pass them.

═══════════════════════════════════════════════════════════════════
3. REQUIRED CHANGES (10 hunks across 6 files)
═══════════════════════════════════════════════════════════════════

──────────────────────────────────────────────────────────────────
HUNK A — src/network/CNetState.h (1 line)
──────────────────────────────────────────────────────────────────

Add a public method (ONE line) in CNetState that overwrites the
protected `m_peerAddress` field. Location: next to other getters like
`getClient()`.

CODE TO ADD (literal, inside the public: section):

    void setPeerAddress(const CSocketAddress& addr) { m_peerAddress = addr; } // B28 WebIdentity override

──────────────────────────────────────────────────────────────────
HUNK B — src/game/CServerConfig.h (1 line)
──────────────────────────────────────────────────────────────────

Add a `CSString m_sWebIdentitySecret` field to the CServerConfig struct.
Place it in the network settings section, next to similar fields like
`m_iNetMaxQueueSize`.

CODE TO ADD (TWO lines):

    CSString m_sWebIdentitySecret;  // B28: pre-shared secret for WebIdentity 0xA4 intercept. Empty = disabled. Live-reloadable via [r]. WEBIDENTITY_SECRET macro is fallback if empty.
    CSString m_sWebIdentityTrustedProxy; // B29: comma-separated list of proxy IPs allowed to bypass the per-IP rate-limit checks at TCP-accept. WebIdentity rewrites m_peerAddress only post-0xA4, so without this every web user counts against the proxy bridge IP. Empty = disabled (legacy behaviour). Live-reloadable via [r].

──────────────────────────────────────────────────────────────────
HUNK C — src/game/CServerConfig.cpp (entry in the RC enum)
──────────────────────────────────────────────────────────────────

Sphere keeps an `RC_*` enum with one ID per sphere.ini key, in
alphabetical order. Add a new entry between `RC_WALKREGEN` and
`RC_WOOLGROWTHTIME`:

    RC_WEBIDENTITYSECRET,        // m_sWebIdentitySecret (B28)
    RC_WEBIDENTITYTRUSTEDPROXY,  // m_sWebIdentityTrustedProxy (B29)

──────────────────────────────────────────────────────────────────
HUNK D — src/game/CServerConfig.cpp (entry in the keyword table)
──────────────────────────────────────────────────────────────────

There's a table with strings like `{ "WALKBUFFER", { ELEM_INT, ... }}`
that maps ini keys to struct fields. It's alphabetically ordered. Add an
entry between `"WALKREGEN"` and `"WOOLGROWTHTIME"`:

    { "WEBIDENTITYSECRET",       { ELEM_CSTRING,  static_cast<uint>OFFSETOF(CServerConfig,m_sWebIdentitySecret)  }}, // B28
    { "WEBIDENTITYTRUSTEDPROXY", { ELEM_CSTRING,  static_cast<uint>OFFSETOF(CServerConfig,m_sWebIdentityTrustedProxy)  }}, // B29

(The spacing/alignment with tabs should match the neighbouring entries —
Sphere uses tabs in this array.)

──────────────────────────────────────────────────────────────────
HUNK E — src/network/receive.cpp (helper + onReceive + pre-seed helper)
──────────────────────────────────────────────────────────────────

Replace the entire body of `PacketSystemInfo::onReceive` (the upstream
version is trivial: `skip(148); return true;`) with a new version. And
ADD two new functions:
  - `GetWebIdentitySecret()` (static helper that reads
    g_Cfg.m_sWebIdentitySecret and falls back to the WEBIDENTITY_SECRET
    macro)
  - `TryProcessWebIdentityPreSeed(...)` (external helper called from
    CNetworkInput.cpp in the pre-seed path)

FULL CODE TO REPLACE/ADD:

// B28: read WebIdentity secret from sphere.ini first, then fall back to
// the WEBIDENTITY_SECRET preprocessor macro if the ini value is empty.
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
            // now that m_peerAddress is rewritten. Restores [BLOCKIP <ip>]
            // parity with desktop clients.
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

    // v0.3.25 — see comment above PacketSystemInfo's identical block.
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

──────────────────────────────────────────────────────────────────
HUNK F — src/network/CNetworkInput.cpp (B17 pre-auth memcpy hardening)
──────────────────────────────────────────────────────────────────

In the `EXC_SET_BLOCK("encryption setup");` section there's an
`ASSERT(buffer->getRemainingLength() <= sizeof(CEvent));`. ASSERT is a
no-op in Release builds → a malformed client can trigger a memcpy past
the bounds of CEvent and corrupt the heap. THIS IS THE RISKIEST PRE-AUTH
MEMCPY IN THE AUDIT — it runs before any validation, the bytes are
attacker-controlled.

REPLACE the ASSERT with a real check using `g_Log.EventWarn` +
`return false`:

UPSTREAM VERSION:

        EXC_SET_BLOCK("encryption setup");
        ASSERT(buffer->getRemainingLength() <= sizeof(CEvent));

VERSION AFTER (B17):

        EXC_SET_BLOCK("encryption setup");
        // B17 (N02): hard size check pre-auth. ASSERT is a no-op in Release;
        // a malformed login packet > sizeof(CEvent) would memcpy past the
        // struct and corrupt heap. This is the riskiest path in the network
        // audit (pre-auth, attacker-controlled, untrusted bytes).
        if (buffer->getRemainingLength() > sizeof(CEvent))
        {
            g_Log.EventWarn("Oversized login packet from %s (%u > %u); disconnecting.\n",
                            state->m_peerAddress.GetAddrStr(),
                            (uint)buffer->getRemainingLength(),
                            (uint)sizeof(CEvent));
            return false;
        }

──────────────────────────────────────────────────────────────────
HUNK G — src/network/CNetworkInput.cpp (0xA4 pre-seed detection)
──────────────────────────────────────────────────────────────────

Two changes in the method that processes the first data from a
newly-connected client.

CHANGE G.1: declare `bool fWebIdentity = false`, add its detection
inside the `if (state->m_seeded == false)` block, and extend the
INT8_MAX guard with `&& !fWebIdentity`.

VERSION AFTER:

    bool fHTTPReq = false;
    bool fWebIdentity = false; // B28: WebIdentity 0xA4 plaintext preamble
    const uint uiOrigRemainingLength = buffer->getRemainingLength();
    const byte* const pOrigRemainingData = buffer->getRemainingData();
    if (state->m_seeded == false)
    {
        fHTTPReq = (uiOrigRemainingLength >= 5 && memcmp(pOrigRemainingData, "GET /", 5) == 0) ||
            (uiOrigRemainingLength >= 6 && memcmp(pOrigRemainingData, "POST /", 6) == 0);

        // B28: detect WebIdentity 0xA4 preamble before INT8_MAX cap.
        fWebIdentity = (uiOrigRemainingLength >= 149
                        && pOrigRemainingData[0] == XCMD_Spy
                        && memcmp(pOrigRemainingData + 1, "CUOWEB", 6) == 0);
    }
    if (!fHTTPReq && !fWebIdentity && (uiOrigRemainingLength > INT8_MAX))

CHANGE G.2: insert the `else if (fWebIdentity)` block RIGHT BEFORE the
`// check for new seed` comment. It calls `TryProcessWebIdentityPreSeed`
(declared externally from receive.cpp) and consumes 149 bytes:

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

──────────────────────────────────────────────────────────────────
HUNK H — src/network/CNetworkManager.cpp (B29 trusted-proxy parse)
──────────────────────────────────────────────────────────────────

In `CNetworkManager::acceptNewConnection()`, RIGHT AFTER
`EXC_SET_BLOCK("ip history");` and BEFORE the line
`DEBUGNETWORK(("Retrieving IP history for ...`, insert this block that
reads the trusted-proxy list from sphere.ini and stores a local flag
`fTrustedProxy`. It's ~32 lines:

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

──────────────────────────────────────────────────────────────────
HUNK I — src/network/CNetworkManager.cpp (B29 rate-limit bypass)
──────────────────────────────────────────────────────────────────

Further down in the same function (~30 lines after block H) there's an
`if` that applies the 4 rate-limit checks. Upstream version:

    // check if ip is allowed to connect
    if (ip.checkPing() ||
        (maxIp > 0 && ip.m_iPendingConnectionRequests > maxIp) ||
        (climaxIp > 0 && ip.m_iAliveSuccessfulConnections > climaxIp) ||
        (g_Cfg._iMaxConnectRequestsPerIP > 0) && (ip.m_iConnectionRequests >= g_Cfg._iMaxConnectRequestsPerIP))
    {

VERSION AFTER:

    // check if ip is allowed to connect (B29: trusted proxies bypass these)
    if (!fTrustedProxy &&
        (ip.checkPing() ||
        (maxIp > 0 && ip.m_iPendingConnectionRequests > maxIp) ||
        (climaxIp > 0 && ip.m_iAliveSuccessfulConnections > climaxIp) ||
        ((g_Cfg._iMaxConnectRequestsPerIP > 0) && (ip.m_iConnectionRequests >= g_Cfg._iMaxConnectRequestsPerIP))))
    {

NOTE ON PARENTHESES: the original had a latent bug — `||` and `&&` with
mixed precedence and no clear parentheses. When you add `!fTrustedProxy &&`
at the front, you must wrap the WHOLE previous disjunction in `(...)` and
group the last `&&` in its own parentheses to preserve the exact
semantics. If you get it wrong, the compiler warns you with
`-Wparentheses`.

═══════════════════════════════════════════════════════════════════
4. INVARIANTS THAT MUST HOLD AFTER THE PATCH
═══════════════════════════════════════════════════════════════════

FUNCTIONAL:
- Desktop UO clients (without the "CUOWEB" preamble) must keep connecting
  exactly as before.
- If `WebIdentitySecret=` is empty in sphere.ini AND the
  WEBIDENTITY_SECRET macro is not defined, behaviour must be identical to
  upstream without the patch (skip(148)).
- The secret is read EVERY TIME a preamble arrives (not cached), so `[r`
  resync reloads the value without restarting.
- On a validation failure, you must call `net->markReadClosed()` (or your
  fork's equivalent) BEFORE returning.

SECURITY:
- The secret comparison MUST be constant-time: an XOR loop over ALL bytes
  accumulating, checking != 0 at the end. NEVER early-return on the first
  difference.
- The length MUST be compared before the XOR (read OOB protection).
- The "CUOWEB" magic is matched with a 6-byte exact memcmp.
- Only activate if kWebIdentitySecretLen >= 8 (anti-empty-secret).
- The m_sWebIdentitySecret field must NOT be readable from script (don't
  add a case to r_WriteVal); if your AI proposes one, reject it.

═══════════════════════════════════════════════════════════════════
5. HOW I WANT YOU TO PROCEED
═══════════════════════════════════════════════════════════════════

1. Confirm you understand the 6 hunks and the invariants. If you have
   doubts, ask me BEFORE modifying anything.

2. When I pass you the 6 files, return each MODIFIED file IN FULL (no
   fragments), in separate code blocks.

3. Do NOT return a unified-diff patch unless I ask for one.

4. If your version of a file differs significantly from the upstream
   version I assume, STOP and describe the difference to me.

5. After showing me the 6 modified files, give me the verification grep
   commands:

   grep -n "setPeerAddress" src/network/CNetState.h
   grep -n "m_sWebIdentitySecret\|m_sWebIdentityTrustedProxy" src/game/CServerConfig.h  # 2 hits
   grep -n "RC_WEBIDENTITYSECRET\|RC_WEBIDENTITYTRUSTEDPROXY" src/game/CServerConfig.cpp  # 2 hits
   grep -n "WEBIDENTITYSECRET\|WEBIDENTITYTRUSTEDPROXY" src/game/CServerConfig.cpp  # 4 hits
   grep -n "GetWebIdentitySecret" src/network/receive.cpp  # 3 hits
   grep -n "TryProcessWebIdentityPreSeed" src/network/receive.cpp src/network/CNetworkInput.cpp
   grep -n "fWebIdentity" src/network/CNetworkInput.cpp    # 4 hits
   grep -n "fTrustedProxy\|m_sWebIdentityTrustedProxy" src/network/CNetworkManager.cpp  # ~10 hits

6. Remind me that after applying I need to:
   a) Add TWO keys to sphere.ini:

         WebIdentitySecret=<my_hex_secret>
         WebIdentityTrustedProxy=<docker_proxy_bridge_ip>

      `WebIdentityTrustedProxy=` is a comma-separated list with exact
      match. For example `172.22.0.1` for a typical docker compose, or
      `172.22.0.1,10.0.0.5` if you have multiple proxies.

      Without this key, Sphere's 5 per-IP counters (MaxPings,
      MaxConnectRequestsPerIP, ClientMaxIP, ConnectingMax,
      ConnectingMaxIp) are evaluated at TCP-accept against the docker
      bridge IP — ALL web users count as a single attacker and on the
      4th simultaneous login the bridge IP gets banned for NetTTL
      seconds (default 300s = 5 min).

      With the new key, the engine skips those 5 checks for the listed
      IPs (the trusted gateway). The checks stay active for desktop UO
      clients connecting directly to port 2593. Real per-IP enforcement
      lives in the proxy (token-bucket + ban list) and post-0xA4 in
      scripts (account.LastIP, <SOCKETIP>).

      The 5 counters stay at their upstream defaults — there's NO need
      to bump them to 999999 with the B29 bypass active.

   b) Rebuild Sphere (no special flags needed)
   c) Configure the SAME secret in the proxy's webIdentity.secret
   d) Restart the Sphere container ONCE after applying the patch.
      After that, both keys (`WebIdentitySecret` and
      `WebIdentityTrustedProxy`) are hot-reloadable with `[r` resync —
      no need to restart again to rotate/change.

═══════════════════════════════════════════════════════════════════
6. HOW TO GENERATE THE SECRET (my part, not yours)
═══════════════════════════════════════════════════════════════════

I generate it with `openssl rand -hex 24` (48 characters). I put it in
sphere.ini and in the proxy YAML. I do NOT share it with you. If I paste
it to you by mistake, I'll rotate the secret immediately.

═══════════════════════════════════════════════════════════════════

When you've read this and you're ready, tell me. I'll pass you the 6
files in separate messages.

=== PROMPT END ===
```

---

## How a third-party user will use it

1. **Try the automatic script first**:
   ```
   python apply_webidentity.py /path/to/Source-X
   ```
   If it works, you're done.

2. **If the script fails** (because your Source-X has changes relative to
   upstream), then:
   a) Open this file (`AI_INSTALL_PROMPT.md`).
   b) Copy everything between `=== PROMPT START ===` and
      `=== PROMPT END ===`.
   c) Paste it into Claude / ChatGPT / Gemini / the AI of your choice.
   d) When the AI asks for the 6 files, open them one by one and paste them.
   e) The AI returns the 6 modified files.
   f) You paste them into your tree, edit sphere.ini with the secret,
      rebuild, start Sphere.
   g) Verify that `[WebIdentity] accepted: real=<ip>` appears in the logs
      when a web client connects.

3. **If the AI doesn't understand it either**, follow the human manual:
   [`INSTALL_MANUAL.md`](INSTALL_MANUAL.md).

---

## Why this prompt works well with AIs

- **Context before task**: it explains WHAT Sphere is, WHAT the patch is,
  and WHY.
- **Separate, numbered lists**: AIs respect lists better than prose. Each
  hunk is a numbered block.
- **Full code, not a diff**: AIs apply "replace this whole function"
  better than "apply this diff".
- **Explicit invariants**: prevents the AI from "optimizing" things like
  the constant-time comparison.
- **Explicit stop-and-ask**: the AI has literal instructions to STOP and
  ask if your fork differs.
- **Greppable verification**: exact commands at the end.

## Warnings for the user

- **Review the code** the AI returns before pasting it. AIs hallucinate.
  Look at the constant-time XOR loop — some AIs "optimize" it into an
  early-return, which would break security.
- **Don't apply to production on the first try**. Test it on a test build,
  connect a real web client, check the log, then promote.
- **The secret is NOT shared over chat**. You generate it and put it in
  sphere.ini + the proxy. If you accidentally paste it to a public
  service, **rotate the secret immediately**.

## License

GPL-3.0 (same regime as upstream Source-X).
