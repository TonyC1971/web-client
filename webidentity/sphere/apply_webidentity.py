#!/usr/bin/env python3
"""
apply_webidentity.py — One-click installer for the Sphere WebIdentity patch.

Edits 5 files in a Sphere Source-X tree to add WebIdentity (real-IP-from-proxy)
support. Idempotent: re-running it on an already-patched tree is a no-op.
Cross-platform: works on Windows, Linux, macOS — any OS with Python 3.8+.

The secret is read at runtime from sphere.ini (key  WebIdentitySecret=)
which means it can be ROTATED WITHOUT RECOMPILING — just edit the ini and
issue [r] from the console. A compile-time WEBIDENTITY_SECRET preprocessor
macro is honoured as a fallback when the ini value is empty (used by sealed
Docker images that bake the secret at image-build time).

Usage:
    python apply_webidentity.py <path-to-sphere-source-tree>
    python apply_webidentity.py /home/me/Source-X
    python apply_webidentity.py "C:\\sphere\\Source-X"

If the path argument is omitted, the script asks interactively.

Files modified:
    src/network/CNetState.h           — public setPeerAddress() setter
    src/network/CNetworkInput.cpp     — pre-seed dispatch
    src/network/CNetworkManager.cpp   — B29 trusted-proxy bypass
    src/network/receive.cpp           — 0xA4 intercept + helper + ini reader
    src/game/CServerConfig.h          — m_sWebIdentitySecret + m_sWebIdentityTrustedProxy fields
    src/game/CServerConfig.cpp        — RC_WEBIDENTITYSECRET + RC_WEBIDENTITYTRUSTEDPROXY enum + keywords

Each modified file is backed up to <file>.pre-webidentity.bak (only on
first apply).

Exit codes:
    0  — success (patch applied or already applied)
    1  — invalid arguments / source tree not found
    2  — file structure unexpected (upstream drift); see AI_INSTALL_PROMPT.md
    3  — write permission denied
"""

from __future__ import annotations

import argparse
import os
import secrets
import shutil
import sys
from pathlib import Path

# ──────────────────────────────────────────────────────────────────────────
# Patch payloads. Single source of truth.
# ──────────────────────────────────────────────────────────────────────────

# ─── Patch 1: CNetState.h — setter ──────────────────────────────────────────
CNETSTATE_ANCHOR = "    CClient* getClient(void) const { return m_client; } // get linked client"
CNETSTATE_INSERT = (
    "    CClient* getClient(void) const { return m_client; } // get linked client\n"
    "    void setPeerAddress(const CSocketAddress& addr) { m_peerAddress = addr; } // B28 WebIdentity override"
)
CNETSTATE_MARKER = "void setPeerAddress(const CSocketAddress& addr)"


# ─── Patch 2: CServerConfig.h — m_sWebIdentitySecret field ──────────────────
CONFIGH_ANCHOR = """    int  _iMaxConnectRequestsPerIP; // Maximum number of connection requests before rejecting/blocking IP.
    int64 _iTimeoutIncompleteConnectionMs; // Maximum time in milliseconds to wait before closing a connection request wich did not make it into a successful login
\tint\t m_iNetMaxQueueSize;        // max packets to hold per queue (comment out for unlimited)"""

CONFIGH_INSERT = """    int  _iMaxConnectRequestsPerIP; // Maximum number of connection requests before rejecting/blocking IP.
    int64 _iTimeoutIncompleteConnectionMs; // Maximum time in milliseconds to wait before closing a connection request wich did not make it into a successful login
\tCSString m_sWebIdentitySecret;  // B28: pre-shared secret for WebIdentity 0xA4 intercept. Empty = disabled. Live-reloadable via [r]. WEBIDENTITY_SECRET macro is fallback if empty.
\tCSString m_sWebIdentityTrustedProxy; // B29: comma-separated list of proxy IPs allowed to bypass the per-IP rate-limit checks at TCP-accept. WebIdentity rewrites m_peerAddress only post-0xA4, so without this every web user counts against the proxy bridge IP. Empty = disabled (legacy behaviour). Live-reloadable via [r].
\tint\t m_iNetMaxQueueSize;        // max packets to hold per queue (comment out for unlimited)"""

CONFIGH_MARKER = "m_sWebIdentityTrustedProxy"


# ─── Patch 3: CServerConfig.cpp — RC enum entry ─────────────────────────────
CONFIGCPP_ENUM_ANCHOR = """\tRC_WALKBUFFER,
\tRC_WALKREGEN,
\tRC_WOOLGROWTHTIME,\t\t\t// m_iWoolGrowthTime"""

CONFIGCPP_ENUM_INSERT = """\tRC_WALKBUFFER,
\tRC_WALKREGEN,
\tRC_WEBIDENTITYSECRET,\t\t// m_sWebIdentitySecret (B28)
\tRC_WEBIDENTITYTRUSTEDPROXY,\t// m_sWebIdentityTrustedProxy (B29)
\tRC_WOOLGROWTHTIME,\t\t\t// m_iWoolGrowthTime"""

