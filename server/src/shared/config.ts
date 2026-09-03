import * as path from 'path';
import { getRuntimeConfigFile } from './runtimeConfig.js';

export const UO_SERVER_HOST = process.env.UO_HOST ?? '127.0.0.1';
export const UO_SERVER_PORT = parseInt(process.env.UO_PORT ?? '2595');
// Single port for HTTP + WebSocket. Override with the PORT env var.
export const ASSETS_HTTP_PORT = parseInt(process.env.PORT ?? process.env.ASSETS_PORT ?? '8080');
export const PROXY_WS_PORT = ASSETS_HTTP_PORT; // same port as HTTP
// Path to UO client data files (.mul, .idx, etc.). Override with ASSETS_PATH.
//
// Legacy single-shard variable — superseded by `gamefilesPath` inside each
// servers/<slug>.yaml from v0.3.0 on. Kept so deploys without a server
// registry still resolve a default path; ServerRegistry.installLegacyFallback
// reads it. New deploys should ignore this and put paths in YAML instead.
export const ASSETS_PATH = process.env.ASSETS_PATH ?? '../gamefiles';
// Path to the compiled web client bundle (index.html + _framework/). Override with CLIENT_PATH.
export const CLIENT_DIST_PATH = process.env.CLIENT_PATH ?? '../client';
// Directory for per-user settings JSON files (Discord SSO). Override with DATA_PATH.
export const DATA_PATH = process.env.DATA_PATH ?? './data';

// v0.3.0+ multi-server config dirs.
//   SERVERS_DIR      — *.yaml records managed by the operator (read-only inside container).
//                      Populated by servers-init.sh at boot from the bind-mount source.
//   DATA_SERVERS_DIR — *.yaml records written by the admin API (read-write, survives
//                      restart). Lives under DATA_PATH so the proxy-data named volume
//                      persists them across container recreations. Admin files take
//                      precedence over operator files for the same slug.
//   MANIFESTS_DIR    — pre-generated <slug>.json maps from filename → SHA-256 hash
//                      used by the WASM client to fetch from the content-addressed
//                      gamefiles pool instead of the per-shard bind-mount.
export const SERVERS_DIR      = process.env.SERVERS_DIR      ?? './servers';
export const MANIFESTS_DIR    = process.env.MANIFESTS_DIR    ?? './manifests';
export const DATA_SERVERS_DIR = process.env.DATA_SERVERS_DIR
  ?? path.join(process.env.DATA_PATH ?? './data', 'servers');

// Absolute path to the root directory that holds all shard gamefiles as
// named sub-directories:  <root>/server-<id>/   (raw .mul tree)
//                          <root>/server-<id>-web/ (brotli-compressed twins)
//                          <root>/pool/            (content-addressed dedup pool)
// Defaults to empty string (force-delete silently skips the gamefiles wipe
// when GAMEFILES_ROOT is not configured — safe for single-shard deploys that
// don't use the admin API).
export const GAMEFILES_ROOT = process.env.GAMEFILES_ROOT ?? '';

// Trading-card art directory (RW inside the container). The admin .zip upload
// (CARDS_ADMIN_UPLOAD.md) writes original PNGs + generated thumbs/wm/wmthumbs here; nginx
// serves the SAME host dir read-only at /usr/share/nginx/cards. Defaults to the mount /app/cards.
export const CARDS_DIR = process.env.CARDS_DIR ?? '/app/cards';

// Administrators are defined SOLELY by the hot-reloadable runtime config file
// (app-config.json `admins` map — see runtimeConfig.ts). Editing that file adds
// or removes an admin live, with no container recreate. There is deliberately
// NO env-var admin list: a single source of truth avoids the "two places to
// look" class of bug. Bootstrap = add your Discord id to the `admins` map in
// app-config.json (DEV_ADMIN=1 also grants the dev-tester in dev mode). The
// helpers below read the LIVE file on every call. See docs/shared/ADMIN.md.

