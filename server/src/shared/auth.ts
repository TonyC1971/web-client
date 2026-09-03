import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { db, migrateJsonOnce } from './db.js';
import { writeFileAtomic, writeFileAtomicAsync } from './atomicJson.js';
import { withFileLockSync } from './fileLock.js';
import { OWNS_JOBS } from './proxyRole.js';
import { eraseUserData } from './userData.js';
import { deleteAllScreenshots } from './screenshotStore.js';
import { deleteUserLogo } from './userLogoStore.js';
import { scheduleAccountDeletion, cancelAccountDeletion, getPendingDeletion } from './accountDeletion.js';
import { updateRuntimeConfig, getRuntimeConfigFile } from './runtimeConfig.js';
import { getSigningSecret, getVerifySecrets, getMinIssuedAt } from './jwtSecrets.js';
import { getOAuth } from './discordIntegration.js';
import { getFlag } from './serverFlags.js';
// 🚨 NO DIRECT IMPORT OF cards.js, and that is structural rather than tidiness.
// This module is the whole identity layer — JWT, Discord OAuth, profiles — and it is exactly what a
// MINIMAL self-hosted install needs. Importing cards.js pulled the trading-card economy into that
// install's dependency graph for one best-effort side effect on login, which is how a "small
// backend" quietly becomes the full product. uonexus registers the grant at startup; a build that
// ships no economy simply never registers one, and the login path is identical either way.
type LoginSideEffect = (sub: string) => void;
let _onDiscordLogin: LoginSideEffect | null = null;
/** Register a best-effort side effect to run once per Discord login (uonexus: the welcome card). */
export function setDiscordLoginSideEffect(fn: LoginSideEffect | null): void {
  _onDiscordLogin = fn;
  console.log(`[registry] discord-login side effect: ${fn ? 'registered' : 'cleared'}`);
}

// The SAME reasoning, applied to identity rather than login, and it took a graph walk to notice.
// recordIdentity used to call ensureNickname() and syncCharacter() directly. Both are uonexus
// concerns — a public nickname exists for the rankings, and the character push targets the
// minigames shard — and neither is referenced anywhere in the minimal client, which mentions
// "nickname" zero times and has no minigames. Yet those two imports put nicknames.ts (20 KB of
// public rankings, visibility opt-in, impersonation guards) and characterSync.ts into the minimal
// backend's import graph, and the publisher ships what the graph reaches.
//
// So it is one registration instead of two imports. uonexus registers it at entrypoint load,
// before a request can be served; a build with neither rankings nor a shard bridge registers
// nothing and recordIdentity does exactly what it says on the tin.
type IdentitySideEffect = (sub: string) => void;
let _onIdentity: IdentitySideEffect | null = null;
/** Register work to run whenever a Discord user identifies themselves (uonexus: nickname + character). */
export function setIdentitySideEffect(fn: IdentitySideEffect | null): void {
  _onIdentity = fn;
  console.log(`[registry] identity side effect: ${fn ? 'registered' : 'cleared'}`);
}
import { clientIpFromReq } from './clientIp.js';
import type { Request, Response } from 'express';
import {
  DISCORD_CLIENT_ID,
  DISCORD_CLIENT_SECRET,
  DISCORD_REDIRECT_URI,
  JWT_SECRET,
  DATA_PATH,
  IS_PRODUCTION,
  DEV_MODE,
  DEV_USER_SUB,
  DEV_USER_NAME,
  isDiscordAdmin,
  adminScopes,
  adminOwnedServers,
  getPublicOrigins,
} from './config.js';

// Per-IP guest-MINT budget (audit 2026-06-22 #2): a guest reuses its cookie/localStorage id (the early
// returns in handleGuestLogin short-circuit), so a legit browser mints ~once; only an attacker rotates
// fresh guestIds to flood the shared shard with auto-created g<hex> accounts. Cap NEW mints per IP per
// window — generous for NAT (many real users behind one IP), tight enough to stop a ~14k/day flood.
const GUEST_MINT_CAP = 30;
const GUEST_MINT_WINDOW_MS = 60 * 60 * 1000; // 1 h
const _guestMint = new Map<string, { n: number; resetAt: number }>();
function guestMintAllowed(ip: string): boolean {
  const now = Date.now();
  if (_guestMint.size > 5000) { for (const [k, v] of _guestMint) { if (now >= v.resetAt) _guestMint.delete(k); } } // bound the map itself
  let e = _guestMint.get(ip);
  if (!e || now >= e.resetAt) { e = { n: 0, resetAt: now + GUEST_MINT_WINDOW_MS }; _guestMint.set(ip, e); }
  if (e.n >= GUEST_MINT_CAP) return false;
  e.n++;
  return true;
}

// ── JWT (manual HS256, zero extra deps) ──────────────────────────────────────

const COOKIE_NAME = '__session';
const COOKIE_MAX_AGE = 60 * 60 * 24 * 30; // 30 days
// v0.3.20: guest sessions expire much sooner than Discord sessions —
// they're meant to grant ephemeral asset-fetch access for try-before-
// SSO, not become a persistent identity. 24h gives a single play
// session without forcing the player to re-mint mid-game; the next
// day they get a fresh sub or move to Discord login.
const GUEST_COOKIE_MAX_AGE = 60 * 60 * 24; // 24 hours
const OAUTH_STATE_COOKIE = '__oauth_state';
const OAUTH_STATE_MAX_AGE = 600; // 10 minutes
// `Secure` flag in production only; localhost dev runs over plain HTTP and
// browsers refuse Secure cookies on http origins. IS_PRODUCTION reads NODE_ENV
// or the explicit PRODUCTION=1 override.
const COOKIE_FLAGS = IS_PRODUCTION
  ? 'HttpOnly; Secure; SameSite=Lax'
  : 'HttpOnly; SameSite=Lax';

// Returns the Domain attribute for a Set-Cookie header, so that a login on an apex domain is also
// accepted by its subdomains. ⚠️ This comment used to name a specific deployment and describe the
// behaviour as hardcoded — it stopped being true when the apex moved to COOKIE_DOMAIN, and a
// comment that documents the previous implementation is worse than none: the next reader trusts it.
function cookieDomain(req: Request): string {
  // 🚨 CONFIGURED, NOT HARDCODED. This used to name the uonexus domain literally, which is both a
  // deployment detail in a file meant to be published and dead weight for anyone else: their host
  // never matches, so they silently get the host-scoped branch anyway.
  //
  // COOKIE_DOMAIN is the apex a login should span (uonexus sets its own so dev.* shares the
  // session). Unset — the default, and what a single-domain self-hosted install wants — every
  // cookie stays HOST-SCOPED, the narrower and safer behaviour. Widening a cookie has to be asked
  // for explicitly, never inherited from whoever happened to write the file.
  const apex = String(process.env.COOKIE_DOMAIN || '').trim().toLowerCase();
  if (!apex) return '';
  const host = (req.headers.host ?? '').split(':')[0].toLowerCase();
  return host === apex || host.endsWith('.' + apex) ? ` Domain=.${apex};` : '';
}

// JWT revocation list (in-memory, lost on restart). On logout we add the
// JWT's `jti` claim with its original expiry; verifyJWT rejects revoked
// tokens. Without this, "logout" only clears the cookie — a stolen token
// stays valid for the full 30-day TTL. Cleanup runs every 10 min: any
// entry whose `exp` has passed is dropped (the JWT itself would be
// rejected by the exp check anyway).
const revokedJtis = new Map<string, number>();
setInterval(() => {
  const now = Math.floor(Date.now() / 1000);
  const expiredJtis: string[] = [];
  for (const [jti, exp] of revokedJtis) {
    if (exp <= now) {
      revokedJtis.delete(jti);
      expiredJtis.push(jti);
    }
  }
  // v0.3.21: keep `revokedBySub` (declared below) in sync so a sub's
  // jti-list doesn't grow stale entries that were already swept here.
  if (expiredJtis.length > 0) {
    for (const [sub, list] of revokedBySub) {
      const filtered = list.filter((j) => !expiredJtis.includes(j));
      if (filtered.length === 0) revokedBySub.delete(sub);
      else if (filtered.length !== list.length) revokedBySub.set(sub, filtered);
    }
  }
}, 10 * 60 * 1000).unref();

// v0.3.13 audit R2-M-4: per-Discord-ID "logged out everywhere" timestamp
// (Unix seconds). When a user discovers compromise (or just wants to
// kick all open tabs across phones / laptops), POST /auth/logout-all
// sets `loggedOutAt[sub] = now`; verifyJWT rejects any JWT whose `iat`
// is older than that timestamp. Persisted to disk so a proxy restart
// doesn't quietly re-validate the attacker's stolen token. Pre-fix
// only the device-local cookie's JTI was revoked — every other open
// session stayed alive for the full 30-day TTL.
const LOGGED_OUT_AT_FILE = path.join(DATA_PATH, 'logged-out-at.json');
const loggedOutAt = new Map<string, number>();

/**
 * 🚨 THE KILL SWITCH IS WRITTEN BY ONE PROCESS AND READ BY THE OTHER.
 *
 * Both logout verbs live under `/auth/`, which the split gives to the GAME process — while every
 * `/api/` route (profile, points, cards, market, items, cosmetics, admin) runs on the WEB one, and
 * `verifyJWT` there consults ITS copy of this map. The map used to be filled once, at module load.
 *
 * So "log out everywhere" — the stolen-phone verb — wrote the file, updated the game process, and
 * left the process serving the entire portal still accepting the token it was told to kill.
 *
 * The irony is that this file exists because of the RESTART axis: the comment above says it is
 * persisted "so a proxy restart doesn't quietly re-validate the attacker's stolen token". The
 * durability was right; the split then broke the other axis, and the same file is the medium that
 * closes it.
 *
 * Re-read when the file MOVES, at most once a second. verifyJWT is the hot path — a stat per
 * request would be a syscall per request — and a logout taking up to a second to reach the other
 * process is not a meaningful window for a control whose alternative was "never".
 */
const LO_RECHECK_MS = 1000;
let _loStamp: string | null = null;
let _loCheckedAt = 0;