CONFIGCPP_ENUM_MARKER = "RC_WEBIDENTITYTRUSTEDPROXY"


# ─── Patch 4: CServerConfig.cpp — keyword table entry ───────────────────────
CONFIGCPP_KW_ANCHOR = """\t{ "WALKBUFFER",\t\t\t\t{ ELEM_INT,\t\tstatic_cast<uint>OFFSETOF(CServerConfig,m_iWalkBuffer)\t\t\t}},
\t{ "WALKREGEN",\t\t\t\t{ ELEM_INT,\t\tstatic_cast<uint>OFFSETOF(CServerConfig,m_iWalkRegen)\t\t\t}},
\t{ "WOOLGROWTHTIME",\t\t\t{ ELEM_INT,\t\tstatic_cast<uint>OFFSETOF(CServerConfig,m_iWoolGrowthTime)\t\t}},"""

CONFIGCPP_KW_INSERT = """\t{ "WALKBUFFER",\t\t\t\t{ ELEM_INT,\t\tstatic_cast<uint>OFFSETOF(CServerConfig,m_iWalkBuffer)\t\t\t}},
\t{ "WALKREGEN",\t\t\t\t{ ELEM_INT,\t\tstatic_cast<uint>OFFSETOF(CServerConfig,m_iWalkRegen)\t\t\t}},
\t{ "WEBIDENTITYSECRET",\t\t{ ELEM_CSTRING,\tstatic_cast<uint>OFFSETOF(CServerConfig,m_sWebIdentitySecret)\t}}, // B28
\t{ "WEBIDENTITYTRUSTEDPROXY",{ ELEM_CSTRING,\tstatic_cast<uint>OFFSETOF(CServerConfig,m_sWebIdentityTrustedProxy)\t}}, // B29
\t{ "WOOLGROWTHTIME",\t\t\t{ ELEM_INT,\t\tstatic_cast<uint>OFFSETOF(CServerConfig,m_iWoolGrowthTime)\t\t}},"""

CONFIGCPP_KW_MARKER = '"WEBIDENTITYTRUSTEDPROXY"'


# ─── Patch 5: receive.cpp — replace PacketSystemInfo body + add helper ──────
RECEIVE_BEFORE = """bool PacketSystemInfo::onReceive(CNetState* net)
{
\tADDTOCALLSTACK("PacketSystemInfo::onReceive");
\tUnreferencedParameter(net);

\tskip(148);
\treturn true;
}"""