// ── Admin scopes + the two admin tiers ───────────────────────────────────────
// GENERAL admin (granted by "*"): full control over every shard + bans + admin
// management. SERVER OWNER: the restricted servers:write:own scope + a `servers`
// list — may edit ONLY those slugs, cannot create/delete or touch others.
//
// app-config.json `admins` value forms (per Discord id):
//    ["*"] | [] | ["servers:write", …]            → scopes only (general)
//    { "scopes": ["servers:write:own"], "servers": ["myslug"] }  → owner
export const ADMIN_SCOPES_ALL = [
  'servers:write',   // create / edit / delete ANY shard
  'servers:approve', // approve / reject PENDING self-service shard requests (a
                     // delegable SUBSET of servers:write — review only, no CRUD)
  'bans:write',      // add / remove bans
  'admins:manage',   // grant / revoke admins from the web
] as const;
// Restricted owner scope — assigned explicitly, NOT included in "*".
export const SCOPE_SERVERS_WRITE_OWN = 'servers:write:own';
const KNOWN_SCOPES = new Set<string>([...ADMIN_SCOPES_ALL, SCOPE_SERVERS_WRITE_OWN]);

// ── Moderator tier (operator 2026-06-19) ─────────────────────────────────────
// An intermediate "moderator" admin: the SUPREME admin (admins:manage) delegates
// a curated, ESCALATION-SAFE subset of capabilities to a trusted user. A
// moderator can NEVER hold admins:manage (the escalation vector — it would let
// them grant themselves "*") nor servers:write (create/delete ANY shard); those
// stay supreme-only. servers:write:own is the OWNER tier (slug-scoped), separate.
// The moderator-create path filters incoming scopes to THIS set server-side, so
// even a tampered request can't smuggle an escalating scope into a moderator.
export const MODERATOR_SCOPES = ['servers:approve', 'bans:write'] as const;

export interface AdminEntry { scopes: string[]; servers: string[]; }

function expandScopes(arr: unknown): string[] {
  // FAIL-CLOSED: only an explicit "*" grants the full set. A malformed value or
  // an empty array grants NO scopes (so "park an admin with []" / a fat-fingered
  // value can never silently escalate to full control — audit MED-1). General
  // admin must be written as ["*"]; the PUT /admin API + app-config.example do.
  if (!Array.isArray(arr)) return [];
  if (arr.includes('*')) return [...ADMIN_SCOPES_ALL];
  return arr.map(String).filter((s) => KNOWN_SCOPES.has(s));
}

/** Normalise a raw app-config admin value (array OR {scopes,servers}) to an
 *  AdminEntry. Pure — works on any value, not just the live file (the admin-
 *  management endpoints use it to simulate a change before persisting). */
export function normalizeAdminValue(v: unknown): AdminEntry {
  if (Array.isArray(v)) return { scopes: expandScopes(v), servers: [] };
  if (v && typeof v === 'object') {
    const o = v as { scopes?: unknown; servers?: unknown };
    const servers = Array.isArray(o.servers)
      ? o.servers.map((s) => String(s).trim().toLowerCase()).filter(Boolean)
      : [];
    return { scopes: expandScopes(o.scopes), servers };
  }
  // The bare-string wildcard, kept EXPLICIT. `admins: { id: "*" }` is a shape people really write
  // by hand, and it used to work only by falling through to the catch-all below — so closing that
  // catch-all would have silently revoked it. Naming it here preserves the intent ("only an
  // explicit wildcard grants everything", the same rule expandScopes applies to the array form)
  // while removing the accident.
  if (v === '*') return { scopes: [...ADMIN_SCOPES_ALL], servers: [] };
  // 🚨 FAIL-CLOSED, to match the policy expandScopes states two functions above: "a malformed
  // value or an empty array grants NO scopes ... can never silently escalate to full control
  // (audit MED-1)". This last branch used to do the exact opposite — anything that was neither an
  // array nor an object (a string, a number, `true`, and notably `undefined` for an ABSENT key)
  // was handed every scope in the system.
  //
  // It had already bitten once. The v0.8.82 note in the self-service registration path documents
  // working AROUND it at the call site, because normalizeAdminValue(undefined) made a brand-new
  // owner evaluate as a general admin. That symptom was benign — a grant was skipped — but the
  // evaluation underneath said "this person holds admins:manage", and the next caller to branch on
  // it the other way would have granted rather than skipped.
  //
  // Measured before changing it: all six call sites either iterate existing keys or guard with
  // hasOwnProperty, so nothing depends on the old answer, and the live config holds exactly one
  // admin entry, in ARRAY form. So this is a no-op today and a closed door tomorrow. The legacy
  // array shape (["*"]) and the {scopes,servers} shape are untouched above.
  return { scopes: [], servers: [] };
}