function loFileStamp(): string | null {
  try { const st = fs.statSync(LOGGED_OUT_AT_FILE); return `${st.mtimeMs}:${st.size}`; } catch { return null; }
}
function loadLoggedOutAt(): void {
  loggedOutAt.clear();
  try {
    const raw = fs.readFileSync(LOGGED_OUT_AT_FILE, 'utf8');
    const obj = JSON.parse(raw) as Record<string, unknown>;
    for (const [sub, ts] of Object.entries(obj)) {
      if (typeof ts === 'number' && Number.isFinite(ts)) loggedOutAt.set(sub, ts);
    }
  } catch { /* file missing on first boot — fine, start with empty map */ }
}
/** Pick up a logout recorded by the OTHER process. Cheap: one stat per second, not per request. */
function refreshLoggedOutAt(now = Date.now()): void {
  if (now - _loCheckedAt < LO_RECHECK_MS) return;
  _loCheckedAt = now;
  const stamp = loFileStamp();
  if (stamp === _loStamp) return;
  loadLoggedOutAt();
  _loStamp = stamp;
}
loadLoggedOutAt();
_loStamp = loFileStamp();
/** Test seam: force the next read to consult the file. Never called in production. */
export function _forceLoggedOutAtRecheck(): void { _loCheckedAt = 0; }
function persistLoggedOutAt(): void {
  try {
    fs.mkdirSync(path.dirname(LOGGED_OUT_AT_FILE), { recursive: true });
    const tmp = `${LOGGED_OUT_AT_FILE}.${process.pid}.${Date.now()}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(Object.fromEntries(loggedOutAt), null, 2), 'utf8');
    fs.renameSync(tmp, LOGGED_OUT_AT_FILE);
    // Our own write is not a foreign change (same reasoning as banRegistry.persist).
    _loStamp = loFileStamp();
    _loCheckedAt = Date.now();
  } catch (e) {
    console.error('[Auth] failed to persist logged-out-at:', (e as Error).message);
  }
}
// Periodic prune: drop entries older than the relevant cookie TTL +
// 1d safety margin — no JWT can have an `iat` that old, so the
// rejection wouldn't fire anyway.
//
// v0.3.23 audit fix: per-class TTL. Pre-fix ALL entries used the
// 30d Discord cookie cutoff; guest entries (`guest-<hex>` sub) thus
// persisted 31 days even though their cookie expires after 24h.
// On a busy proxy with high guest churn (every guest-logout adds an
// entry), the JSON file grew unboundedly between sweeps. Now guest
// subs use the 24h+1d budget so they age out within ~48h instead of
// 31d.
// 🚨 ONE OWNER, because this job WRITES THE WHOLE FILE. It ran on both processes, and the web one
// never reloaded — so once a day it could rewrite logged-out-at.json from its boot-time snapshot
// and erase every logout recorded since. A pruner that deletes expired entries is harmless; a
// pruner that persists a stale map is a kill switch being switched back off.
//
// OWNS_JOBS is the game process, which is also the only WRITER (both logout verbs are /auth/), so
// the owner and the writer are the same side by construction rather than by coincidence.
if (OWNS_JOBS) {
  setInterval(() => {
    refreshLoggedOutAt(Date.now() + LO_RECHECK_MS);   // never prune from a stale copy
    const now = Math.floor(Date.now() / 1000);
    const discordCutoff = now - COOKIE_MAX_AGE - 86400;
    const guestCutoff   = now - GUEST_COOKIE_MAX_AGE - 86400;
    let mutated = false;
    for (const [sub, ts] of loggedOutAt) {
      const cutoff = sub.startsWith('guest-') ? guestCutoff : discordCutoff;
      if (ts < cutoff) { loggedOutAt.delete(sub); mutated = true; }
    }
    if (mutated) persistLoggedOutAt();
  }, 24 * 60 * 60 * 1000).unref();
}

function b64url(buf: Buffer | string): string {
  const b = Buffer.isBuffer(buf) ? buf : Buffer.from(buf as string);
  return b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// Exported for LogoutCrossProcess.test.ts: the revocation cutoff is an auth PRIMITIVE, and the
// defect it covers lives between issuing and verifying. Same precedent as makeOAuthState below.
// Not a widened capability — the module boundary was never the security boundary; the secret is.
export function issueJWT(payload: Record<string, unknown>, ttlSeconds = COOKIE_MAX_AGE): string {
  const jti = crypto.randomBytes(12).toString('base64url');
  const now = Math.floor(Date.now() / 1000);
  const header = b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body   = b64url(JSON.stringify({
    ...payload,
    jti,
    iat: now, // v0.3.13 audit R2-M-4: logout-all compares iat to loggedOutAt[sub].
    exp: now + ttlSeconds,
  }));
  // Hot-rotatable signing key (jwtSecrets.ts) — env JWT_SECRET is the fallback.
  const sig    = b64url(crypto.createHmac('sha256', getSigningSecret()).update(`${header}.${body}`).digest());
  return `${header}.${body}.${sig}`;
}

export function verifyJWT(token: string): Record<string, unknown> | null {
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  const [header, body, sig] = parts;

  // Belt-and-braces alg/typ check before HMAC. HMAC verify alone is enough
  // to reject `alg: none` (the signature wouldn't match) and `alg: HS512`
  // forgeries (32 vs 64 byte length mismatch), but explicitly asserting
  // the header shape closes any future class of confusion attacks early.
  try {
    const headerJson = JSON.parse(Buffer.from(header, 'base64url').toString('utf8')) as Record<string, unknown>;
    if (headerJson.alg !== 'HS256' || headerJson.typ !== 'JWT') return null;
  } catch { return null; }

  // HS256 produces a fixed 32-byte digest → 43 chars in b64url. Reject any
  // signature whose decoded length isn't 32 — defends against truncation
  // attacks if the scheme ever changes to a longer hash.
  // Dual-key verify (jwtSecrets.ts): the current signing secret always, plus
  // the pre-rotation one within its 24h grace window — a rotation never cuts
  // live sessions. Each candidate gets the same constant-time compare.
  let matched = false;
  try {
    const sigBuf = Buffer.from(sig, 'base64url');
    for (const secret of getVerifySecrets()) {
      const expBuf = crypto.createHmac('sha256', secret).update(`${header}.${body}`).digest();
      if (sigBuf.length === expBuf.length && crypto.timingSafeEqual(sigBuf, expBuf)) { matched = true; break; }
    }
  } catch { return null; }
  if (!matched) return null;
  try {
    const raw = Buffer.from(body, 'base64url').toString('utf8');
    const payload = JSON.parse(raw) as Record<string, unknown>;
    // v0.3.21 audit fix: strict-type guard on exp/iat. Pre-fix, a
    // forged JWT with `"exp": "99999999999"` (string) would skip the
    // expiry check entirely because `typeof string !== 'number'`.
    // Same for `iat`. Now require both to be finite numbers — a
    // malformed JWT that lacks either OR ships them as strings is
    // rejected outright.
    if (payload.exp === undefined || typeof payload.exp !== 'number' || !Number.isFinite(payload.exp)) return null;
    if (payload.exp < Math.floor(Date.now() / 1000)) return null;
    if (payload.iat !== undefined && (typeof payload.iat !== 'number' || !Number.isFinite(payload.iat))) return null;
    // v0.9.132 (audit 2026-06-21): global token epoch — a HARD JWT rotation stamps
    // a min-iat floor so EVERY token issued before it is rejected platform-wide
    // (the kill switch the per-sub loggedOutAt map can't give). A missing iat is
    // treated as 0 → rejected once a floor is set, which is correct: any token old
    // enough to lack iat predates the floor. Cheap: getMinIssuedAt is memoised 5s.
    const minIat = getMinIssuedAt();
    if (minIat > 0) {
      const iat = typeof payload.iat === 'number' ? payload.iat : 0;
      if (iat < minIat) return null;
    }
    if (typeof payload.jti === 'string' && revokedJtis.has(payload.jti)) return null;
    // v0.3.13 audit R2-M-4 + R3-M-2: logout-everywhere check. If the user
    // has ever called POST /auth/logout-all, every JWT they had at that
    // moment is rejected. Round 3 fix: treat a MISSING `iat` as 0 (not
    // skip-the-check), so JWTs minted before this build deployed (which
    // lack the `iat` field but are still within their 30-day exp) ALSO
    // get killed by logout-all. Otherwise a user reacting to a phone-
    // theft scenario right after a deploy would falsely believe their
    // pre-deploy tokens were revoked when they weren't.
    if (typeof payload.sub === 'string') {
      // Consult the file if it has moved since we last looked — the logout may have been recorded
      // by the OTHER proxy process, which is the only one that serves /auth/. See above.
      refreshLoggedOutAt();
      const lo = loggedOutAt.get(payload.sub);
      if (lo !== undefined) {
        const iat = typeof payload.iat === 'number' ? payload.iat : 0;
        if (iat < lo) return null;
      }
    }
    return payload;
  } catch { return null; }
}

// v0.3.13 audit R3-M-1: must verify the JWT's HMAC before inserting into
// revokedJtis. Pre-fix `revokeJWT` parsed the body without verifying the
// signature — a hostile caller could POST /auth/logout with a forged
// token whose `jti` is a 64KB random string and `exp = MAX_SAFE_INTEGER`,
// growing the in-memory map unbounded (the 10-min sweep only deletes
// entries whose exp has already passed). With verify-first the attacker
// can only revoke JWTs they actually possess.
//
// Defence-in-depth: cap revokedJtis.size with per-sub LRU eviction.
// v0.3.21 audit fix: pre-fix used a global FIFO cap (10000 entries) —
// a single attacker holding 10001+ valid JWTs of their own could spam
// /auth/logout to flush every other user's revocation by FIFO age. New
// scheme: cap entries-per-sub at 32, plus a global ceiling of 50000 to
// bound total memory under a hostile-mass-logout-then-issue scenario.
// Total worst-case memory: 50k jtis × ~80 bytes ≈ 4 MB.
const REVOKED_JTIS_MAX = 50_000;
const REVOKED_JTIS_PER_SUB = 32;
const revokedBySub = new Map<string, string[]>(); // sub -> ordered list of jtis
function revokeJWT(token: string): void {
  const payload = verifyJWT(token);
  if (!payload) return;
  if (typeof payload.jti === 'string' && typeof payload.exp === 'number'
      && typeof payload.sub === 'string') {
    // Evict this sub's oldest jti if they're at the per-sub cap.
    const list = revokedBySub.get(payload.sub) ?? [];
    if (list.length >= REVOKED_JTIS_PER_SUB) {
      const oldest = list.shift();
      if (oldest !== undefined) revokedJtis.delete(oldest);
    }
    list.push(payload.jti);
    revokedBySub.set(payload.sub, list);
    // Also enforce a global ceiling — drops the absolute oldest entry
    // (insertion-order via revokedJtis Map). Belt-and-braces against a
    // hostile process spawning a million distinct subs (impossible with
    // Discord OAuth, plausible with a guest-mint flood pre-rate-limit).
    if (revokedJtis.size >= REVOKED_JTIS_MAX) {
      const oldest = revokedJtis.keys().next().value;
      if (oldest !== undefined) revokedJtis.delete(oldest);
    }
    revokedJtis.set(payload.jti, payload.exp);
  }
}

/** Extract and verify the session JWT from a request's cookie or Authorization header.
 *  Returns the decoded payload on success, null if missing or invalid.
 *  Used by admin endpoints to gate access without re-implementing the JWT logic.
 *
 *  Accepts both Express `Request` (from /api/* handlers) and Node's bare
 *  `http.IncomingMessage` (from the WS upgrade pre-handshake hook in
 *  UOProxy.verifyClient) — Express Request extends IncomingMessage, and we
 *  only read the two header fields both support. v0.3.14 widening: prior
 *  signature locked us out of the upgrade path. */
export function verifyRequestJwt(req: { headers: Record<string, unknown> }): Record<string, unknown> | null {
  const cookieRaw = req.headers['cookie'];
  const cookieHeader = typeof cookieRaw === 'string' ? cookieRaw : '';
  const cookieToken  = cookieHeader.match(/(?:^|;\s*)__session=([^;]+)/)?.[1] ?? null;
  const authRaw = req.headers['authorization'];
  const authHeader = typeof authRaw === 'string' ? authRaw : null;
  const bearerToken  = authHeader?.startsWith('Bearer ') ? authHeader.slice(7) : null;
  const token = bearerToken ?? cookieToken;
  if (!token) return null;
  return verifyJWT(token);
}

// ── CSRF: Origin / Referer allow-list ────────────────────────────────────────
//
// Defends state-changing routes (PUT /api/settings, PUT /api/profile,
// POST /auth/logout) against forged cross-site form submissions and
// SameSite=Lax edge cases. The check is cheap and idempotent.
//
// Allow-list source:
//   - PUBLIC_ORIGINS env (comma-separated) when set — explicit list.
//   - The request's own Host header (same-origin) otherwise — keeps local
//     dev working without configuration.

export function requireSafeOrigin(req: Request, res: Response): boolean {
  // Audit R2-M-4: only honour Origin (set by all modern browsers on POST/PUT
  // and on WS upgrades). Dropping the Referer fallback eliminates a class of
  // bypasses where a browser strips Origin but still sends Referer; missing
  // Origin is a strong signal of a non-browser caller for state-changing
  // routes, which we want to reject.
  const origin = req.headers.origin;
  if (typeof origin !== 'string' || !origin) {
    res.status(403).json({ error: 'missing Origin header' });
    return false;
  }
  // Audit R2-M-3: case-insensitive compare. Browsers always lowercase Origin
  // scheme + host, but Host (and X-Forwarded-Host) preserve client-supplied
  // case. nginx's `$http_host` forwards as-sent.
  const originLc = origin.toLowerCase();

  // Explicit allow-list takes precedence — preferred path in production.
  // getPublicOrigins() reads the hot-reloadable runtime config (file → env),
  // so adding a domain doesn't need a proxy restart.
  for (const allowed of getPublicOrigins()) if (allowed.toLowerCase() === originLc) return true;

  // Host-header fallback for dev / single-tenant deploys without
  // PUBLIC_ORIGINS. In production REFUSE this fallback: an attacker who
  // can reach the proxy directly (e.g. it's exposed on 0.0.0.0 with no
  // nginx in front) could spoof Host: attacker.com to bypass the check.
  // The config-time guard logs a warning when PUBLIC_ORIGINS is empty in
  // production; here we make the bypass non-functional.
  if (!IS_PRODUCTION) {
    const xfh = req.headers['x-forwarded-host'];
    const fwdHost = (typeof xfh === 'string' && xfh) ? xfh.split(',')[0].trim() : null;
    const host = (fwdHost ?? req.headers.host ?? '').toLowerCase();
    if (host && (originLc === `http://${host}` || originLc === `https://${host}`)) return true;
  }

  res.status(403).json({ error: `origin not allowed: ${origin}` });
  return false;
}