RECEIVE_AFTER = """// B28: read WebIdentity secret from sphere.ini first, then fall back to
// the WEBIDENTITY_SECRET preprocessor macro if the ini value is empty.
// The ini route enables hot-rotation via `[r]` resync (no recompile).
// Both routes return "" if neither is configured -> intercept disabled.
static const char* GetWebIdentitySecret() noexcept
{
\tconst char* iniValue = g_Cfg.m_sWebIdentitySecret.GetBuffer();
\tif (iniValue && iniValue[0] != '\\0')
\t\treturn iniValue;
#ifdef WEBIDENTITY_SECRET
\treturn WEBIDENTITY_SECRET;
#else
\treturn "";
#endif
}

bool PacketSystemInfo::onReceive(CNetState* net)
{
\tADDTOCALLSTACK("PacketSystemInfo::onReceive");

\t// WebIdentity 0xA4 intercept (post-seed path). Secret comes from
\t// sphere.ini `WebIdentitySecret=` (preferred) or compile-time
\t// WEBIDENTITY_SECRET macro (fallback). Empty/short -> disabled.
\tconst char* const kWebIdentitySecret    = GetWebIdentitySecret();
\tconst size_t      kWebIdentitySecretLen = strlen(kWebIdentitySecret);

\tif (kWebIdentitySecretLen >= 8) {
\t\tchar clientType[7] = {0};
\t\treadStringASCII(clientType, 6, false);

\t\tif (memcmp(clientType, "CUOWEB", 6) == 0) {
\t\t\t(void)readByte();
\t\t\t(void)readInt32();

\t\t\tchar secret[128]  = {0}; readStringNullASCII(secret,   sizeof(secret));
\t\t\tchar userId[64]   = {0}; readStringNullASCII(userId,   sizeof(userId));
\t\t\tchar realIp[64]   = {0}; readStringNullASCII(realIp,   sizeof(realIp));
\t\t\tchar authProv[64] = {0}; readStringNullASCII(authProv, sizeof(authProv));
\t\t\tchar authUser[64] = {0}; readStringNullASCII(authUser, sizeof(authUser));
\t\t\tchar authId[64]   = {0}; readStringNullASCII(authId,   sizeof(authId));
\t\t\tchar role[32]     = {0}; readStringNullASCII(role,     sizeof(role));

\t\t\tconst uint remaining = getRemainingLength();
\t\t\tif (remaining > 0) skip(remaining);

\t\t\tconst size_t suppliedLen = strlen(secret);
\t\t\tif (suppliedLen != kWebIdentitySecretLen) {
\t\t\t\tg_Log.Event(LOGM_INIT | LOGL_WARN,
\t\t\t\t\t"[WebIdentity] reject: secret length mismatch (got %zu, expected %zu) userId=%s\\n",
\t\t\t\t\tsuppliedLen, kWebIdentitySecretLen, userId);
\t\t\t\tnet->markReadClosed();
\t\t\t\treturn false;
\t\t\t}
\t\t\tunsigned char acc = 0;
\t\t\tfor (size_t i = 0; i < kWebIdentitySecretLen; ++i)
\t\t\t\tacc |= (unsigned char)(secret[i] ^ kWebIdentitySecret[i]);
\t\t\tif (acc != 0) {
\t\t\t\tg_Log.Event(LOGM_INIT | LOGL_WARN,
\t\t\t\t\t"[WebIdentity] reject: secret mismatch userId=%s ip=%s\\n",
\t\t\t\t\tuserId, realIp);
\t\t\t\tnet->markReadClosed();
\t\t\t\treturn false;
\t\t\t}

\t\t\tCSocketAddress addr;
\t\t\taddr.SetAddrStr(realIp);
\t\t\tnet->setPeerAddress(addr);

\t\t\t// v0.3.25 (rev2 + post-rewrite re-check): now that m_peerAddress
\t\t\t// holds the REAL IP, run the per-IP enforcement (ip-ban + MaxPings)
\t\t\t// against IT, not against the bridge IP. Mirrors what the engine
\t\t\t// would do at TCP-accept for a desktop client. Preserves
\t\t\t// `[BLOCKIP <real-ip>]` parity between desktop and web.
\t\t\tHistoryIP& realHist = g_NetworkManager.getIPHistoryManager().getHistoryForIP(realIp);
\t\t\tif (realHist.checkPing()) {
\t\t\t\tg_Log.Event(LOGM_INIT|LOGL_WARN,
\t\t\t\t\t"[WebIdentity] post-rewrite reject: real IP %s blocked or MaxPings exceeded "
\t\t\t\t\t"(blocked=%d pings=%d) userId=%s\\n",
\t\t\t\t\trealIp, (int)realHist.m_fBlocked, realHist.m_iPings, userId);
\t\t\t\tnet->markReadClosed();
\t\t\t\treturn false;
\t\t\t}

\t\t\tg_Log.Event(LOGM_INIT,
\t\t\t\t"[WebIdentity] accepted: real=%s userId=%s role=%s authProv=%s authUser=%s authId=%s\\n",
\t\t\t\trealIp, userId, role, authProv, authUser, authId);
\t\t\treturn true;
\t\t}

\t\tskip(148 - 6);
\t} else {
\t\tskip(148);
\t}
\treturn true;
}


// WebIdentity helper for the pre-seed path (called from CNetworkInput.cpp).
bool TryProcessWebIdentityPreSeed(CNetState* net, const byte* data, uint dataLen)
{
\tif (dataLen < 149) return false;
\tif (data[0] != XCMD_Spy) return false;
\tif (memcmp(data + 1, "CUOWEB", 6) != 0) return false;

\tconst char* const kWebIdentitySecret    = GetWebIdentitySecret();
\tconst size_t      kWebIdentitySecretLen = strlen(kWebIdentitySecret);
\tif (kWebIdentitySecretLen < 8) return false;

\t(void)data[7];
\tconst byte* p   = data + 12;
\tconst byte* end = data + 149;

\tauto readStrz = [&](char* out, size_t cap) -> bool {
\t\tsize_t i = 0;
\t\twhile (p < end && *p != 0 && i + 1 < cap) { out[i++] = (char)*p++; }
\t\tif (p >= end) return false;
\t\tout[i] = 0;
\t\t++p;
\t\treturn true;
\t};

\tchar secret[128]  = {0}; if (!readStrz(secret,   sizeof(secret)))   return false;
\tchar userId[64]   = {0}; if (!readStrz(userId,   sizeof(userId)))   return false;
\tchar realIp[64]   = {0}; if (!readStrz(realIp,   sizeof(realIp)))   return false;
\tchar authProv[64] = {0}; if (!readStrz(authProv, sizeof(authProv))) return false;
\tchar authUser[64] = {0}; if (!readStrz(authUser, sizeof(authUser))) return false;
\tchar authId[64]   = {0}; if (!readStrz(authId,   sizeof(authId)))   return false;
\tchar role[32]     = {0}; if (!readStrz(role,     sizeof(role)))     return false;

\tconst size_t suppliedLen = strlen(secret);
\tif (suppliedLen != kWebIdentitySecretLen) {
\t\tg_Log.Event(LOGM_INIT | LOGL_WARN,
\t\t\t"[WebIdentity] reject (pre-seed): secret length mismatch (got %zu, expected %zu) userId=%s\\n",
\t\t\tsuppliedLen, kWebIdentitySecretLen, userId);
\t\tnet->markReadClosed();
\t\treturn true;
\t}
\tunsigned char acc = 0;
\tfor (size_t i = 0; i < kWebIdentitySecretLen; ++i)
\t\tacc |= (unsigned char)(secret[i] ^ kWebIdentitySecret[i]);
\tif (acc != 0) {
\t\tg_Log.Event(LOGM_INIT | LOGL_WARN,
\t\t\t"[WebIdentity] reject (pre-seed): secret mismatch userId=%s ip=%s\\n",
\t\t\tuserId, realIp);
\t\tnet->markReadClosed();
\t\treturn true;
\t}

\tCSocketAddress addr;
\taddr.SetAddrStr(realIp);
\tnet->setPeerAddress(addr);

\t// v0.3.25 (rev2 + post-rewrite re-check): run per-IP enforcement
\t// (ip-ban + MaxPings) against the REWRITTEN real IP, not the bridge.
\t// At TCP-accept the bridge bypassed enforcement (B29 trusted-proxy
\t// gate); here we apply the equivalent check on the real IP so
\t// `[BLOCKIP <real-ip>]` from the GM console works the same way it
\t// does for desktop clients. checkPing also increments m_iPings —
\t// rapid login attempts by a single real IP accumulate just as if
\t// they had connected directly to the listening port.
\tHistoryIP& realHist = g_NetworkManager.getIPHistoryManager().getHistoryForIP(realIp);
\tif (realHist.checkPing()) {
\t\tg_Log.Event(LOGM_INIT|LOGL_WARN,
\t\t\t"[WebIdentity] post-rewrite reject (pre-seed): real IP %s blocked or MaxPings exceeded "
\t\t\t"(blocked=%d pings=%d) userId=%s\\n",
\t\t\trealIp, (int)realHist.m_fBlocked, realHist.m_iPings, userId);
\t\tnet->markReadClosed();
\t\treturn true;
\t}

\tg_Log.Event(LOGM_INIT,
\t\t"[WebIdentity] accepted (pre-seed): real=%s userId=%s role=%s authProv=%s authUser=%s authId=%s\\n",
\t\trealIp, userId, role, authProv, authUser, authId);
\treturn true;
}"""