/** Scopes a raw admin value would grant (for lockout simulation). */
export function adminValueScopes(v: unknown): string[] {
  return normalizeAdminValue(v).scopes;
}

/** Normalised admin record for a Discord id, or null if not an admin. */
export function getAdminEntry(discordUserId: string): AdminEntry | null {
  // DEV_ADMIN dev-tester is a general admin (preserves the dev backdoor — the
  // new scope gates would otherwise deny it even though resolveAdmin allows it).
  if (DEV_ADMIN && discordUserId === DEV_USER_SUB) {
    return { scopes: [...ADMIN_SCOPES_ALL], servers: [] };
  }
  const admins = getRuntimeConfigFile().admins;
  if (admins && typeof admins === 'object' &&
      Object.prototype.hasOwnProperty.call(admins, discordUserId)) {
    return normalizeAdminValue((admins as Record<string, unknown>)[discordUserId]);
  }
  // Not in the live admins map (and not the dev-tester) → not an admin.
  return null;
}

export function isDiscordAdmin(discordUserId: string): boolean {
  return getAdminEntry(discordUserId) !== null;
}

export function adminScopes(discordUserId: string): string[] {
  return getAdminEntry(discordUserId)?.scopes ?? [];
}

/** Slugs a server-owner may edit. Empty for general admins (who may edit all). */
export function adminOwnedServers(discordUserId: string): string[] {
  return getAdminEntry(discordUserId)?.servers ?? [];
}

export function adminHasScope(discordUserId: string, scope: string): boolean {
  return adminScopes(discordUserId).includes(scope);
}

/** Remove a slug from EVERY owner's grant in a raw admins map (mutates `admins`
 *  in place). General admins (servers:write) are never slug-scoped → untouched;
 *  an owner left with no servers is dropped entirely. Pure structural transform
 *  used by the shard-deletion paths so a stale `servers:['<slug>']` can't survive
 *  deletion and hijack a recycled slug (audit 2026-06-21, findings 2/3). Returns
 *  the number of owners scrubbed. */
export function scrubSlugFromAdmins(admins: Record<string, unknown>, slug: string): number {
  const s = String(slug).toLowerCase();
  let removed = 0;
  for (const sub of Object.keys(admins)) {
    const norm = normalizeAdminValue(admins[sub]);
    if (norm.scopes.includes('servers:write')) continue;   // general admin — never slug-scoped
    if (!norm.servers.includes(s)) continue;
    const servers = norm.servers.filter((sv) => sv !== s);
    if (servers.length === 0) delete admins[sub];
    else admins[sub] = { scopes: [SCOPE_SERVERS_WRITE_OWN], servers };
    removed++;
  }
  return removed;
}

/** May this admin edit this specific shard? General (servers:write) → any;
 *  owner (servers:write:own) → only their listed slugs. */
export function canEditServer(discordUserId: string, slug: string): boolean {
  const e = getAdminEntry(discordUserId);
  if (!e) return false;
  if (e.scopes.includes('servers:write')) return true;
  if (e.scopes.includes(SCOPE_SERVERS_WRITE_OWN)) return e.servers.includes(String(slug).toLowerCase());
  return false;
}

// `true` flips Secure cookie + stricter rate limits. The proxy detects
// production from NODE_ENV; the operator can override with PRODUCTION=1.
//
// v0.3.13 audit R2-M-1: declared BEFORE the DEV_MODE block. Pre-fix the
// dev-mode refuse-to-start guard at line ~80 referenced this constant
// before its declaration; thanks to `&&` short-circuiting the TDZ
// reference never fired in production deploys (where DEV_MODE is unset)
// — but the moment any operator set DEV_MODE=1, the guard exploded with
// `ReferenceError: Cannot access 'IS_PRODUCTION' before initialization`,
// silently disabling the fail-closed behaviour the guard exists to
// provide. Hoist resolves both timing dependencies cleanly.
export const IS_PRODUCTION = (process.env.PRODUCTION === '1') || process.env.NODE_ENV === 'production';