// ── Cookie / auth helpers ─────────────────────────────────────────────────────

function parseCookie(req: Request): string | undefined {
  const header = req.headers.cookie ?? '';
  for (const part of header.split(';')) {
    const idx = part.indexOf('=');
    if (idx === -1) continue;
    if (part.slice(0, idx).trim() === COOKIE_NAME) return part.slice(idx + 1).trim();
  }
  return undefined;
}

export function currentUser(req: Request): { sub: string; name: string; avatar?: string } | null {
  const token = parseCookie(req);
  if (!token) return null;
  const payload = verifyJWT(token);
  if (!payload || typeof payload.sub !== 'string' || typeof payload.name !== 'string') return null;
  return { sub: payload.sub, name: payload.name, avatar: typeof payload.avatar === 'string' ? payload.avatar : undefined };
}

// ── Settings storage ──────────────────────────────────────────────────────────

// SECURITY (2026-06-08): 'wsHost' removed — persisting a client-supplied WS host
// was a stored-MITM vector (a crafted ?wsHost=evil.com link, auto-saved here on
// pagehide, redirected the credential-carrying game WebSocket to an attacker
// host on every future load). The client now locks the WS host to its own
// origin (location.hostname); dropping it from the allow-list also stops the
// proxy from EVER storing it, so a stale/forged value can't persist.
const ALLOWED_KEYS = new Set(['wsPort', 'wsPath', 'encrypt']);
// Large per-user blobs (JSON strings) the rail UI persists to the Discord
// account so they travel across browsers/devices: the JS-macro scripts and
// (later) the agents config. They far exceed the 256-char small-setting cap,
// so they get their own larger ceiling. Values are opaque JSON strings the
// rail writes/reads verbatim — the server never parses them.
// 🚨 `railLScripts` was missing here until 2026-08-12, and the consequence was silent, total data
// loss. The rail grew a second scripting language (LegionScript) and persists it under its own
// key; this list did not follow. A PUT carrying it returned 200 and the server DROPPED it — the
// client had no way to tell, so LegionScript work lived only in that browser's localStorage and
// clearing site data destroyed it for good. Reported as "I cleared the cache and my macros are
// gone", which is exactly what it looked like from the outside.
// Anything the rail persists must appear here or it is not stored. See the cross-layer gate in
// server/test/PersistedKeysAreAcceptedByServer.test.ts.
const LARGE_KEYS = new Set(['railScripts', 'railLScripts', 'railAgents']);
const LARGE_KEY_MAX_BYTES = 128 * 1024;

// Discord IDs are 17–19 numeric digits today. The 64-char slice cannot
// produce a collision under that format; the L-2 audit finding is kept
// open as a documented risk in case Discord ever changes the `sub`
// shape. Migrating now would orphan existing user files.
//
// Both settings + profile paths flow through the same helper so the
// empty-string guard is symmetric (R2-L-3 — previously profilePath
// lacked the guard that settingsPath had).
function safeUserId(userId: string): string {
  const safe = userId.replace(/[^a-zA-Z0-9_-]/g, '').slice(0, 64);
  if (!safe) throw new Error('empty user id');
  return safe;
}

function settingsPath(userId: string): string {
  return path.join(DATA_PATH, `${safeUserId(userId)}.json`);
}

async function loadSettings(userId: string): Promise<Record<string, string>> {
  try {
    const raw = await fs.promises.readFile(settingsPath(userId), 'utf8');
    return JSON.parse(raw) as Record<string, string>;
  } catch { return {}; }
}

async function persistSettings(userId: string, data: unknown): Promise<void> {
  if (typeof data !== 'object' || data === null) return;
  const clean: Record<string, string> = {};
  for (const [k, v] of Object.entries(data as Record<string, unknown>)) {
    if (typeof v !== 'string') continue;
    if (ALLOWED_KEYS.has(k) && v.length < 256) {
      clean[k] = v;
    } else if (LARGE_KEYS.has(k) && v.length <= LARGE_KEY_MAX_BYTES) {
      clean[k] = v;
    }
  }

  // 🚨 MERGE, never replace. This wrote `clean` straight over the file, so a PUT that carried only
  // some keys DELETED the rest — and the client makes exactly that call: discordSaveSettings()
  // builds its payload from `{}` and adds only wsPort/wsPath/encrypt, then PUTs. Picking a shard,
  // or simply closing the tab, therefore erased the player's saved scripts and agents. Measured
  // 2026-08-12: a settings file holding a LegionScript script became 2 bytes (`{}`) during an
  // ordinary session, with no crash and nothing in any log.
  //
  // The irony is that the comment below already described this damage — and blamed a TORN FILE.
  // The torn-file path is real and the atomic write fixes it, but the everyday path did the same
  // thing with no corruption involved at all, which is why it went unnoticed for so long: the
  // failure looked like the thing that was already thought to be handled.
  //
  // Absent means "not sent", not "delete me". Nothing in this API expresses removal, so treating
  // omission as deletion could only ever destroy data nobody asked to lose.
  //
  // 🚨 The read-merge-write runs INSIDE the file lock, and the read happens in there too. A merge
  // whose read sits outside the lock still loses a key: two saves overlapping — a pagehide flush
  // against a script save is the everyday pair — both read the old file and the second write wins.
  // The lock is best-effort (same as runtimeConfig), so what actually carries the guarantee is
  // re-reading in the critical section, not the lock itself. Sync inside, because withFileLockSync
  // cannot hold a lock across an await.
  await fs.promises.mkdir(DATA_PATH, { recursive: true });
  withFileLockSync(settingsPath(userId), () => {
    let existing: Record<string, string> = {};
    try {
      existing = JSON.parse(fs.readFileSync(settingsPath(userId), 'utf8')) as Record<string, string>;
    } catch { /* missing or torn: start from nothing, exactly as loadSettings does */ }
    const merged: Record<string, string> = { ...existing, ...clean };
    writeFileAtomic(settingsPath(userId), JSON.stringify(merged, null, 2));
  });
}