RECEIVE_MARKER = "TryProcessWebIdentityPreSeed"


# ─── Patch 6: CNetworkInput.cpp — declare fWebIdentity + dispatch block ─────
NETINPUT_BEFORE_1 = """    bool fHTTPReq = false;
    const uint uiOrigRemainingLength = buffer->getRemainingLength();
    const byte* const pOrigRemainingData = buffer->getRemainingData();
    if (state->m_seeded == false)
    {
        fHTTPReq = (uiOrigRemainingLength >= 5 && memcmp(pOrigRemainingData, "GET /", 5) == 0) ||
            (uiOrigRemainingLength >= 6 && memcmp(pOrigRemainingData, "POST /", 6) == 0);
    }
    if (!fHTTPReq && (uiOrigRemainingLength > INT8_MAX))"""

NETINPUT_AFTER_1 = """    bool fHTTPReq = false;
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
    if (!fHTTPReq && !fWebIdentity && (uiOrigRemainingLength > INT8_MAX))"""

NETINPUT_BEFORE_2 = """        // check for new seed (sometimes it's received on its own)
        else if (uiOrigRemainingLength == 1 && pOrigRemainingData[0] == XCMD_NewSeed)"""

NETINPUT_AFTER_2 = """        // B28: WebIdentity 0xA4 plaintext preamble - process before any
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
        else if (uiOrigRemainingLength == 1 && pOrigRemainingData[0] == XCMD_NewSeed)"""

NETINPUT_MARKER_1 = "bool fWebIdentity = false"
NETINPUT_MARKER_2 = "TryProcessWebIdentityPreSeed(state"


# ─── Patch 7: CNetworkManager.cpp — B29 trusted-proxy parse + bypass ────────
NETMANAGER_BEFORE_1 = """    // check ip history
    EXC_SET_BLOCK("ip history");

    DEBUGNETWORK(("Retrieving IP history for '%s'.\\n", client_addr.GetAddrStr()));"""