// ── Dev mode ───────────────────────────────────────────────────────────────
// `DEV_MODE=1` exposes a backdoor login at /auth/dev-login that mints a
// JWT for a synthetic user (sub='dev', name='dev-tester') without going
// through Discord OAuth. Lets the operator (or AI assistant testing the
// flow) exercise login-gated UI without real Discord credentials.
//
// Hard-disabled in production unless DEV_MODE_ALLOW_PROD=1 is also set —
// staging deploys that need to be exercised the same way as prod can
// opt in explicitly. Default behaviour is fail-closed.
//
// `DEV_ADMIN=1` (additionally) marks the dev user as an admin for the
// /api/admin/* endpoints (landing in v0.3.1). Off by default so dev mode
// alone doesn't grant write access.
const _devModeRaw = process.env.DEV_MODE === '1';
const _devAllowProd = process.env.DEV_MODE_ALLOW_PROD === '1';
const _devKnowWhatImDoing = process.env.DEV_MODE_I_KNOW_WHAT_IM_DOING === 'yes-really';
// v0.3.13 audit LOW-1.6: when an operator explicitly opts into dev mode in
// production (DEV_MODE_ALLOW_PROD=1) refuse to boot unless ALSO setting
// DEV_MODE_I_KNOW_WHAT_IM_DOING=yes-really. Two-key footgun: both must be
// turned on the same day to prove intent. Pre-fix the prod opt-in only
// emitted a warning, which an automated deploy would silently swallow.
if (_devModeRaw && IS_PRODUCTION && _devAllowProd && !_devKnowWhatImDoing) {
  console.error(
    '[Config] FATAL: DEV_MODE=1 + DEV_MODE_ALLOW_PROD=1 in production without ' +
    'DEV_MODE_I_KNOW_WHAT_IM_DOING=yes-really. This combination would mint a ' +
    'JWT for sub="dev-tester" via /auth/dev-login with no real auth — refuse ' +
    'to start. Set the third var to confirm intent (literally yes-really), ' +
    'or unset DEV_MODE/DEV_MODE_ALLOW_PROD if this was a mistake.',
  );
  process.exit(1);
}
export const DEV_MODE = _devModeRaw && (!IS_PRODUCTION || (_devAllowProd && _devKnowWhatImDoing));
export const DEV_ADMIN = DEV_MODE && process.env.DEV_ADMIN === '1';
if (_devModeRaw && IS_PRODUCTION && !_devAllowProd) {
  console.error(
    '[Config] DEV_MODE=1 was requested in production without DEV_MODE_ALLOW_PROD=1 — ignored. ' +
    'Set DEV_MODE_ALLOW_PROD=1 + DEV_MODE_I_KNOW_WHAT_IM_DOING=yes-really to opt in (deliberate footgun guard).',
  );
}
export const DEV_USER_SUB  = 'dev-tester';
export const DEV_USER_NAME = 'Dev Tester';

// Display name shown in the login screen title. Override with SERVER_NAME.
// Default kept brand-only ("UO Nexus") — the chrome must not advertise the
// build tech (operator 2026-06-16).
const ENV_SERVER_NAME = process.env.SERVER_NAME ?? 'UO Nexus';
export const SERVER_NAME = ENV_SERVER_NAME;   // back-compat snapshot
/** Live display name (runtime config file overrides SERVER_NAME env). */
export function getServerName(): string {
  const f = getRuntimeConfigFile().serverName;
  return (typeof f === 'string' && f.trim()) ? f.trim() : ENV_SERVER_NAME;
}

// Trust X-Forwarded-* headers from this many reverse-proxy hops. Default 0
// (fail-closed) — bare `npm start` exposed to the internet should NEVER
// trust attacker-supplied XFF. The docker-compose deploy explicitly sets
// `TRUST_PROXY_HOPS=1` because nginx is in front.
export const TRUST_PROXY_HOPS = parseInt(process.env.TRUST_PROXY_HOPS ?? '0');

// Public-facing URL the client reaches us on. Used for CSRF Origin/Referer
// allow-list and CORS allow-list on /api + /auth. Comma-separated for multi-
// origin deploys. Empty string means "match any same-host request".
const ENV_PUBLIC_ORIGINS = (process.env.PUBLIC_ORIGINS ?? '')
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean);
export const PUBLIC_ORIGINS = ENV_PUBLIC_ORIGINS;   // back-compat snapshot
/** Live CSRF/CORS allow-list (runtime config file overrides PUBLIC_ORIGINS env
 *  when it has a non-empty publicOrigins array). Callers MUST use this, not the
 *  static export, to pick up edits without a restart. */