// ── Profile blob storage ─────────────────────────────────────────────────────
//
// The full CUO `/Data/Profiles/<server>/<account>/<char>/` tree (profile.json,
// gumps.xml, macros.xml, skills.xml, cooldowns.xml…) packed by the client as a
// single tar.gz blob, stored verbatim on disk under DATA_PATH/profiles/.
//
// Why opaque blob: we don't validate the inner files. CUO is the only writer;
// the server's job is durable storage tied to the Discord account so the
// profile travels across browsers/devices.

const PROFILE_DIR = 'profiles';
const PROFILE_MAX_BYTES = 10 * 1024 * 1024;          // gzipped size cap (PUT body)
const PROFILE_MAX_INFLATED_BYTES = 20 * 1024 * 1024; // hard cap on tar size; bombs reject
const PROFILE_PER_FILE_MAX_BYTES = 256 * 1024;       // single-file size cap inside tar
// Strict per-basename whitelist — user directive 2026-04-27. Anything
// other than these five files is rejected (400). The client also
// filters by exact basename before packing, plus prunes /Data of
// non-whitelisted entries before every IDBFS sync, so this server-
// side check exists to defend against a hand-crafted PUT from a
// hijacked / malicious client.
const PROFILE_ALLOWED_FILES = new Set([
  'gumps.xml',
  'infobar.xml',
  'macros.xml',
  'profile.json',
  'skillsgroups.xml',
]);

// Cloud profiles are keyed per CLIENT as well as per Discord id: a ClassicUO
// profile is not a TazUO profile, so each bundle stores its own blob
// (`<id>.cuo.tar.gz` / `<id>.tuo.tar.gz`). `client` is an untrusted query param
// — validate to the known bundles; anything else falls back to the legacy
// single-blob name (`<id>.tar.gz`) so old installs keep working.
// One source of truth for the per-client profile-blob suffix list. EVERY site
// that names a client blob (write suffix / admin list regex / per-user delete)
// derives from this, so adding a 4th client is a one-line edit here instead of
// 3 scattered literals. (Modularity audit 2026-06-27 D2: `mini` was added to the
// write suffix but NOT to deleteProfilesForUser/listProfiles → a Discord mini
// user's `<sub>.mini.tar.gz` was written but never deleted by GDPR self-erasure —
// a live data-retention bug. Driving all three from this const closes it.)
const PROFILE_CLIENTS: readonly string[] = ['cuo', 'tuo', 'mini'];
// Parse a profile blob filename → (id, client), built from PROFILE_CLIENTS so a
// new client is auto-recognised by listProfiles AND deleteProfilesForUser.
const PROFILE_NAME_RE = new RegExp(`^(.+?)(?:\\.(${PROFILE_CLIENTS.join('|')}))?\\.tar\\.gz$`);
function profileClientSuffix(client?: string): string {
  return (client && PROFILE_CLIENTS.includes(client)) ? `.${client}` : '';
}
function profilePath(userId: string, client?: string): string {
  return path.join(DATA_PATH, PROFILE_DIR, `${safeUserId(userId)}${profileClientSuffix(client)}.tar.gz`);
}

// ── Identity name store (sub → Discord display name) — SQLite (db.ts) ────────
// The admin panel needs a human name next to the opaque Discord-id profile blobs.
// We capture the JWT display name on each /api/me into the `identities` table and
// join it in listProfiles. Guests are skipped. Skip-if-unchanged avoids a write on
// every /api/me. SQLite scales — the old A6 JSON size-cap is no longer needed.
migrateJsonOnce(path.join(DATA_PATH, 'identities.json'), (raw) => {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return;
  const ins = db.prepare('INSERT OR IGNORE INTO identities(sub, name, avatar, seen) VALUES(?, ?, ?, ?)');
  for (const [sub, rec] of Object.entries(raw as Record<string, unknown>)) {
    const r = (rec && typeof rec === 'object') ? rec as { name?: unknown; avatar?: unknown; seen?: unknown } : {};
    if (typeof r.name !== 'string' || !r.name) continue;
    ins.run(sub, r.name.slice(0, 64), typeof r.avatar === 'string' ? r.avatar : null, Number(r.seen) || Date.now());
  }
});
// COALESCE on avatar: a login/refresh whose JWT lacks the avatar hash (older token)
// must NEVER wipe a previously-stored avatar — keep the existing one when the incoming
// is null (fixes the rail/menu showing the default embed avatar, operator 2026-06-18).
const qUpsertIdent = db.prepare('INSERT INTO identities(sub, name, avatar, seen) VALUES(?, ?, ?, ?) ON CONFLICT(sub) DO UPDATE SET name = excluded.name, avatar = COALESCE(excluded.avatar, identities.avatar), seen = excluded.seen');
const qTouchSeen = db.prepare('UPDATE identities SET seen = ? WHERE sub = ?');
const qGetIdentName = db.prepare('SELECT name FROM identities WHERE sub = ?');
const qGetIdentAvatar = db.prepare('SELECT avatar FROM identities WHERE sub = ?');
export function recordIdentity(sub: string, name: string, avatar?: string): void {
  if (!sub || sub.startsWith('guest-') || sub === '__proto__' || sub === 'constructor' || sub === 'prototype' || !name) return;
  // Whatever this build wants done when a Discord user identifies themselves. On uonexus that is
  // two things, registered together in index.ts: give them a public nickname for the rankings
  // (random by default, editable once, idempotent), and push their character to every configured
  // shard bridge -- signing in is enough to have a character (operator 2026-07-30: "solo por hacer
  // login de discord en la web ya te debe crear automáticamente el personaje en el servidor de
  // minigames").
  //
  // Hooked HERE because this is the one place that means "a Discord user just identified
  // themselves": it covers a first login and a returning one alike, which matters for accounts
  // that predate the character push and have none yet. Swallowed, because a game server being
  // down must not break signing in.
  try { _onIdentity?.(sub); } catch { /* never let this affect a login */ }
  const nm = String(name).slice(0, 64);
  const prev = qGetIdentName.get(sub) as { name: string } | undefined;
  // v0.8.43: ALWAYS refresh `seen` — the inactive-owner reaper relies on it as
  // a real last-login timestamp. Pre-fix the name-unchanged fast-path skipped
  // the write entirely, so `seen` only moved when the Discord name changed.
  if (prev && prev.name === nm) { qTouchSeen.run(Date.now(), sub); return; }
  qUpsertIdent.run(sub, nm, avatar ?? null, Date.now());
}
export function getIdentityName(sub: string): string | null {
  const r = qGetIdentName.get(sub) as { name: string } | undefined;
  return r ? r.name : null;
}
/** Stored Discord avatar hash for `sub`, or null. Lets /api/me serve the avatar even
 *  when the caller's JWT predates avatar-in-token (recorded on every login). */
export function getIdentityAvatar(sub: string): string | null {
  if (!sub || sub.startsWith('guest-')) return null;
  const r = qGetIdentAvatar.get(sub) as { avatar: string | null } | undefined;
  return r && typeof r.avatar === 'string' ? r.avatar : null;
}
// v0.8.44: seed `seen` for a sub that may have NO identity row yet (e.g. an
// owner the operator just granted a shard to, who has never logged into the
// web). Without this, getIdentitySeen→null = "inactive forever" and the reaper
// would queue their brand-new shard at the first tick. Name is NOT NULL, so we
// insert '' as a placeholder — the real Discord name overwrites it on first
// login (qUpsertIdent ON CONFLICT updates name). Never clobbers an existing
// name/avatar; only moves `seen` forward.
const qSeedSeen = db.prepare("INSERT INTO identities(sub, name, avatar, seen) VALUES(?, '', NULL, ?) ON CONFLICT(sub) DO UPDATE SET seen = excluded.seen");
export function touchIdentitySeen(sub: string): void {
  if (!sub || sub.startsWith('guest-') || sub === '__proto__' || sub === 'constructor' || sub === 'prototype') return;
  try { qSeedSeen.run(sub, Date.now()); } catch { /* best-effort */ }
}
const qGetIdentSeen = db.prepare('SELECT seen FROM identities WHERE sub = ?');
/** Last time this Discord user was seen (ms epoch), or null if never. Drives
 *  the inactive-owner shard reaper. `seen` is refreshed on every /api/me. */
export function getIdentitySeen(sub: string): number | null {
  if (!sub || sub.startsWith('guest-')) return null;
  const r = qGetIdentSeen.get(sub) as { seen: number } | undefined;
  return r ? Number(r.seen) : null;
}

// ── Admin profile management (supreme-admin only — wired in AssetServer) ──────
// Cloud profiles are <safeUserId>[.cuo|.tuo].tar.gz blobs keyed by Discord id;
// there is NO id→name map on disk (the blobs are opaque), so "delete by user"
// means by Discord id. listProfiles powers the admin view; deleteProfilesForUser
// removes every per-client blob for one id; purgeAllProfiles wipes them all.
// Paged + searchable so the admin view scales to thousands of blobs: filtering is
// done on the parsed names WITHOUT stat(), and only the returned page is stat()ed
// for size. Returns { profiles, total } where total is the full match count.
export function listProfiles(opts?: { search?: string; offset?: number; limit?: number }):
    { profiles: Array<{ id: string; client: string; bytes: number; mtime: number; name: string | null; isGuest: boolean }>; total: number } {
  const dir = path.join(DATA_PATH, PROFILE_DIR);
  let names: string[];
  try { names = fs.readdirSync(dir); } catch { return { profiles: [], total: 0 }; }
  const idents = new Map<string, string>();
  for (const r of db.prepare('SELECT sub, name FROM identities').all() as Array<{ sub: string; name: string }>) idents.set(r.sub, r.name);
  let entries: Array<{ id: string; client: string; file: string; dname: string | null; isGuest: boolean }> = [];
  for (const fname of names) {
    const m = fname.match(PROFILE_NAME_RE);
    if (!m) continue;
    const id = m[1];
    const isGuest = id.startsWith('guest-');
    const dname = (!isGuest && idents.has(id)) ? (idents.get(id) ?? null) : null;
    entries.push({ id, client: m[2] || 'legacy', file: fname, dname, isGuest });
  }
  const search = (opts?.search || '').trim().toLowerCase();
  // Search matches the id OR the captured Discord name, so an admin can find a user
  // by name (not just the opaque id). Done before stat() so it still scales.
  if (search) entries = entries.filter((e) => e.id.toLowerCase().includes(search) || (e.dname != null && e.dname.toLowerCase().includes(search)));
  entries.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : a.client < b.client ? -1 : 1));
  const total = entries.length;
  const offset = Math.max(0, opts?.offset || 0);
  const limit = Math.min(200, Math.max(1, opts?.limit || 50));
  const profiles = entries.slice(offset, offset + limit).map((e) => {
    let bytes = 0, mtime = 0;
    try { const st = fs.statSync(path.join(dir, e.file)); bytes = st.size; mtime = st.mtimeMs; } catch { /* race */ }
    return { id: e.id, client: e.client, bytes, mtime, name: e.dname, isGuest: e.isGuest };
  });
  return { profiles, total };
}