NETMANAGER_AFTER_1 = """    // check ip history
    EXC_SET_BLOCK("ip history");

    // B29: WebIdentity trusted-proxy bypass. WebIdentity rewrites
    // m_peerAddress only AFTER the 0xA4 frame is read (post-byte-read),
    // but the rate-limit checks below run pre-byte-read at TCP-accept,
    // so without this every web user counts against the proxy bridge IP.
    // When sphere.ini's `WebIdentityTrustedProxy=` (comma-separated list)
    // contains the accepting peer's IP, skip the per-IP rate-limit
    // counters (m_iPendingConnectionRequests, m_iAliveSuccessfulConnections,
    // _iMaxConnectRequestsPerIP). The IP-ban check (ip.checkPing) STILL
    // fires for trusted proxies — v0.3.20 hardening: a compromised proxy
    // host can still be banned by the operator via standard sphere
    // mechanisms even if it would otherwise count as trusted.
    //
    // Real per-IP enforcement of WebIdentity traffic still happens at:
    //   (a) the proxy layer (token-bucket rate limit + admin ban list)
    //   (b) post-0xA4 via account.LastIP / <SOCKETIP> in scripts (uses
    //       the rewritten real IP, not the bridge).
    //
    // v0.3.20 IP normalisation: peer addresses can arrive in several
    // forms depending on the listening socket family + OS (`172.22.0.1`,
    // `::ffff:172.22.0.1` IPv4-mapped IPv6 on dual-stack listeners,
    // `[::1]` with brackets in some printers). We normalise BOTH the
    // peer string and each list entry by stripping the `::ffff:` prefix
    // and surrounding `[` `]` before bytewise compare. The previous
    // strict memcmp silently failed on dual-stack systems, leaving the
    // bypass dead and every web user re-throttled — which is the
    // exact bug B29 was meant to close.
    //
    // Empty list = disabled (legacy behaviour for non-proxy deployments).
    bool fTrustedProxy = false;
    {
        const char* const trustedList = g_Cfg.m_sWebIdentityTrustedProxy.GetBuffer();
        const char* peerStr = client_addr.GetAddrStr();
        // Strip ::ffff: IPv4-mapped IPv6 prefix.
        if (peerStr && strncmp(peerStr, "::ffff:", 7) == 0) peerStr += 7;
        // Strip leading bracket (some printers wrap IPv6 in [..]).
        if (peerStr && peerStr[0] == '[') ++peerStr;
        size_t peerLen = peerStr ? strlen(peerStr) : 0;
        // Strip trailing bracket if present.
        if (peerLen > 0 && peerStr[peerLen - 1] == ']') --peerLen;
        if (trustedList && trustedList[0] != '\\0' && peerLen > 0)
        {
            const char* p = trustedList;
            while (*p && !fTrustedProxy)
            {
                while (*p == ' ' || *p == '\\t') ++p;
                const char* start = p;
                while (*p && *p != ',') ++p;
                const char* end = p;
                while (end > start && (end[-1] == ' ' || end[-1] == '\\t')) --end;
                // Apply same normalisation to the list entry.
                if ((size_t)(end - start) >= 7 && memcmp(start, "::ffff:", 7) == 0) start += 7;
                if (start < end && *start == '[') ++start;
                if (start < end && end[-1] == ']') --end;
                if ((size_t)(end - start) == peerLen && memcmp(start, peerStr, peerLen) == 0)
                    fTrustedProxy = true;
                if (*p == ',') ++p;
            }
        }
        // Once-per-server-boot diagnostic: log the normalised peer string
        // the FIRST time the trusted-proxy list is non-empty AND the
        // bypass DOESN'T fire — surfaces silent IP-format mismatches
        // (the operator wrote 172.22.0.1, peer arrives as ::ffff:172.22.0.1).
        static bool s_loggedFirstMismatch = false;
        if (trustedList && trustedList[0] != '\\0' && !fTrustedProxy && !s_loggedFirstMismatch)
        {
            s_loggedFirstMismatch = true;
            g_Log.Event(LOGM_INIT|LOGL_WARN,
                "WebIdentityTrustedProxy is set ('%s') but peer '%s' did NOT match. "
                "Add this exact peer string to the list if it is your proxy.\\n",
                trustedList, peerStr ? peerStr : "(null)");
        }
    }

    DEBUGNETWORK(("Retrieving IP history for '%s'.\\n", client_addr.GetAddrStr()));"""

NETMANAGER_BEFORE_2 = """    // check if ip is allowed to connect
    if (ip.checkPing() ||\t\t\t\t\t\t\t\t                // check for ip ban and connection attempts (decaying)
        (maxIp > 0 && ip.m_iPendingConnectionRequests > maxIp) ||       // check for too many connecting
        (climaxIp > 0 && ip.m_iAliveSuccessfulConnections > climaxIp) ||// check for too many connected
        (g_Cfg._iMaxConnectRequestsPerIP > 0) && (ip.m_iConnectionRequests >= g_Cfg._iMaxConnectRequestsPerIP)) // connection attempts (not decaying)"""