export function getPublicOrigins(): string[] {
  const f = getRuntimeConfigFile().publicOrigins;
  return (Array.isArray(f) && f.length > 0)
    ? f.map((s) => String(s).trim()).filter(Boolean)
    : ENV_PUBLIC_ORIGINS;
}

// v0.3.13 audit R2-L-1: shard `logo` URLs in YAML used to accept any
// https:// host. Rendered as `<img src=...>` in the picker, that turns
// into a per-visitor beacon (every site visit pings the URL, leaking
// IP and User-Agent to the attacker). For multi-admin deployments
// (operators sharing a single proxy host) a hostile admin could exfil
// other shards' visitors via this. Restrict to a per-deploy allow-list.
// Empty default = same-origin only (relative paths) — most operators
// host logos under their own gamefiles tree anyway. Add upstream CDNs
// (e.g. `cdn.discordapp.com,i.imgur.com`) here only if you trust them.
const ENV_LOGO_ALLOWED_HOSTS = (process.env.LOGO_ALLOWED_HOSTS ?? '')
  .split(',')
  .map((s) => s.trim().toLowerCase())
  .filter(Boolean);
export const LOGO_ALLOWED_HOSTS = ENV_LOGO_ALLOWED_HOSTS;   // back-compat snapshot
/** Live logo-host allow-list (runtime config file overrides env when non-empty). */
export function getLogoAllowedHosts(): string[] {
  const f = getRuntimeConfigFile().logoAllowedHosts;
  return (Array.isArray(f) && f.length > 0)
    ? f.map((s) => String(s).trim().toLowerCase()).filter(Boolean)
    : ENV_LOGO_ALLOWED_HOSTS;
}

// IS_PRODUCTION declared above the DEV_MODE block (audit R2-M-1).

// ── v0.3.14: per-handle rate limit + admin ban-list ───────────────────────────
// Comma-separated CIDR list of IPs exempt from the per-handle WS-upgrade rate
// limit AND from the persistent admin ban-list. Primary purpose: keep smoke
// bots / dev workstations out of their own throttle while exercising
// end-to-end flow. Empty default = no exemptions.
//
//   PROXY_RATE_LIMIT_WHITELIST=10.0.0.0/8,192.168.0.0/16,X.Y.Z.W/32
//
// Bare IPs (no `/N`) are interpreted as `/32` (single host) for IPv4 and
// `/128` for IPv6. Invalid entries are dropped at boot with a warning so a
// typo doesn't silently neuter the whitelist for the rest of the list.
//
// Reload semantics: env var is parsed at boot only — operator must
// `docker compose restart proxy` to apply changes. In DEV_MODE the per-IP
// rate ceiling auto-multiplies by 10 (a 60/min ceiling becomes 600/min) so
// local smoke runs against `localhost:8080` deploys don't starve themselves.
const _whitelistRaw = (process.env.PROXY_RATE_LIMIT_WHITELIST ?? '')
  .split(',')
  .map((s) => s.trim())
  .filter(Boolean);

interface ParsedCidr {
  family: 4 | 6;
  /** Network bytes (length 4 for v4, 16 for v6). */
  net: Buffer;
  /** Prefix length in bits. */
  prefix: number;
}

/** The three app-config allow-lists that more than one writer can set. */
export type AppConfigListKey = 'publicOrigins' | 'logoAllowedHosts' | 'rateLimitWhitelist';

/**
 * Which entries of an app-config allow-list are malformed. Returns the offending raw
 * entries, so each caller can phrase its own refusal.
 *
 * 🚨 TWO WRITERS REACH THESE FIELDS AND THEY DISAGREED. The admin PUT
 * (`/api/admin/app-config`) refused a bad entry with a 400; the config-backup import wrote
 * the same field through with nothing but a `trim`. A restore could therefore install
 * values the editor beside it rejects — and one of them is not inert: `serverRegistry`
 * builds the logo allow-list from `publicOrigins`, and for an entry that is not a valid
 * URL its `catch` falls back to using the RAW STRING as an allowed host. The PUT's
 * validation is what makes that branch unreachable, so the import was the only way to
 * reach it, and what it reaches is the host allow-list for `<img src>` shown to every
 * visitor. Hence one function, called by both, rather than two lists of rules that agree
 * only while somebody remembers to edit them together.
 */