// Accepts EITHER the Discord id (the on-disk blob key) OR a Discord display name
// (operator 2026-06-19: "quiero pasarle el nombre de discord, no el id"). Blobs
// are keyed by id, so a name is resolved to its sub(s) via the identities table
// first. Exact match only (never prefix/substring) so deleting "123" can't take
// out "1234". Returns the number of blob files removed across all matched users.
export function deleteProfilesForUser(idOrName: string): number {
  const dir = path.join(DATA_PATH, PROFILE_DIR);
  let names: string[];
  try { names = fs.readdirSync(dir); } catch { return 0; }
  const delFor = (key: string): number => {
    const safe = safeUserId(key);
    if (!safe) return 0;
    let c = 0;
    // Legacy single-blob `<id>.tar.gz` + every per-client `<id>.<client>.tar.gz`
    // (driven by PROFILE_CLIENTS so a new client is covered automatically — this
    // is the line that was missing `.mini` and caused the GDPR-delete gap).
    const targets = new Set([`${safe}.tar.gz`, ...PROFILE_CLIENTS.map((cl) => `${safe}.${cl}.tar.gz`)]);
    for (const name of names) {
      if (targets.has(name)) {
        try { fs.unlinkSync(path.join(dir, name)); c++; } catch { /* race */ }
      }
    }
    return c;
  };
  // 1. Try the value as an id (the blob key) directly.
  let n = 0;
  try { n = delFor(idOrName); } catch { /* */ }
  if (n > 0) return n;
  // 2. Otherwise resolve it as a Discord display name → its sub(s) and delete those.
  const want = idOrName.trim().toLowerCase();
  if (!want) return 0;
  try {
    const subs = (db.prepare('SELECT sub FROM identities WHERE lower(name) = ? AND sub NOT LIKE \'guest-%\'').all(want) as Array<{ sub: string }>).map((r) => r.sub);
    for (const sub of subs) n += delFor(sub);
  } catch { /* db unavailable */ }
  return n;
}

export function purgeAllProfiles(): number {
  const dir = path.join(DATA_PATH, PROFILE_DIR);
  let names: string[];
  try { names = fs.readdirSync(dir); } catch { return 0; }
  let n = 0;
  for (const name of names) {
    if (name.endsWith('.tar.gz')) {
      try { fs.unlinkSync(path.join(dir, name)); n++; } catch { /* race */ }
    }
  }
  return n;
}

async function loadProfile(userId: string, client?: string): Promise<Buffer | null> {
  try {
    return await fs.promises.readFile(profilePath(userId, client));
  } catch {
    // Migration: the legacy `<id>.tar.gz` was written by the root (`/`) client,
    // i.e. ClassicUO. Fall back to it ONLY for client=cuo so an existing CUO
    // user keeps continuity; TUO deliberately does NOT inherit it (a ClassicUO
    // profile is not a TazUO profile — TUO starts clean rather than importing
    // CUO settings). Subsequent uploads write the per-client blob and diverge.
    if (client === 'cuo') {
      try { return await fs.promises.readFile(profilePath(userId)); } catch { /* none */ }
    }
    return null;
  }
}

// Walk a POSIX ustar tar buffer; returns descriptive error or null.
//
// Hardening (audit findings H-6):
//   1. Verify ustar magic at offset 257-263 — rejects pre-POSIX `tar` (V7),
//      cpio dumps, and arbitrary garbage that happens to have a number at
//      offset 124.
//   2. Verify the header checksum at offset 148-156 — defends against a
//      crafted oversize header that lies about its size to skip past a
//      hidden second entry.
//   3. Bound `off + size + padding` against `tar.length` so a header
//      claiming `size = MAX` can't desync the parser into reading past EOF.
//   4. Explicitly reject GNU long-name (L), GNU long-link (K), PAX
//      extended-header (x) and PAX global-header (g) typeflags. They
//      smuggle path information in side-band entries that our basename
//      whitelist would not see.
//   5. Reject any non-NUL bytes in the name/prefix/sizeStr regions past
//      the first NUL — old tar implementations stuffed metadata there.
// v0.3.13 audit R2-H-1: tar entry filenames flow into error messages
// that the proxy logs as `[Auth] /api/profile rejected for sub=...: <detail>`.
// Without sanitisation an authenticated user could inject CR/LF + ANSI
// escapes via crafted tar headers (the spec only fences \0; \r\n is fair
// game), forging log lines like `... rejected\r\n[Admin] User authorized`.
// R3-L-1: also strip Unicode bidi / format chars (U+202E "RTLO" etc) —
// they don't break newlines but visually re-order text in operator
// terminals (`rejected: foo.txt` renders as `rejected: txt.foo`), an
// alternative log-spoofing primitive. The Unicode general-category Cf
// covers all relevant codepoints (`\p{Cf}` requires the `u` flag).
function sanitizeForLog(s: string): string {
  return s
    .replace(/[\x00-\x1f\x7f]/g, '?')
    .replace(/\p{Cf}/gu, '?')
    .slice(0, 256);
}

/**
 * Exported ONLY so ProfileTarValidator.test.ts can reach it.
 *
 * 🚨 It is ~70 lines of security-critical parsing that had no test at all. What makes it worth
 * pinning is that it is FAIL-CLOSED: it accepts a plain file and a directory and rejects every
 * other typeflag, including symlinks and the GNU long-name / PAX entries that are the classic way
 * to smuggle a path past a name check. Fail-closed is exactly the shape a tidy-up turns into
 * fail-open — "the else branch never fires, drop it" — and nothing would have noticed.
 */
export function validateTar(tar: Buffer): string | null {
  if (tar.length < 1024) return 'tar too short';
  let off = 0;
  let totalEntries = 0;
  while (off + 512 <= tar.length) {
    // EOF marker is two consecutive zero blocks; checking the first byte is
    // enough — a real header has a path char at offset 0.
    if (tar[off] === 0) break;

    // (1) ustar magic at offset 257-263. The string is "ustar" + NUL.
    if (tar.subarray(off + 257, off + 262).toString('ascii') !== 'ustar') {
      return `not a ustar header at offset ${off}`;
    }

    // (2) header checksum: stored as 6 octal digits + NUL + space at 148-156.
    // The checksum is the unsigned sum of all 512 header bytes treating
    // the checksum field itself as 8 spaces. Both the GNU "signed" variant
    // and the standard unsigned form should match — we compute unsigned.
    const csStr = tar.subarray(off + 148, off + 156).toString('ascii').replace(/[\0 ].*$/, '');
    const claimedCs = parseInt(csStr, 8);
    if (!Number.isFinite(claimedCs)) return `bad checksum at offset ${off}`;
    let computedCs = 0;
    for (let i = 0; i < 512; i++) {
      computedCs += (i >= 148 && i < 156) ? 0x20 : tar[off + i];
    }
    if (computedCs !== claimedCs) return `checksum mismatch at offset ${off}`;

    const name = tar.subarray(off, off + 100).toString('utf8').replace(/\0.*$/, '');
    const prefix = tar.subarray(off + 345, off + 500).toString('utf8').replace(/\0.*$/, '');
    const sizeStr = tar.subarray(off + 124, off + 136).toString('ascii').replace(/\0.*$/, '').trim();
    const size = parseInt(sizeStr, 8);
    if (!Number.isFinite(size) || size < 0) return `bad size at offset ${off}`;
    if (size > PROFILE_MAX_INFLATED_BYTES) return `size larger than tar (${size}) at offset ${off}`;
    const typeflag = tar[off + 156];
    const fullName = prefix ? `${prefix}/${name}` : name;

    // (3) bound check: the entry payload must fit within the buffer.
    const padding = size % 512 ? 512 - (size % 512) : 0;
    if (off + 512 + size + padding > tar.length) {
      return `entry runs past tar end at offset ${off}`;
    }

    off += 512;
    if (typeflag === 0x30 || typeflag === 0) {
      if (!fullName) return 'empty path';
      if (fullName.includes('..') || fullName.startsWith('/')) return `path escape: ${sanitizeForLog(fullName)}`;
      if (fullName.includes('\0')) return 'NUL byte in path';
      if (!fullName.startsWith('Profiles/')) return `path outside Profiles/: ${sanitizeForLog(fullName)}`;
      const slash = fullName.lastIndexOf('/');
      const base = slash >= 0 ? fullName.substring(slash + 1) : fullName;
      if (!PROFILE_ALLOWED_FILES.has(base)) return `disallowed filename: ${sanitizeForLog(fullName)}`;
      if (size > PROFILE_PER_FILE_MAX_BYTES) return `file too large (${size} B): ${sanitizeForLog(fullName)}`;
      totalEntries++;
      if (totalEntries > 256) return 'too many entries';
    } else if (typeflag === 0x35) {
      // directory entry — accept silently
    } else if (
      typeflag === 0x4C /* L: GNU long-name */ ||
      typeflag === 0x4B /* K: GNU long-link */ ||
      typeflag === 0x78 /* x: PAX extended */ ||
      typeflag === 0x67 /* g: PAX global   */
    ) {
      return `extension typeflag '${String.fromCharCode(typeflag)}' not allowed (long names + PAX entries)`;
    } else {
      return `disallowed typeflag 0x${typeflag.toString(16)} for ${sanitizeForLog(fullName)}`;
    }
    off += size + padding;
  }
  return null;
}