NETMANAGER_AFTER_2 = """    // check if ip is allowed to connect.
    // B29 + v0.3.25 (rev2): trusted proxies bypass ALL per-IP rate-limit
    // checks at TCP-accept, INCLUDING ip.checkPing(). The v0.3.20 audit
    // kept checkPing enforced as defense-in-depth ("compromised proxy
    // can still be banned by operator"), but observed reality (chaos
    // run 2026-05-05): checkPing() returns true once m_iPings >= MaxPings
    // (~15 by default), and at scale (10+ web logins/min via the bridge)
    // the bridge IP trips MaxPings within seconds — kicked, blocked=1
    // set with TTL=300s, every subsequent web user rejected. The
    // intended threat ("ban the bridge if proxy is compromised") was
    // theoretical: banning the bridge is self-DOS, not enforcement.
    // Real attacker IPs are visible AFTER WebIdentity rewrite via
    // <SRC.IP> in scripts. So we now skip the entire per-IP enforcement
    // for trusted proxies — the bridge becomes invisible at TCP-accept,
    // all gating happens (a) at the proxy layer (token-bucket rate
    // limit + admin ban list) and (b) post-0xA4 via real IP in scripts.
    if (!fTrustedProxy &&
        (ip.checkPing() ||\t\t\t\t\t\t\t\t                // check for ip ban + MaxPings + connection attempts
        ((maxIp > 0 && ip.m_iPendingConnectionRequests > maxIp) ||      // check for too many connecting
         (climaxIp > 0 && ip.m_iAliveSuccessfulConnections > climaxIp) ||// check for too many connected
         ((g_Cfg._iMaxConnectRequestsPerIP > 0) && (ip.m_iConnectionRequests >= g_Cfg._iMaxConnectRequestsPerIP))))) // connection attempts (not decaying)"""

NETMANAGER_MARKER_1 = "bool fTrustedProxy = false"
# v0.3.25-rev2: bypass widened back — `if (!fTrustedProxy &&` again
# leads the rebuilt block (single outer gate around all 4 throttle
# checks including ip.checkPing). Marker matches the line that
# starts with `if (!fTrustedProxy &&`.
NETMANAGER_MARKER_2 = "if (!fTrustedProxy &&"


# ──────────────────────────────────────────────────────────────────────────
# I/O helpers
# ──────────────────────────────────────────────────────────────────────────

class Colour:
    OK = "\033[32m" if os.name != "nt" or "WT_SESSION" in os.environ else ""
    WARN = "\033[33m" if os.name != "nt" or "WT_SESSION" in os.environ else ""
    ERR = "\033[31m" if os.name != "nt" or "WT_SESSION" in os.environ else ""
    BOLD = "\033[1m" if os.name != "nt" or "WT_SESSION" in os.environ else ""
    OFF = "\033[0m" if os.name != "nt" or "WT_SESSION" in os.environ else ""


def info(msg: str) -> None:
    print(f"  {msg}")


def step(n: int, total: int, msg: str) -> None:
    print(f"\n{Colour.BOLD}[{n}/{total}] {msg}{Colour.OFF}")


def ok(msg: str) -> None:
    print(f"  {Colour.OK}[OK]{Colour.OFF} {msg}")


def warn(msg: str) -> None:
    print(f"  {Colour.WARN}[!]{Colour.OFF} {msg}")


def fail(msg: str, code: int = 2) -> None:
    print(f"\n{Colour.ERR}[X] ERROR:{Colour.OFF} {msg}")
    print(f"\n  If your Source-X has drifted a lot, try the AI shortcut:")
    print(f"  open AI_INSTALL_PROMPT.md and hand it to Claude/ChatGPT/Gemini")
    print(f"  along with your source files. The AI applies the changes by")
    print(f"  understanding the patch intent, not by matching exact text.")
    sys.exit(code)


# ──────────────────────────────────────────────────────────────────────────
# Locate + back up + patch
# ──────────────────────────────────────────────────────────────────────────

def locate_file(root: Path, rel_path: str) -> Path:
    p = root / rel_path
    if not p.is_file():
        fail(f"Can't find {rel_path}.\n"
             f"  Is {root} the root of the Source-X repository?\n"
             f"  The file {p} must exist", 1)
    return p


def backup_once(p: Path) -> bool:
    bk = p.with_suffix(p.suffix + ".pre-webidentity.bak")
    if bk.exists():
        return False
    shutil.copy2(p, bk)
    return True


def apply_patch(p: Path, before: str, after: str, marker: str, label: str) -> str:
    text = p.read_text(encoding="utf-8")
    if marker in text:
        return "already applied"
    if before not in text:
        fail(f"Can't find the block to patch in {p.name} ({label}).\n"
             f"  Your Source-X differs from upstream for this file.\n"
             f"  Restore the .pre-webidentity.bak backup if one exists.", 2)
    new = text.replace(before, after, 1)
    is_first_backup = backup_once(p)
    p.write_text(new, encoding="utf-8")
    return "applied" + (" (backup created)" if is_first_backup else "")


def patch_cnetstate(root: Path) -> str:
    p = locate_file(root, "src/network/CNetState.h")
    return apply_patch(p, CNETSTATE_ANCHOR, CNETSTATE_INSERT, CNETSTATE_MARKER,
                       "setter setPeerAddress")


def patch_configh(root: Path) -> str:
    p = locate_file(root, "src/game/CServerConfig.h")
    return apply_patch(p, CONFIGH_ANCHOR, CONFIGH_INSERT, CONFIGH_MARKER,
                       "m_sWebIdentitySecret field")