export function invalidAppConfigEntries(key: AppConfigListKey, list: readonly string[]): string[] {
  const bad: string[] = [];
  for (const raw of list) {
    const v = String(raw).trim();
    if (!v) continue;                       // blanks are filtered by the callers, not rejected
    if (key === 'publicOrigins') {
      try {
        const u = new URL(v);
        // A bare origin: scheme + host only. A path, query or fragment means the value can
        // never equal a browser's Origin header, so it is a lockout waiting to happen.
        if ((u.protocol !== 'https:' && u.protocol !== 'http:') || u.pathname !== '/' || u.search || u.hash) bad.push(raw);
      } catch { bad.push(raw); }
    } else if (key === 'logoAllowedHosts') {
      if (!/^[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?$/i.test(v)) bad.push(raw);
    } else if (parseCidr(v) === null) bad.push(raw);
  }
  return bad;
}

function parseCidr(entry: string): ParsedCidr | null {
  const slashIdx = entry.indexOf('/');
  const ipPart = slashIdx >= 0 ? entry.slice(0, slashIdx) : entry;
  const prefixPart = slashIdx >= 0 ? entry.slice(slashIdx + 1) : null;
  const isV4 = /^\d+\.\d+\.\d+\.\d+$/.test(ipPart);
  const isV6 = ipPart.includes(':');
  if (isV4) {
    const parts = ipPart.split('.').map((p) => parseInt(p, 10));
    if (parts.length !== 4 || parts.some((p) => !Number.isInteger(p) || p < 0 || p > 255)) return null;
    const prefix = prefixPart === null ? 32 : parseInt(prefixPart, 10);
    if (!Number.isInteger(prefix) || prefix < 0 || prefix > 32) return null;
    return { family: 4, net: Buffer.from(parts), prefix };
  }
  if (isV6) {
    // Minimal IPv6 parser — accept canonical / compressed forms; reject
    // anything we can't fully resolve.
    const noZone = ipPart.split('%')[0];
    const dblIdx = noZone.indexOf('::');
    let head: string[];
    let tail: string[];
    if (dblIdx >= 0) {
      head = noZone.slice(0, dblIdx).split(':').filter(Boolean);
      tail = noZone.slice(dblIdx + 2).split(':').filter(Boolean);
    } else {
      head = noZone.split(':');
      tail = [];
    }
    const groups = head.length + tail.length;
    if (groups > 8) return null;
    const padding = 8 - groups;
    const all = [...head, ...new Array(padding).fill('0'), ...tail];
    if (all.length !== 8) return null;
    const buf = Buffer.alloc(16);
    for (let i = 0; i < 8; i++) {
      const g = all[i];
      if (!/^[0-9a-fA-F]{1,4}$/.test(g)) return null;
      const v = parseInt(g, 16);
      buf[i * 2]     = (v >> 8) & 0xff;
      buf[i * 2 + 1] = v & 0xff;
    }
    const prefix = prefixPart === null ? 128 : parseInt(prefixPart, 10);
    if (!Number.isInteger(prefix) || prefix < 0 || prefix > 128) return null;
    return { family: 6, net: buf, prefix };
  }
  return null;
}

export const PROXY_RATE_LIMIT_WHITELIST: ParsedCidr[] = [];
for (const raw of _whitelistRaw) {
  const parsed = parseCidr(raw);
  if (parsed) PROXY_RATE_LIMIT_WHITELIST.push(parsed);
  else console.warn(`[Config] WARNING: PROXY_RATE_LIMIT_WHITELIST entry "${raw}" is not a valid CIDR — dropping.`);
}

// Live rate-limit whitelist: the runtime config file's `rateLimitWhitelist`
// (re-parsed only when its contents change) overrides the env-parsed list.
let _fileWlKey = '\0';
let _fileWlParsed: ParsedCidr[] = [];
function liveWhitelist(): ParsedCidr[] {
  const f = getRuntimeConfigFile().rateLimitWhitelist;
  if (Array.isArray(f) && f.length > 0) {
    const key = f.join(',');
    if (key !== _fileWlKey) {
      _fileWlKey = key;
      _fileWlParsed = [];
      for (const raw of f) {
        const p = parseCidr(String(raw).trim());
        if (p) _fileWlParsed.push(p);
        else console.warn(`[Config] WARNING: rateLimitWhitelist entry "${raw}" is not a valid CIDR — dropping.`);
      }
    }
    return _fileWlParsed;
  }
  return PROXY_RATE_LIMIT_WHITELIST;  // env fallback (parsed at boot)
}

/** Check whether an IP literal falls under any whitelist CIDR. Accepts both
 *  v4 and v6 dotted/colon forms. Unrecognised input → not whitelisted. */
export function isIpWhitelisted(ip: string): boolean {
  const whitelist = liveWhitelist();
  if (!ip || whitelist.length === 0) return false;
  // Strip IPv4-mapped IPv6 prefix `::ffff:` so node's req.socket.remoteAddress
  // formatting doesn't break the v4 fast-path.
  const lc = ip.toLowerCase();
  const stripped = lc.startsWith('::ffff:') ? lc.slice('::ffff:'.length) : lc;
  const parsed = parseCidr(stripped);
  if (!parsed) return false;
  for (const allowed of whitelist) {
    if (parsed.family !== allowed.family) continue;
    if (cidrContains(allowed, parsed.net)) return true;
  }
  return false;
}

function cidrContains(cidr: ParsedCidr, addr: Buffer): boolean {
  if (addr.length !== cidr.net.length) return false;
  let bitsLeft = cidr.prefix;
  for (let i = 0; i < cidr.net.length && bitsLeft > 0; i++) {
    if (bitsLeft >= 8) {
      if (addr[i] !== cidr.net[i]) return false;
      bitsLeft -= 8;
    } else {
      const mask = (0xff << (8 - bitsLeft)) & 0xff;
      if ((addr[i] & mask) !== (cidr.net[i] & mask)) return false;
      bitsLeft = 0;
    }
  }
  return true;
}

/** Re-export the parsed CIDR helper for the ban-list module. */
export type { ParsedCidr };
export { parseCidr };

// Per-handle rate limit knobs. See V0.3.X-TODO § v0.3.14 for the design.
//   capacity = max simultaneous burst attempts
//   refillPerMin = sustained attempts/min after the bucket drains
// Defaults sized for real reconnect noise (page refresh, shard switch, tab
// close+reopen) staying well under, while a script hammering reconnects
// trips at attempt 31.
const _rateBurst = parseInt(process.env.PROXY_RATE_LIMIT_BURST ?? '30');
const _rateSustained = parseInt(process.env.PROXY_RATE_LIMIT_PER_MIN ?? '10');
export const RATE_LIMIT_BURST     = (Number.isInteger(_rateBurst) && _rateBurst > 0) ? _rateBurst : 30;
export const RATE_LIMIT_PER_MIN   = (Number.isInteger(_rateSustained) && _rateSustained > 0) ? _rateSustained : 10;
// DEV_MODE auto-multiplies both knobs ×10 so local smoke runs don't starve
// themselves. IS_PRODUCTION deploys keep the strict defaults.
export const RATE_LIMIT_DEV_MULT  = DEV_MODE ? 10 : 1;

// v0.9.137: live overrides for the two rate-limit knobs so they're editable from
// the admin panel (App config card) without a proxy restart — mirrors
// getServerName()/getPublicOrigins()/liveWhitelist(). The runtime config file's
// rateLimitBurst/rateLimitPerMin (a positive int) wins; otherwise the env-derived
// constant. The rate limiter reads these when MINTING a new bucket, so a change
// applies to new connections immediately + to all within the 30-min idle evict.
// RATE_LIMIT_DEV_MULT is applied on top by the limiter, not here.
export function getRateLimitBurst(): number {
  const f = getRuntimeConfigFile().rateLimitBurst;
  return (typeof f === 'number' && Number.isInteger(f) && f > 0) ? f : RATE_LIMIT_BURST;
}
export function getRateLimitPerMin(): number {
  const f = getRuntimeConfigFile().rateLimitPerMin;
  return (typeof f === 'number' && Number.isInteger(f) && f > 0) ? f : RATE_LIMIT_PER_MIN;
}

// ── Discord OAuth2 SSO ────────────────────────────────────────────────────────
// Optional: set these to enable "Sign in with Discord" + server-side settings persistence.
// Leave DISCORD_CLIENT_ID unset to disable Discord SSO (the /auth/ routes will return 503).
export const DISCORD_CLIENT_ID     = process.env.DISCORD_CLIENT_ID     ?? '';
export const DISCORD_CLIENT_SECRET = process.env.DISCORD_CLIENT_SECRET ?? '';
export const DISCORD_REDIRECT_URI  = process.env.DISCORD_REDIRECT_URI  ?? 'http://localhost:8080/auth/discord/callback';
// Random secret for signing JWTs. If unset/weak the proxy auto-generates an
// ephemeral one at boot — every var should be optional (operator directive
// 2026-05-01). Sessions won't survive a proxy restart in that mode; warn
// loudly so operators know to set it explicitly for any real deploy.
import { randomBytes } from 'crypto';
const PLACEHOLDER_JWT_SECRET = 'change-me-generate-a-real-secret';
function resolveJwtSecret(): string {
  const supplied = process.env.JWT_SECRET ?? '';
  if (supplied && supplied !== PLACEHOLDER_JWT_SECRET && supplied.length >= 32) {
    return supplied;
  }
  // randomBytes is sync and cheap (~µs); doing it at module-load time
  // means JWT_SECRET is a stable string for the lifetime of the process,
  // which is all that's needed for HMAC sign/verify symmetry.
  return randomBytes(32).toString('hex');
}
export const JWT_SECRET = resolveJwtSecret();
const JWT_SECRET_IS_EPHEMERAL = !process.env.JWT_SECRET
  || process.env.JWT_SECRET === PLACEHOLDER_JWT_SECRET
  || process.env.JWT_SECRET.length < 32;

/**
 * Surface configuration health at boot. Operator directive 2026-05-01:
 * every env var is optional, sensible defaults, no refuse-to-start gates.
 * v0.3.13 audit MEDIUM-2.2 carves out ONE exception: in production, an
 * ephemeral `JWT_SECRET` is silently broken (every restart invalidates
 * all sessions; a 2-replica deploy signs JWTs with two keys and auth
 * fails probabilistically). Dev / single-replica self-host stays
 * permissive — the audit fix is fail-closed for IS_PRODUCTION only.
 */
export function assertSecretsConfigured(): void {
  const warnings: string[] = [];
  if (JWT_SECRET_IS_EPHEMERAL && IS_PRODUCTION) {
    console.error(
      '[Config] FATAL: JWT_SECRET is unset or weak in production. An ' +
      'ephemeral random secret silently breaks sessions across restarts ' +
      'and across multi-replica deploys (each replica signs with a ' +
      'different key, verify fails ~50%% of the time). Set ' +
      'JWT_SECRET=<32+ hex chars> in .env (the install scripts emit a ' +
      'random one). Refusing to start.',
    );
    process.exit(1);
  }
  if (JWT_SECRET_IS_EPHEMERAL) {
    warnings.push(
      'JWT_SECRET is unset or weak — generated an ephemeral one. Sessions ' +
      'will NOT survive proxy restart. Set JWT_SECRET=<32+ hex chars> in .env ' +
      'to persist sessions across reboots.',
    );
  }
  if (DISCORD_CLIENT_ID && !DISCORD_CLIENT_SECRET) {
    warnings.push(
      'DISCORD_CLIENT_ID is set but DISCORD_CLIENT_SECRET is empty — Discord ' +
      'SSO will fail. Either set both or unset both (which disables SSO).',
    );
  }

  // UO_HOST footgun is now defused by the server registry — the legacy
  // env vars are only read as a last-resort fallback when no servers/*.yaml
  // is mounted. We still warn about the well-known bad values in production
  // for the benefit of operators upgrading from a pre-v0.3.0 deploy.
  if (IS_PRODUCTION) {
    if (UO_SERVER_HOST === 'host.docker.internal' || UO_SERVER_HOST === '0.0.0.0') {
      warnings.push(
        `Legacy UO_HOST="${UO_SERVER_HOST}" in production — used only if no ` +
        'servers/*.yaml is mounted. Verify the registry is the source of truth.',
      );
    }
    if (PUBLIC_ORIGINS.length === 0) {
      warnings.push(
        'PUBLIC_ORIGINS is empty in production — Origin-checked state-changing ' +
        'endpoints FAIL CLOSED: every cross-origin or Origin-less write is rejected ' +
        '(your own site included). Set PUBLIC_ORIGINS=https://your.domain to allow it.',
      );
    }
  }

  for (const w of warnings) console.warn('[Config] WARNING: ' + w);
  // No errors[] — every var is optional in v0.3.0+. Bad config produces
  // warnings + degraded features, never refuse-to-start. The proxy is
  // expected to boot even with a completely empty .env.
}