async function persistProfile(userId: string, data: Buffer, client?: string): Promise<void> {
  // Guests get NO cloud-save (audit 2026-06-22 #3): a client-chosen guestId is a distinct filename, so
  // an attacker could write an unbounded pile of guest-<hex>.tar.gz blobs (no per-account cap, no reaper,
  // guests can't self-delete) → disk-exhaustion DoS. Cloud-save is Discord-only by product intent
  // (canRegister / account-delete already gate guests out); guests keep full game + asset access.
  if (userId.startsWith('guest-')) return;
  if (!Buffer.isBuffer(data) || data.length === 0) return;
  if (data.length > PROFILE_MAX_BYTES) throw new Error(`payload exceeds ${PROFILE_MAX_BYTES} bytes`);
  // Decompress with a hard cap to defeat zip-bombs, then inspect
  // every tar entry. If any entry is non-config or escapes Profiles/,
  // reject the whole upload — never store an attacker-shaped blob.
  let inflated: Buffer;
  try {
    inflated = zlib.gunzipSync(data, { maxOutputLength: PROFILE_MAX_INFLATED_BYTES });
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : 'gunzip failed';
    throw new Error(`gunzip: ${msg}`);
  }
  const violation = validateTar(inflated);
  if (violation) throw new Error(`tar validation: ${violation}`);
  await fs.promises.mkdir(path.join(DATA_PATH, PROFILE_DIR), { recursive: true });
  // Atomic write: drop a temp file in the same directory, then rename
  // over the destination. fs.rename is atomic on the same filesystem,
  // so concurrent PUTs from the same Discord ID end with one of them
  // winning entirely — no torn writes, no corrupted half-state.
  const finalPath = profilePath(userId, client);
  const tmpPath = `${finalPath}.${process.pid}.${Date.now()}.tmp`;
  try {
    await fs.promises.writeFile(tmpPath, data);
    await fs.promises.rename(tmpPath, finalPath);
  } catch (err) {
    // Best-effort cleanup if rename failed (rare: cross-fs, ENOSPC, etc.)
    fs.promises.unlink(tmpPath).catch(() => { /* already gone */ });
    throw err;
  }
}

// ── Discord OAuth2 ────────────────────────────────────────────────────────────

const DISCORD_AUTH_URL  = 'https://discord.com/api/oauth2/authorize';
const DISCORD_TOKEN_URL = 'https://discord.com/api/oauth2/token';
const DISCORD_USER_URL  = 'https://discord.com/api/users/@me';
const DISCORD_SCOPES    = 'identify';

export async function handleDiscordLogin(req: Request, res: Response): Promise<void> {
  const oauth = getOAuth();
  if (!oauth.clientId || !oauth.clientSecret) {
    res.status(503).send('Discord SSO not configured. Set it in /admin (Discord integration) or via DISCORD_CLIENT_ID/SECRET in .env.');
    return;
  }
  // OAuth `state` defends the callback against login-CSRF: an attacker who
  // crafts a callback URL with their own `code` and tricks the victim into
  // visiting it would otherwise bind the victim's browser to the attacker's
  // Discord ID. We mint a signed nonce, drop it as a short-lived cookie,
  // and require the callback's `state` query param to match.
  const stateValue = makeOAuthState();
  res.setHeader('Set-Cookie',
    `${OAUTH_STATE_COOKIE}=${stateValue}; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=${OAUTH_STATE_MAX_AGE}; Path=/auth/`
  );

  const url = new URL(DISCORD_AUTH_URL);
  url.searchParams.set('client_id', oauth.clientId);
  url.searchParams.set('redirect_uri', oauth.redirectUri);
  url.searchParams.set('response_type', 'code');
  url.searchParams.set('scope', DISCORD_SCOPES);
  url.searchParams.set('state', stateValue);
  res.redirect(url.toString());
}

/**
 * Mint a signed one-shot OAuth `state`: a random nonce plus an HMAC of it.
 *
 * Lives next to its verifier deliberately — the two halves have to agree on the format and the
 * key, and a signing scheme whose halves drift apart fails OPEN if the mismatch is in the
 * verifier. Exported so the tests can exercise the real pair rather than approximate one; nothing
 * in production calls it but the login redirect.
 */
export function makeOAuthState(): string {
  const nonce = crypto.randomBytes(16).toString('base64url');
  const sig = crypto.createHmac('sha256', getSigningSecret()).update(nonce).digest('base64url');
  return `${nonce}.${sig}`;
}

/** Exported for the tests: the callback's login-CSRF defence, exercised rather than read. */
export function verifyOAuthState(req: Request, claimed: string): boolean {
  if (!claimed) return false;
  const cookies = req.headers.cookie ?? '';
  let stored: string | undefined;
  for (const part of cookies.split(';')) {
    const idx = part.indexOf('=');
    if (idx === -1) continue;
    if (part.slice(0, idx).trim() === OAUTH_STATE_COOKIE) {
      stored = part.slice(idx + 1).trim();
      break;
    }
  }
  if (!stored || stored !== claimed) return false;
  const dot = claimed.lastIndexOf('.');
  if (dot < 0) return false;
  const nonce = claimed.slice(0, dot);
  const claimedSig = claimed.slice(dot + 1);
  // Dual-key (jwtSecrets.ts): an OAuth handshake that straddles a rotation
  // (state signed pre-rotation, callback lands post-rotation) still verifies
  // during the grace window instead of failing the login with 'bad state'.
  let cb: Buffer;
  try { cb = Buffer.from(claimedSig, 'base64url'); } catch { return false; }
  for (const secret of getVerifySecrets()) {
    const eb = Buffer.from(crypto.createHmac('sha256', secret).update(nonce).digest('base64url'), 'base64url');
    try { if (cb.length === eb.length && crypto.timingSafeEqual(cb, eb)) return true; } catch { /* next */ }
  }
  return false;
}

export async function handleDiscordCallback(req: Request, res: Response): Promise<void> {
  const code = typeof req.query.code === 'string' ? req.query.code : '';
  const state = typeof req.query.state === 'string' ? req.query.state : '';
  if (!code) { res.redirect('/?auth=failed'); return; }

  if (!verifyOAuthState(req, state)) {
    console.warn('[Auth] OAuth state mismatch — rejecting callback');
    res.redirect('/?auth=failed');
    return;
  }
  // Burn the state cookie so it can't be replayed.
  res.setHeader('Set-Cookie',
    `${OAUTH_STATE_COOKIE}=; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=0; Path=/auth/`
  );

  try {
    const tokenRes = await fetch(DISCORD_TOKEN_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        client_id:     getOAuth().clientId,
        client_secret: getOAuth().clientSecret,
        grant_type:    'authorization_code',
        code,
        redirect_uri:  getOAuth().redirectUri,
      }),
    });
    if (!tokenRes.ok) throw new Error(`Token exchange HTTP ${tokenRes.status}`);
    const tokenData = await tokenRes.json() as { access_token: string };

    const userRes = await fetch(DISCORD_USER_URL, {
      headers: { Authorization: `Bearer ${tokenData.access_token}` },
    });
    if (!userRes.ok) throw new Error(`User fetch HTTP ${userRes.status}`);
    const user = await userRes.json() as { id?: unknown; username?: unknown; avatar?: unknown };

    // v0.3.23 audit fix: validate the Discord /users/@me payload before
    // issuing a JWT. Pre-fix the cast to `{ id: string; ... }` was a
    // pure TypeScript assertion — any malformed payload (`{}`, missing
    // id, id as number, etc.) flowed through and `issueJWT({sub: undefined})`
    // produced a "looks-logged-in" cookie that `currentUser` later
    // rejected. Net effect: silent guest-mode after `/?auth=ok`
    // redirect with no error path. Now we explicitly require id to
    // be a non-empty Discord snowflake (decimal digits) and username
    // a non-empty string.
    const userId = typeof user.id === 'string' ? user.id : null;
    const username = typeof user.username === 'string' ? user.username : null;
    if (!userId || !/^\d+$/.test(userId) || !username) {
      console.error(`[Auth] Discord /users/@me returned malformed payload: ${JSON.stringify(user)}`);
      res.redirect('/?auth=failed');
      return;
    }
    const avatar = typeof user.avatar === 'string' ? user.avatar : null;

    const jwt = issueJWT({ sub: userId, name: username, ...(avatar ? { avatar } : {}) });
    res.appendHeader('Set-Cookie',
      `${COOKIE_NAME}=${jwt}; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=${COOKIE_MAX_AGE}; Path=/`
    );
    res.redirect('/?auth=ok');
  } catch (err) {
    console.error('[Auth] Discord callback error:', err);
    res.redirect('/?auth=failed');
  }
}

/**
 * Dev-mode login backdoor — only mounted when DEV_MODE=1 (config-time
 * gate). Mints a JWT for a synthetic user without Discord OAuth so the
 * operator (or an AI agent that doesn't have a Discord identity) can
 * exercise login-gated UI flows. Returns 503 in any other mode.
 *
 * The dev cookie is the SAME shape as a real Discord one (same HMAC,
 * same expiry, same name). Hardening: production deploys must NOT set
 * DEV_MODE; if they do, config.ts requires DEV_MODE_ALLOW_PROD=1 too,
 * and warns loudly at boot.
 */
export async function handleDevLogin(req: Request, res: Response): Promise<void> {
  if (!DEV_MODE) {
    res.status(503).send('DEV_MODE not enabled.');
    return;
  }
  if (!requireSafeOrigin(req, res)) return;
  const jwt = issueJWT({ sub: DEV_USER_SUB, name: DEV_USER_NAME });
  res.appendHeader('Set-Cookie',
    `${COOKIE_NAME}=${jwt}; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=${COOKIE_MAX_AGE}; Path=/`,
  );
  // Mirror handleDiscordCallback: send the user back to the landing page
  // with an `auth=ok` flag so the SSO panel JS picks up the welcome state.
  res.redirect('/?auth=ok');
}