def patch_configcpp(root: Path) -> str:
    p = locate_file(root, "src/game/CServerConfig.cpp")
    text = p.read_text(encoding="utf-8")
    already = CONFIGCPP_ENUM_MARKER in text and CONFIGCPP_KW_MARKER in text
    if already:
        return "already applied"
    is_first_backup = backup_once(p)
    new = text
    if CONFIGCPP_ENUM_MARKER not in new:
        if CONFIGCPP_ENUM_ANCHOR not in new:
            fail("Can't find the RC_ enum region in CServerConfig.cpp.\n"
                 "  Upstream probably reordered the W* entries. Apply it\n"
                 "  manually or use AI_INSTALL_PROMPT.md.", 2)
        new = new.replace(CONFIGCPP_ENUM_ANCHOR, CONFIGCPP_ENUM_INSERT, 1)
    if CONFIGCPP_KW_MARKER not in new:
        if CONFIGCPP_KW_ANCHOR not in new:
            fail("Can't find the keyword table in CServerConfig.cpp.\n"
                 "  Upstream probably reformatted the W* entries. Apply it\n"
                 "  manually or use AI_INSTALL_PROMPT.md.", 2)
        new = new.replace(CONFIGCPP_KW_ANCHOR, CONFIGCPP_KW_INSERT, 1)
    p.write_text(new, encoding="utf-8")
    return "applied" + (" (backup created)" if is_first_backup else "")


def patch_receive(root: Path) -> str:
    p = locate_file(root, "src/network/receive.cpp")
    return apply_patch(p, RECEIVE_BEFORE, RECEIVE_AFTER, RECEIVE_MARKER,
                       "PacketSystemInfo + helper + ini reader")


def patch_netinput(root: Path) -> str:
    p = locate_file(root, "src/network/CNetworkInput.cpp")
    text = p.read_text(encoding="utf-8")
    already = NETINPUT_MARKER_1 in text and NETINPUT_MARKER_2 in text
    if already:
        return "already applied"
    is_first_backup = backup_once(p)
    new = text
    if NETINPUT_MARKER_1 not in new:
        if NETINPUT_BEFORE_1 not in new:
            fail("Can't find the pre-seed block in CNetworkInput.cpp.", 2)
        new = new.replace(NETINPUT_BEFORE_1, NETINPUT_AFTER_1, 1)
    if NETINPUT_MARKER_2 not in new:
        if NETINPUT_BEFORE_2 not in new:
            fail("Can't find 'check for new seed' in CNetworkInput.cpp.", 2)
        new = new.replace(NETINPUT_BEFORE_2, NETINPUT_AFTER_2, 1)
    p.write_text(new, encoding="utf-8")
    return "applied" + (" (backup created)" if is_first_backup else "")


def patch_netmanager(root: Path) -> str:
    p = locate_file(root, "src/network/CNetworkManager.cpp")
    text = p.read_text(encoding="utf-8")
    already = NETMANAGER_MARKER_1 in text and NETMANAGER_MARKER_2 in text
    if already:
        return "already applied"
    is_first_backup = backup_once(p)
    new = text
    if NETMANAGER_MARKER_1 not in new:
        if NETMANAGER_BEFORE_1 not in new:
            fail("Can't find the 'ip history' block in CNetworkManager.cpp.\n"
                 "  Upstream probably reformatted acceptNewConnection().\n"
                 "  Apply it manually following INSTALL_MANUAL.md Step 5.5,\n"
                 "  or use AI_INSTALL_PROMPT.md (HUNK H + I).", 2)
        new = new.replace(NETMANAGER_BEFORE_1, NETMANAGER_AFTER_1, 1)
    if NETMANAGER_MARKER_2 not in new:
        if NETMANAGER_BEFORE_2 not in new:
            fail("Can't find the 'check if ip is allowed to connect' block in\n"
                 "  CNetworkManager.cpp. Apply it manually (INSTALL_MANUAL.md\n"
                 "  Step 5.5) or use AI_INSTALL_PROMPT.md (HUNK I).", 2)
        new = new.replace(NETMANAGER_BEFORE_2, NETMANAGER_AFTER_2, 1)
    p.write_text(new, encoding="utf-8")
    return "applied" + (" (backup created)" if is_first_backup else "")