/**
 * POST /auth/guest — mint a session cookie for an anonymous visitor.
 *
 * Background (v0.3.13 audit HIGH-1.3 + B30 incident, 2026-05-04):
 *   The nginx /server-<id>/* gate rejects any request whose `__session`
 *   cookie is empty (returns 403). Originally the SSO panel's
 *   "Continue as guest" button only hid the panel and resolved the boot
 *   promise WITHOUT setting any cookie — so guest users hit a wall of
 *   403s on every gamefile fetch. Discord-authenticated users were the
 *   only working path in production. This endpoint closes that gap.
 *
 *   Subjects: `guest-<8-hex>`. The `guest-` prefix lets per-user
 *   rate-limit (UOProxy.allowC2s) + audit logs distinguish anon traffic
 *   from Discord users (whose sub is the Discord user ID). Same JWT
 *   shape as Discord/dev — same HMAC, same expiry, same cookie flags.
 *
 *   guestsAllowed: NOT enforced here. The gate lives in UOProxy on WS
 *   upgrade — guests can grab a cookie and download gamefiles for any
 *   shard, but if the shard's yaml says `guestsAllowed: false` the WS
 *   upgrade rejects them. Letting guests pre-fetch assets is harmless
 *   and avoids friction when an admin toggles the flag mid-session.
 *
 *   Returns JSON `{ ok: true, sub: 'guest-<id>' }` so main.js can show
 *   a transient toast / log line. No redirect (the caller is a fetch,
 *   not a navigation).
 */
export async function handleGuestLogin(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;

  // v0.3.20 audit fix: don't downgrade an existing valid session. If the
  // caller already has a Discord (or dev-login) cookie, return that sub
  // instead of minting a new guest one. A surviving XSS that hits
  // /auth/guest would otherwise silently strip the user's Discord
  // identity binding from their cookie, breaking /api/profile + linked
  // Discord features until they re-OAuth.
  const existing = currentUser(req);
  if (existing && !existing.sub.startsWith('guest-')) {
    res.json({ ok: true, sub: existing.sub, reused: true });
    return;
  }
  // Same caveat for an already-valid guest cookie: return the same sub
  // (idempotent) instead of churning the cookie + creating an orphan
  // /api/profile/<old-sub>/ tree on disk.
  if (existing && existing.sub.startsWith('guest-')) {
    res.json({ ok: true, sub: existing.sub, reused: true });
    return;
  }

  // We're about to MINT a brand-new guest sub (no valid cookie). Cap new mints per IP so a client can't
  // rotate fresh guestIds to flood the shared shard with auto-created accounts (audit 2026-06-22 #2).
  if (!guestMintAllowed(clientIpFromReq(req))) {
    res.status(429).json({ ok: false, error: 'too many new guest sessions from your network — try again later' });
    return;
  }

  // STABLE per-browser guest id (mini): the client persists a random 16-hex id in
  // localStorage and sends it so the SAME browser always resolves to the SAME guest
  // account, even after the 24h cookie expires. Without this every cookie-less visit
  // (expired cookie, cleared storage, a partitioned 3rd-party iframe) minted a fresh
  // guest-<hex> → MiniAutoLogin created yet another g<hex> account on the shard
  // (operator 2026-06-15: "no debería generar cientos de cuentas; la cache y la
  // cookie deberían guardar su cuenta"). We use the client id DIRECTLY as the suffix
  // (it is already 64-bit random) rather than HMAC'ing it, so the sub survives a
  // JWT_SECRET rotation. Strict ^[0-9a-f]{16}$ keeps the sub format server-controlled
  // (a client can't pick an arbitrary/Discord-looking sub) and unguessable (64-bit).
  let guestId: string;
  const claimed = (req.body && typeof req.body === 'object' && typeof (req.body as Record<string, unknown>).guestId === 'string')
    ? String((req.body as Record<string, unknown>).guestId).trim().toLowerCase()
    : '';
  if (/^[0-9a-f]{16}$/.test(claimed)) {
    guestId = claimed;
  } else {
    // v0.3.20 audit fix: 8 random bytes → 16 hex chars (64-bit space) so two
    // browsers don't collide on `sub` and overwrite each other's /api/profile.
    guestId = crypto.randomBytes(8).toString('hex');
  }
  const sub = `guest-${guestId}`;
  const jwt = issueJWT({ sub, name: `Guest ${guestId}` }, GUEST_COOKIE_MAX_AGE);
  res.appendHeader('Set-Cookie',
    `${COOKIE_NAME}=${jwt}; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=${GUEST_COOKIE_MAX_AGE}; Path=/`,
  );
  res.json({ ok: true, sub });
}

export async function handleLogout(req: Request, res: Response): Promise<void> {
  // CSRF: refuse cross-origin logouts. SameSite=Lax already mitigates most
  // cases but a forged GET-redirect attack can still log a victim out.
  if (!requireSafeOrigin(req, res)) return;
  const token = parseCookie(req);
  if (token) {
    // v0.3.13 audit R4-L-1: also bump `loggedOutAt[sub]` past this JWT's
    // iat so an attacker can't reactivate a stolen-then-revoked token by
    // FIFO-evicting it out of the in-memory `revokedJtis` map (10k cap,
    // doable in ~16h at the auth rate-limit). The persistent
    // loggedOutAt check survives even if revokedJtis gets evicted.
    // Side-effect: a logout from one device also kills any OTHER session
    // for this sub whose iat is ≤ this token's iat. New sessions opened
    // AFTER this logout (higher iat) are unaffected. For the typical
    // single-device user this is invisible; for multi-device users this
    // is the safer default, with /auth/logout-all available as the
    // explicit "kill literally everything including future not-yet-
    // issued tokens" verb.
    const payload = verifyJWT(token);
    if (payload && typeof payload.sub === 'string' && typeof payload.iat === 'number') {
      const existing = loggedOutAt.get(payload.sub) ?? 0;
      const target = payload.iat + 1;
      if (target > existing) {
        loggedOutAt.set(payload.sub, target);
        persistLoggedOutAt();
      }
    }
    // Add the JWT's jti to the in-memory revocation list as a fast-path
    // (verifyJWT short-circuits on revokedJtis before reaching the
    // loggedOutAt check; saves a Map lookup on the hot path).
    revokeJWT(token);
  }
  res.setHeader('Set-Cookie', `${COOKIE_NAME}=; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=0; Path=/`);
  res.redirect('/');
}

// GDPR right to erasure (operator 2026-06-11, "delete account" in Manage
// Storage). Wipes every personal record we hold for the caller — SQLite tables
// (identity, nickname, metrics, achievements, votes, deaths), cloud profile
// blobs, and any admin/owner grant — then kills the session. Irreversible; the
// client gates it behind a typed confirmation. Their owned shards (if any) lose
// their owner and become operator-managed orphans (we don't auto-delete game
// content on an account deletion).
export async function handleDeleteAccount(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'not authenticated' }); return; }
  if (user.sub.startsWith('guest-')) { res.status(403).json({ error: 'guest sessions store no account to delete' }); return; }
  // 7-day grace (operator 2026-06-18): schedule the erasure instead of running it now.
  // The account keeps working during the countdown so the player can change their mind
  // and cancel (POST /api/me/cancel-deletion); the reaper (AssetServer) performs the
  // real, irreversible erasure — eraseUserData + profile blobs + admin grants + session
  // kill, via adminEraseUser — once scheduled_at passes.
  const pending = scheduleAccountDeletion(user.sub);
  res.setHeader('Cache-Control', 'no-store');
  res.json({ ok: true, pending: true, scheduledAt: pending.scheduledAt, graceDays: 7 });
}

/** Cancel a pending account deletion during the 7-day grace (operator 2026-06-18). */
export async function handleCancelDeletion(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'not authenticated' }); return; }
  if (user.sub.startsWith('guest-')) { res.status(403).json({ error: 'no account' }); return; }
  const cancelled = cancelAccountDeletion(user.sub);
  res.setHeader('Cache-Control', 'no-store');
  res.json({ ok: true, cancelled });
}

/** Admin "delete user": full erasure of ANOTHER account — every personal record,
 *  cloud profile blobs, any admin/owner grant — and kills their sessions. Does NOT
 *  ban them (block is a separate, persistent action). Returns what was removed. */
/**
 * Every account this install knows about, for an admin view.
 *
 * Reads the identities table AND the settings files on disk, because the two do not always agree:
 * a guest gets a settings file the moment it saves anything, and an identity row only once it is
 * recorded. Listing one source alone would hide accounts that are genuinely occupying space.
 *
 * `bytes` is the settings file only — profile blobs are listed separately by listProfiles(), which
 * already knows their naming scheme.
 */
export function listAccounts(): Array<{
  sub: string; name: string | null; isGuest: boolean; seen: number | null; bytes: number;
}> {
  const rows = new Map<string, { name: string | null; seen: number | null }>();
  try {
    for (const r of db.prepare('SELECT sub, name, seen FROM identities').all() as
        Array<{ sub: string; name: string; seen: number }>) {
      rows.set(r.sub, { name: r.name || null, seen: r.seen || null });
    }
  } catch { /* no table yet — the disk scan below still answers */ }

  const out: Array<{ sub: string; name: string | null; isGuest: boolean; seen: number | null; bytes: number }> = [];
  const seenSubs = new Set<string>();
  let files: string[] = [];
  try { files = fs.readdirSync(DATA_PATH); } catch { /* unreadable data dir */ }
  for (const f of files) {
    if (!f.endsWith('.json') || f === 'admin-settings.json') continue;
    const sub = f.slice(0, -5);
    let bytes = 0;
    try { bytes = fs.statSync(path.join(DATA_PATH, f)).size; } catch { /* vanished */ }
    const meta = rows.get(sub);
    seenSubs.add(sub);
    out.push({ sub, name: meta?.name ?? null, isGuest: sub.startsWith('guest-'), seen: meta?.seen ?? null, bytes });
  }
  // Identities with no settings file yet still exist and can still be erased.
  for (const [sub, meta] of rows) {
    if (seenSubs.has(sub)) continue;
    out.push({ sub, name: meta.name, isGuest: sub.startsWith('guest-'), seen: meta.seen, bytes: 0 });
  }
  out.sort((a, b) => (b.seen ?? 0) - (a.seen ?? 0));
  return out;
}

export function adminEraseUser(sub: string): { removed: number; profileBlobs: number } {
  if (!sub) return { removed: 0, profileBlobs: 0 };

  // 🚨 THE SETTINGS FILE IS PERSONAL DATA AND NOTHING WAS DELETING IT. eraseUserData clears DB rows
  // and deleteProfilesForUser clears blobs; DATA_PATH/<sub>.json — the player's stored client
  // settings — survived every erasure. On the minimal build that file IS the data, so an admin
  // purge reported {ok:true, erased:3} while all three files sat untouched on disk. Caught 2026-08-26
  // by listing the directory after the purge instead of trusting the response.
  //
  // 🚨 AND GUESTS USED TO RETURN EARLY, one line up, which made erasing a guest a total no-op. That
  // is defensible where a guest owns no rows, but not where a guest owns a settings file: the early
  // return is now scoped to the DB pass, which is the part that genuinely has nothing to do.
  try { fs.unlinkSync(settingsPath(sub)); } catch { /* absent or unreadable — nothing to erase */ }

  let removed = 0;
  if (sub.startsWith('guest-')) {
    // eraseUserData skips guests wholesale, and `identities` is one of the tables it would have
    // cleared — so a guest's name/avatar/last-seen row outlived every erasure. It is a small row
    // and it is still a record of a person.
    try { removed = Number(db.prepare('DELETE FROM identities WHERE sub = ?').run(sub).changes) || 0; }
    catch { /* no table yet */ }
  } else {
    removed = eraseUserData(sub);
  }
  let profileBlobs = 0;
  try { profileBlobs = deleteProfilesForUser(sub); } catch { /* best-effort */ }
  if (sub.startsWith('guest-')) return { removed, profileBlobs };
  // On-disk personal FILES, not just DB rows (audit 2026-07-25 found these survived
  // an erasure): the player's uploaded gameview screenshots, and any logo left over
  // from the retired logo-upload feature (its route now 410s, but old files remain).
  // Fire-and-forget: erasure must not block on I/O, and both helpers never throw.
  void deleteAllScreenshots(sub).catch(() => { /* best-effort */ });
  void deleteUserLogo(sub).catch(() => { /* best-effort */ });
  try {
    const admins = getRuntimeConfigFile().admins;
    if (admins && Object.prototype.hasOwnProperty.call(admins, sub)) {
      updateRuntimeConfig((cfg) => { if (cfg.admins) delete (cfg.admins as Record<string, unknown>)[sub]; });
    }
  } catch { /* best-effort */ }
  loggedOutAt.set(sub, Math.floor(Date.now() / 1000) + 1);
  persistLoggedOutAt();
  return { removed, profileBlobs };
}

// v0.3.13 audit R2-M-4: "log out everywhere" — kills every JWT issued
// to this Discord ID before now (iat-based check in verifyJWT). Use
// case: user discovered their phone was stolen / a tab on a shared
// computer was left logged in / suspect XSS leak. One POST and every
// other tab is dead within one HTTP round-trip's worth of clock skew.
// Persisted across restarts via `loggedOutAt` JSON file.
export async function handleLogoutAll(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'not authenticated' }); return; }
  // v0.3.21 audit fix: bump cutoff +1s so a token minted in the same
  // wall-clock second as the logout-all call doesn't sneak through.
  // verifyJWT compares `iat < lo` (strict less-than); without the +1
  // bump, a JWT whose iat == loggedOutAt would be accepted. The
  // companion handleLogout at line ~708 already does `target = iat + 1`
  // for symmetric reason — this aligns logout-all with that contract.
  loggedOutAt.set(user.sub, Math.floor(Date.now() / 1000) + 1);
  persistLoggedOutAt();
  // Also revoke this device's JWT and clear its cookie so the caller
  // sees themselves logged out immediately (without waiting for the
  // browser to drop the cookie on next request).
  const token = parseCookie(req);
  if (token) revokeJWT(token);
  res.setHeader('Set-Cookie', `${COOKIE_NAME}=; ${COOKIE_FLAGS};${cookieDomain(req)} Max-Age=0; Path=/`);
  res.json({ message: 'logged out everywhere', sub: user.sub });
}

// ── API handlers ──────────────────────────────────────────────────────────────

export async function handleApiMe(req: Request, res: Response): Promise<void> {
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'Not authenticated' }); return; }
  // Capture the Discord display name (debounced; guests skipped inside) so the
  // admin profile panel can show a human name next to the opaque id.
  recordIdentity(user.sub, user.name, user.avatar);
  // Discord-login welcome: just for signing in with Discord, grant one random Bronze card
  // (once per account; guests skipped; in-proc Set keeps this hot path cheap). Non-fatal.
  try { _onDiscordLogin?.(user.sub); } catch { /* login side effects are best-effort, never fatal */ }
  // isAdmin + scopes let the admin console (/admin) gate itself and decide which
  // controls to render. scopes is the forward-compatible field (per-admin
  // granularity later); isAdmin stays as the simple boolean for current callers.
  const isAdmin = isDiscordAdmin(user.sub);
  const scopes = adminScopes(user.sub);
  res.setHeader('Cache-Control', 'no-store'); // per-user identity/scopes — never cache (CF / browser bfcache / shared PC)
  res.json({
    id: user.sub,
    name: user.name,
    // Prefer the JWT's avatar; fall back to the stored identity avatar so a token
    // minted before avatar-in-JWT still shows the real Discord picture (no re-login).
    avatar: user.avatar ?? getIdentityAvatar(user.sub) ?? null,
    isAdmin,
    scopes,
    // Two tiers: general admins have servers:write (edit any shard); server
    // owners have servers:write:own + this list (edit only these slugs).
    isGeneralAdmin: scopes.includes('servers:write'),
    ownedServers: adminOwnedServers(user.sub),
    // canRegister lets the launcher show a "Register your shard" entry point to
    // a self-service-eligible user who isn't an admin and owns nothing yet
    // (otherwise the /admin link stays hidden and they can't find the button).
    // Mirrors GET /api/servers/self/config's canRegister: flag ON + Discord (not guest).
    canRegister: getFlag('allow-self-service-shards') && !user.sub.startsWith('guest-'),
    // 7-day account-deletion grace (operator 2026-06-18): non-null while a deletion
    // is pending so the client can show the countdown + a "Cancel deletion" button.
    pendingDeletion: user.sub.startsWith('guest-') ? null : (getPendingDeletion(user.sub)?.scheduledAt ?? null),
  });
}

// v0.7.9 — security fix for the "anyone with Cookie: __session=anything can
// download gamefiles" bug. Pre-fix nginx's `if ($cookie___session = "")
// return 403;` only checked that the cookie was non-empty, so a forged
// junk value passed straight through and let attackers exfiltrate the
// whole .mul/.uop tree without going through Discord login. This endpoint
// cryptographically validates the JWT signature + iat/exp + revocation
// list (full verifyRequestJwt path) and reports the verdict as 200/401 so
// nginx `auth_request /auth/verify` can gate /server-N/* requests.
//
// Designed to be cheap: no DB lookup, no body, no logging on success — the
// rate-limit zones are wide enough that nginx can sub-request once per
// gamefile fetch (memento boot = ~91 hits in 30 s) without breaking a
// sweat. Returns 401 (not 403) on failure so the browser doesn't cache
// the deny — invalid cookies become valid again the second the user
// signs in, and Cloudflare's edge keeps `private, no-store` per the
// always-on Cache-Control header.
export async function handleAuthVerify(req: Request, res: Response): Promise<void> {
  // Always emit no-store so neither browser nor CF caches the verdict.
  res.setHeader('Cache-Control', 'private, no-store');
  res.setHeader('CDN-Cache-Control', 'no-store');
  const payload = verifyRequestJwt(req);
  if (!payload) { res.status(401).end(); return; }
  res.status(200).end();
}

export async function handleApiGetSettings(req: Request, res: Response): Promise<void> {
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'Not authenticated' }); return; }
  res.setHeader('Cache-Control', 'no-store'); // per-user settings — never cache (CF / browser bfcache / shared PC)
  res.json(await loadSettings(user.sub));
}

export async function handleApiPutSettings(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'Not authenticated' }); return; }
  await persistSettings(user.sub, req.body);
  res.json({ ok: true });
}

// The bundle that owns this profile blob (CUO vs TUO). Untrusted query param,
// so accept only the known bundles; anything else → legacy single-blob name.
function profileClientParam(req: Request): string | undefined {
  const c = req.query.client;
  return (c === 'cuo' || c === 'tuo' || c === 'mini') ? c : undefined;
}

export async function handleApiGetProfile(req: Request, res: Response): Promise<void> {
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'Not authenticated' }); return; }
  const blob = await loadProfile(user.sub, profileClientParam(req));
  if (!blob) { res.status(404).end(); return; }
  res.setHeader('Content-Type', 'application/octet-stream');
  res.setHeader('Cache-Control', 'private, no-store');
  res.send(blob);
}

export async function handleApiPutProfile(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'Not authenticated' }); return; }
  if (!Buffer.isBuffer(req.body)) {
    res.status(400).json({ error: 'expected binary body (application/octet-stream)' });
    return;
  }
  try {
    await persistProfile(user.sub, req.body, profileClientParam(req));
    res.json({ ok: true, bytes: req.body.length });
  } catch (e: unknown) {
    // Express itself enforces the 10 MB body cap with 413; any error
    // reaching here is structural (bad gzip, disallowed entry, path
    // escape, oversized inner file) — a client-side bug or abuse,
    // either way 400 is the right code. Log the detail server-side
    // for forensics; surface only a generic error to the caller so
    // we don't help an attacker iterate against the tar parser.
    const detail = e instanceof Error ? e.message : 'persist failed';
    console.warn(`[Auth] /api/profile rejected for sub=${user.sub}: ${detail}`);
    res.status(400).json({ error: 'invalid profile blob' });
  }
}

// DELETE /api/profile — the caller resets their OWN cloud UO settings (operator
// 2026-06-20: "Delete my UO settings (CUO + TUO) online"). Removes ONLY this
// user's profile blobs (<sub>[.cuo|.tuo].tar.gz) — the ClassicUO/TazUO settings +
// macros. Cards, points, achievements, cosmetics and the nickname live in SQLite
// and are NEVER touched. The client suppresses re-upload + signs out so the blob
// does not immediately come back; next login starts from clean defaults.
export async function handleApiDeleteProfile(req: Request, res: Response): Promise<void> {
  if (!requireSafeOrigin(req, res)) return;
  const user = currentUser(req);
  if (!user) { res.status(401).json({ error: 'Not authenticated' }); return; }
  if (user.sub.startsWith('guest-')) { res.status(403).json({ error: 'guests have no cloud profile' }); return; }
  let removed = 0;
  try { removed = deleteProfilesForUser(user.sub); } catch { /* best-effort */ }
  res.setHeader('Cache-Control', 'no-store');
  res.json({ ok: true, removed });
}