# ──────────────────────────────────────────────────────────────────────────
# Main
# ──────────────────────────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(
        description="Applies the WebIdentity patch (secret read from sphere.ini) "
                    "to a Sphere Source-X tree.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Example:  python apply_webidentity.py /home/me/Source-X",
    )
    parser.add_argument(
        "source_root",
        nargs="?",
        help="Path to the Source-X repository root (the folder that contains src/).",
    )
    parser.add_argument(
        "--no-secret",
        action="store_true",
        help="Don't generate a new secret at the end (use this if you already have one).",
    )
    args = parser.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    print(f"\n{Colour.BOLD}Sphere WebIdentity - automated installer{Colour.OFF}")
    print(f"{'=' * 60}\n")

    if args.source_root:
        root = Path(args.source_root).resolve()
    else:
        try:
            raw = input("Path to the Source-X repository (the folder that contains src/):\n> ").strip().strip('"')
        except (EOFError, KeyboardInterrupt):
            print()
            return 1
        if not raw:
            print(f"{Colour.ERR}[X] You didn't provide a path.{Colour.OFF}")
            return 1
        root = Path(raw).resolve()

    if not root.is_dir():
        print(f"{Colour.ERR}[X] Folder does not exist:{Colour.OFF} {root}")
        return 1
    if not (root / "src" / "network" / "CNetState.h").is_file():
        print(f"{Colour.ERR}[X] The folder {root} doesn't look like a Source-X tree.{Colour.OFF}")
        print(f"  Expected to find src/network/CNetState.h inside it.")
        return 1

    info(f"Source-X detected at: {root}")

    try:
        TOTAL = 7
        step(1, TOTAL, "src/network/CNetState.h (setPeerAddress setter)")
        ok(patch_cnetstate(root))

        step(2, TOTAL, "src/game/CServerConfig.h (B28 + B29 fields)")
        ok(patch_configh(root))

        step(3, TOTAL, "src/game/CServerConfig.cpp (RC enum + keyword B28 + B29)")
        ok(patch_configcpp(root))

        step(4, TOTAL, "src/network/receive.cpp (intercept + helper + ini reader)")
        ok(patch_receive(root))

        step(5, TOTAL, "src/network/CNetworkInput.cpp (pre-seed dispatch)")
        ok(patch_netinput(root))

        step(6, TOTAL, "src/network/CNetworkManager.cpp (B29 trusted-proxy bypass)")
        ok(patch_netmanager(root))

        step(7, TOTAL, "Secret + WebIdentityTrustedProxy configuration")
    except PermissionError as e:
        fail(f"Can't write to disk: {e}\n"
             f"  Run with sufficient privileges (sudo / Administrator) or\n"
             f"  close any IDE that has the files open.", 3)

    if args.no_secret:
        info("Skipped because of --no-secret. Edit sphere.ini as you see fit.")
    else:
        secret = secrets.token_hex(24)
        ok(f"Secret generated (24 hex bytes):")
        info(f"    {Colour.BOLD}{secret}{Colour.OFF}")

    print(f"\n{Colour.BOLD}Final steps{Colour.OFF}")
    print("-" * 60)
    print(f"\n  {Colour.BOLD}A){Colour.OFF} {Colour.BOLD}Add TWO keys to your sphere.ini{Colour.OFF}")
    if not args.no_secret:
        print(f"     WebIdentitySecret={secret}")
    else:
        print(f"     WebIdentitySecret=<your_secret>")
    print(f"     WebIdentityTrustedProxy=<docker_proxy_bridge_ip>")
    print(f"")
    print(f"     Typical docker compose example: WebIdentityTrustedProxy=172.22.0.1")
    print(f"     (multiple proxies, comma-separated, no spaces:")
    print(f"      WebIdentityTrustedProxy=172.22.0.1,10.0.0.5)")
    print(f"")
    print(f"     {Colour.BOLD}Without this second key{Colour.OFF}, Sphere's 5 per-IP counters")
    print(f"     (MaxPings, MaxConnectRequestsPerIP, ClientMaxIP, ConnectingMax,")
    print(f"     ConnectingMaxIp) count ALL web logins against the docker")
    print(f"     bridge IP. On the 4th simultaneous login the bridge gets banned")
    print(f"     for 5 minutes = every web user rejected.")
    print(f"")
    print(f"     Both keys are hot-reloadable via [r] (no recompile) after the")
    print(f"     initial post-patch restart.")
    print(f"")
    print(f"  {Colour.BOLD}B){Colour.OFF} {Colour.BOLD}Build Sphere{Colour.OFF}")
    print(f"     No special flags needed. CMake and Visual Studio as usual.")
    print(f"     The patch compiles as a no-op while")
    print(f"     sphere.ini.WebIdentitySecret is empty.")
    print(f"")
    print(f"     (Optional - only if you do NOT want to use sphere.ini and prefer")
    print(f"     to bake the secret into the binary):")
    print(f"     cmake ... -DCMAKE_CXX_FLAGS='... -DWEBIDENTITY_SECRET=\"\\\"<secret>\\\"\"' ...")
    print(f"")
    print(f"  {Colour.BOLD}C){Colour.OFF} {Colour.BOLD}Set the SAME secret on your proxy{Colour.OFF}")
    print(f"     uonexus-minimal:  servers/<your-shard>.yaml")
    print(f"     -> webIdentity.secret")
    print(f"")
    print(f"  {Colour.BOLD}D){Colour.OFF} {Colour.BOLD}Verify the first web login{Colour.OFF}")
    print(f"     [WebIdentity] accepted: real=<real-ip> userId=...")
    print(f"     in the running Sphere's logs.")

    print(f"\n{Colour.OK}{Colour.BOLD}[OK] Done.{Colour.OFF}\n")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print(f"\n{Colour.WARN}Cancelled by the user.{Colour.OFF}")
        sys.exit(130)
