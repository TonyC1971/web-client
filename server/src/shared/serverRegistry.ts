// servers/*.yaml registry — loaded once at boot, hot-reloaded by admin API.
//
// Each YAML file declares one UO shard the proxy is willing to bridge to.
// The full record (host/port/gamefilesPath) stays server-side; the browser
// only sees the public-safe view.
//
// Two sources are merged at load time:
//   SERVERS_DIR      — operator-managed YAMLs (read-only named volume from
//                      servers-init.sh). Source of truth for static shards.
//   DATA_SERVERS_DIR — admin-API-written YAMLs (writable proxy-data volume).
//                      Admin files take precedence over operator files for the
//                      same slug. New servers created via the admin API live
//                      exclusively here.
//
// Backward compat: if both dirs are empty/missing, a single virtual `default`
// shard is synthesised from the legacy UO_HOST / UO_PORT / ASSETS_PATH env
// vars so existing single-shard deploys keep working without touching anything.

import * as fs from 'fs';
import * as path from 'path';
import { stringify as yamlStringify } from 'yaml';
import { parse as parseYaml } from 'yaml';
import {
  UO_SERVER_HOST,
  UO_SERVER_PORT,
  ASSETS_PATH,
  SERVERS_DIR,
  DATA_SERVERS_DIR,
  GAMEFILES_ROOT,
  getPublicOrigins,
  getLogoAllowedHosts,
} from './config.js';
import { getAll as getDeletionQueue, daysRemaining } from './deletionQueue.js';
import { seed as seedIdRegistry } from './idRegistry.js';
import { isPrivateOrReservedIp } from './netGuard.js';

// ── Types ─────────────────────────────────────────────────────────────────────

export type EncryptType =
  | 'none'
  | 'old_bfish'
  | 'blowfish_1_25_36'
  | 'blowfish'
  | 'blowfish_2_0_3'
  | 'twofish_md5';

export type RegionCode = 'EU' | 'NA' | 'AS' | 'SA' | 'OC';
export type EraCode    = 'T2A' | 'UOR' | 'LBR' | 'AOS' | 'SE' | 'ML' | 'SA' | 'HS';

export const VALID_ENCRYPT_SET = new Set<string>([
  'none', 'old_bfish', 'blowfish_1_25_36', 'blowfish', 'blowfish_2_0_3', 'twofish_md5',
]);
const VALID_REGIONS = new Set<string>(['EU', 'NA', 'AS', 'SA', 'OC']);
// LBR (Lord Blackthorn's Revenge, 2002) sits chronologically between T2A
// (1998) and AOS (2003) — added in v0.4.22 because eternal.yaml uses it
// and it was being SKIP'd at registry load time, collapsing the picker
// to a single shard which auto-selected.
const VALID_ERAS    = new Set<string>(['T2A', 'UOR', 'LBR', 'AOS', 'SE', 'ML', 'SA', 'HS']);

// v0.8.43: ports a SELF-SERVICE owner may NOT target (the proxy would relay raw
// TCP from our IP to them). Common sensitive services — a UO shard never uses
// these, so blocking them costs nothing and shuts the open-relay abuse.
const SELF_SERVICE_BLOCKED_PORTS = new Set<number>([
  19, 20, 21, 22, 23, 25, 43, 53, 69, 79, 110, 111, 119, 123, 135, 137, 138, 139,
  143, 161, 162, 179, 389, 443, 445, 465, 514, 515, 543, 544, 548, 587, 631, 636,
  873, 990, 993, 995, 1080, 1433, 1521, 2049, 2375, 2376, 3128, 3306, 3389, 4444,
  5432, 5601, 5672, 5900, 5984, 6379, 7001, 8086, 9000, 9092, 9200, 9300, 11211,
  15672, 25565, 27017, 27018, 50070,
]);

export interface ServerRecord {
  id: number;
  slug: string;
  displayName: string;
  description: string;
  host: string;
  port: number;
  gamefilesPath: string;
  encrypt: EncryptType;
  clientVersion: string;
  gamefilesUrlBase: string;
  emulator?: string;
  region?: RegionCode;
  era?: EraCode;
  logo?: string;
  website?: string;
  discord?: string;
  createdAt?: number;
  /** v0.7.1: pin this shard to a specific WASM bundle ('cuo' | 'tuo' | …).
   *  Picker shows a "This server requires …" badge and routes the click
   *  to that bundle regardless of the toggle. Omit for no pin. */
  forceClient?: string;
  /** v0.3.14: per-shard "Discord-only" flag. Default true. When false the
   *  proxy refuses WS upgrades whose JWT lacks a Discord ID for that
   *  slug. Pairs with the v0.3.14 ban-list — operators can drop ban-
   *  resistant guest abuse on a single shard without affecting others. */
  guestsAllowed: boolean;
  /** v0.3.15: WebIdentity 0xA4 preamble. When `enabled: true` the proxy
   *  emits a 149-byte 0xA4 frame on the upstream TCP socket as the FIRST
   *  bytes (before any client 0x80/0x91), carrying the real client IP +
   *  shared secret. Shards with the upstream RunUO-like reference impl
   *  installed (or our Sphere patch) read the IP and use it for all
   *  per-IP heuristics. Default `enabled: false` (kept off until both
   *  proxy YAML + shard handler are configured with matching secrets).
   *  Spec: `docs/WEBIDENTITY.md`. */
  webIdentity?: {
    enabled: boolean;
    /** ASCII shared secret. SAME value on proxy + shard. Required when
     *  `enabled: true`; min 16 chars (upstream reference impl uses 32+
     *  but accepts shorter — we set the floor at 16 to keep operators
     *  honest without being onerous). */
    secret: string;
  };
  /** v0.9.224 (mini auto-login per-shard gate): true = this shard runs the
   *  mini auto-login handshake — the proxy binds a mini account (g<hex>/d<id>),
   *  stamps role=mini-guest/mini-player into the 0xA4, and rewrites the 0x80
   *  AccountLogin so the shard's Custom/ MiniAutoLogin.cs auto-creates the
   *  account+char. Operator/normal shards (eternal → cuo/tuo) leave this UNSET
   *  so their Discord/guest sessions are NEVER rewritten. Replaces the global
   *  `MINI_AUTOLOGIN` env, which is unsafe on a proxy that serves both the mini
   *  and the regular webclient. The env still force-enables it for legacy/test. */
  autologin?: boolean;
  /** v0.8.43 (self-service shards): when set, the WASM client fetches this
   *  shard's gamefiles DIRECTLY from this external https base instead of our
   *  pool — the owner hosts their own files (zero bytes/disk on our side).
   *  Their root must serve a `manifest.json` (name→sha256) AND CORS headers.
   *  Mutually exclusive with our pool: a shard either lives in our pool
   *  (sponsored / operator-uploaded) or points here. */
  externalGamefilesUrl?: string;
  /** v0.8.43: true = created by a non-supreme owner via the self-service flow
   *  (auto-live). Carries STRICTER rules than operator shards: host must be a
   *  PUBLIC address (no RFC1918/loopback — the proxy bridges TCP to it) and
   *  gamefiles must be external. Operator shards leave this unset. */
  selfService?: boolean;
  /** v0.8.43: operator-blessed. true = WE host its files in our pool AND
   *  recommend it (picker "Sponsored" badge + ranking boost). Supreme-only;
   *  never settable by an owner. */
  sponsored?: boolean;
  /** v0.9.516 (operator 2026-07-28): the shard EXISTS and is configured but must not
   *  be shown to anyone -- "no está listo o no quiero que se muestre pero esté ahí".
   *  Same visibility rule as `pending` -- dropped from the public list for everyone
   *  except the shard's own owner, who keeps it badged "Hidden" -- but a different
   *  reason: `pending` is self-service moderation awaiting approval, `hidden` is a
   *  deliberate decision to keep a working shard out of sight. Supreme-only, like
   *  `sponsored`. */
  hidden?: boolean;
  /** v0.8.73: self-service shard awaiting operator approval. A new
   *  registration lands with `pending: true`; it is HIDDEN from the public
   *  picker (the owner still sees it badged "Pending review") and cannot be
   *  joined until a general admin approves it (clears the flag) or rejects it
   *  (deletes it). Only the approve endpoint clears this — never an owner PUT. */
  pending?: boolean;
  /** v0.9.131 (audit 2026-06-21): the host/port were last set by an UNTRUSTED
   *  (owner-tier) editor on a shard that is NOT selfService — e.g. a sponsored
   *  pool-migration that dropped selfService while the owner kept edit rights, or
   *  an operator shard a supreme delegated to a `servers:write:own` owner. The
   *  SSRF/relay defenses (public-host gate + blocked-port denylist here, and the
   *  connect-time resolve-pin in UOProxy/serverStatus/shardCooldown) keyed ONLY on
   *  `selfService`, so an owner could repoint such a shard at an internal address.
   *  AssetServer marks this true whenever a non-general editor changes host/port;
   *  every SSRF gate now fires on `selfService || untrustedHost`. A general admin
   *  re-setting host/port clears it (they vouch for the target). Operator shards
   *  never touched by an owner stay unset (they may legitimately use LAN hosts). */
  untrustedHost?: boolean;
}

/** Public-safe projection returned by /api/servers. */
export interface PublicServer {
  id: number;
  slug: string;
  displayName: string;
  description: string;
  clientVersion: string;
  gamefilesUrlBase: string;
  encrypt: EncryptType;
  emulator?: string;
  region?: RegionCode;
  era?: EraCode;
  logo?: string;
  website?: string;
  discord?: string;
  createdAt?: number;
  /** v0.7.1: per-shard WASM-bundle pin ('cuo' | 'tuo' | …). When set the
   *  picker shows a "This server requires …" badge and routes clicks
   *  to that bundle. Omitted = no pin (visitor's toggle wins). */
  forceClient?: string;
  /** v0.3.14: surfaced so the picker can warn before the user clicks
   *  through (no Discord = WS refused on this slug). */
  guestsAllowed: boolean;
  /** v0.8.43: external gamefiles base (owner-hosted shards). The client
   *  fetches gamefiles from here instead of our pool. Absent = our pool. */
  externalGamefilesUrl?: string;
  /** v0.8.43: community-hosted (owner self-service) — picker shows a neutral
   *  "Community" note; files are not ours. */
  selfService?: boolean;
  /** v0.8.43: operator-blessed "Sponsored" badge (we host + recommend). */
  sponsored?: boolean;
  /** v0.9.516: operator-hidden. Dropped from the public list for everyone except
   *  the shard's own owner, who keeps it badged "Hidden" (v0.9.519). */
  hidden?: boolean;
  /** v0.8.73: awaiting operator approval (self-service). Surfaced ONLY to the
   *  owner's own view so the picker can badge it "Pending review"; the public
   *  list omits pending shards entirely. */
  pending?: boolean;
  /** 'active' | 'pending_deletion' */
  status: string;
  /** Days left in 7-day grace period, only present when status === 'pending_deletion'. */
  daysUntilDeletion?: number;
}

// ── Validation ────────────────────────────────────────────────────────────────

// Exported so every path that validates a SHARD SLUG uses this one. useCases.ts already imported
// URL_BASE_REGEX/VALID_ENCRYPT_SET/CLIENT_VERSION_REGEX from here "so the two paths can never
// drift" — but this one was not exported, so its slug check fell back to a local id pattern that
// allowed 40 chars against this 32. The promise only held for the fields that were reachable.
export const SLUG_REGEX  = /^[a-z0-9][a-z0-9-]{0,31}$/;

/**
 * May this shard appear in somebody's picker?
 *
 * 🚨 EXTRACTED SO IT CAN BE ASKED. It was an inline `.filter` in the listing route, and mutation
 * showed that deleting the `!r.hidden` half went unnoticed by the whole suite: an operator-hidden
 * shard would have been listed to everyone, which is the one thing hiding it is for.
 *
 * The rule is a small truth table and every row of it is a deliberate decision, so it is written
 * out rather than left to be re-derived from an `||` chain:
 *
 *   ordinary        → everyone
 *   hidden          → its OWNER (badged "Hidden") and a SUPREME admin; nobody else
 *   pending         → its OWNER only (badged "Pending review")
 *   pending, supreme→ NO. Approval is a moderation queue with its own screen; unreviewed shards
 *                     in a supreme's picker would be noise, not a feature.
 *
 * The supreme exception is scoped to `admins:manage`, NOT `servers:write`, so a per-shard editor
 * gains nothing from it.
 */
export function isVisibleInPicker(
  r: { slug: string; pending?: boolean; hidden?: boolean },
  ownedSlugs: Set<string>,
  isSupremeViewer: boolean,
): boolean {
  if (ownedSlugs.has(r.slug)) return true;
  if (r.pending) return false;
  return !r.hidden || isSupremeViewer;
}
export const CLIENT_VERSION_REGEX = /^\d{1,2}\.\d{1,3}\.\d{1,3}(\.\d{1,3})?$/;
// Up to 63 chars so an auto-generated `files-<slug>-<hash>` base fits (slug is
// itself up to 32 chars). Still strictly [a-z0-9-] → Linux-path- and URL-safe.
export const URL_BASE_REGEX     = /^[a-z0-9][a-z0-9-]{0,62}$/;
const DEFAULT_CLIENT_VERSION   = '7.0.45.1';
const DEFAULT_GAMEFILES_URL_BASE = 'gamefiles';

/**
 * May a directory of this name be RECURSIVELY DELETED under GAMEFILES_ROOT?
 *
 * 🚨 THIS IS THE GUARD IN FRONT OF AN `rm -rf`, and it was written out THREE times — in the
 * force-delete route, in the hourly expiry cleanup, and in the shard-update path that prunes an old
 * gamefiles dir when the base changes. Three copies of a destructive predicate is the shape this
 * codebase keeps paying for: relax one and nothing tells you the others were not relaxed, and the
 * failure mode here is deleting somebody else's files.
 *
 * It answers a narrow question on purpose. A per-shard gamefiles directory is either the legacy
 * `server-<id>` form or the content-addressed `files-<slug>-<hash>` one; anything else — `pool`,
 * `..`, an empty string, a name with a separator — is either shared infrastructure or an attempt to
 * leave the root, and neither is ours to remove. The name is only ever taken from a stored record,
 * so this is defence in depth over the registration-time validation; the point of defence in depth
 * is that it holds when the layer above is wrong.
 */
export function isPerShardGamefilesDir(name: unknown): boolean {
  return typeof name === 'string' && /^(server-\d+|files-[a-z0-9][a-z0-9-]*)$/.test(name);
}

function asString(v: unknown, field: string, file: string): string {
  if (typeof v !== 'string' || v.length === 0) {
    throw new Error(`${file}: '${field}' must be a non-empty string`);
  }
  return v;
}

// v0.3.x audit M-2: validate the `host` field's *format*. Pre-fix any
// non-empty string was accepted, so a YAML edit could point a shard at an
// arbitrary string and — worse — at the cloud-metadata link-local address
// (169.254.169.254) or any 169.254.0.0/16 host, turning the proxy's
// upstream TCP connect into an SSRF primitive. RFC1918 / loopback /
// host.docker.internal are intentionally NOT rejected — operators
// legitimately point shards at LAN IPs and the docker host.
const HOSTNAME_LABEL_REGEX = /^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$/;
const IPV4_REGEX = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;

function isIpv4Literal(s: string): boolean {
  const m = IPV4_REGEX.exec(s);
  if (!m) return false;
  return m.slice(1, 5).every((o) => {
    const n = Number(o);
    return n >= 0 && n <= 255 && String(n) === o;
  });
}

function isIpv6Literal(s: string): boolean {
  // Accept bracketed or bare form; require at least one ':' and only hex
  // digits / colons / a single optional embedded IPv4 tail.
  const inner = s.startsWith('[') && s.endsWith(']') ? s.slice(1, -1) : s;
  if (!inner.includes(':')) return false;
  if (!/^[0-9a-fA-F:.]+$/.test(inner)) return false;
  if ((inner.match(/::/g) ?? []).length > 1) return false;
  return true;
}

function validateHost(host: string, file: string): void {
  const h = host.trim();
  if (h.length === 0) {
    throw new Error(`${file}: 'host' must be a non-empty string`);
  }
  const isIpv4 = isIpv4Literal(h);
  const isIpv6 = isIpv6Literal(h);
  if (!isIpv4 && !isIpv6 && !HOSTNAME_LABEL_REGEX.test(h)) {
    throw new Error(
      `${file}: host='${host}' is not a valid hostname or IPv4/IPv6 literal`,
    );
  }
  // Reject link-local 169.254.0.0/16 — covers the cloud-metadata IP
  // 169.254.169.254. SSRF-to-metadata is the canonical proxy abuse here.
  if (isIpv4 && /^169\.254\./.test(h)) {
    throw new Error(
      `${file}: host='${host}' is in the link-local range 169.254.0.0/16 ` +
      `(cloud-metadata SSRF surface) and is not allowed`,
    );
  }
}

// v0.8.43: STRICTER host gate for self-service (owner-created) shards. The
// proxy opens a TCP socket to this host — for an OPERATOR shard a LAN IP is
// legitimate, but an UNTRUSTED owner must NOT be able to make the proxy reach
// into our private network (NAS, ModernUO, DSM, other shards). Reject every
// non-public address: loopback, RFC1918, CGNAT, link-local, ULA, and bare
// localhost. Public hostnames + public IPs only.
export function assertPublicHost(host: string): void {
  const h = host.trim().toLowerCase().replace(/^\[|\]$/g, '');
  if (h === 'localhost' || h === 'host.docker.internal' || h.endsWith('.localhost') || h.endsWith('.local') || h.endsWith('.internal')) {
    throw new Error(`host='${host}' resolves to a private/local name — self-service shards must use a public address`);
  }
  // IP literal → delegate to the canonical resolved-address classifier. The old
  // inline IPv6 check string-matched only the dotted-decimal embedded form
  // (::ffff:192.168.x) and missed the HEX form (::ffff:c0a8:xxxx) of an IPv4-mapped
  // address (audit 2026-06-21 HIGH-SSRF). One classifier = no future divergence;
  // it also covers the extra reserved IPv4 ranges the inline check omitted.
  if (isIpv4Literal(h) || isIpv6Literal(h)) {
    if (isPrivateOrReservedIp(h)) {
      throw new Error(`host='${host}' is a private/reserved address — self-service shards must use a public address`);
    }
  }
}

function asInt(v: unknown, field: string, file: string): number {
  if (typeof v !== 'number' || !Number.isInteger(v) || v <= 0 || v > 65535) {
    throw new Error(`${file}: '${field}' must be an integer 1..65535`);
  }
  return v;
}

function asPositiveInt(v: unknown, field: string, file: string): number {
  if (typeof v !== 'number' || !Number.isInteger(v) || v <= 0) {
    throw new Error(`${file}: '${field}' must be a positive integer`);
  }
  return v;
}

// v0.3.13 audit R2-L-1: vet shard logo URLs against an allow-list before
// the picker renders them as `<img src=...>`. Pre-fix any https:// URL
// was accepted, turning a YAML edit into a visitor-tracking beacon
// (logo URL fetched by every visitor's browser).
//   - Empty / undefined / wrong type → drop silently.
//   - Relative path under `/<safe-chars>/...` → keep (same-origin).
//   - Absolute https URL whose host is in PUBLIC_ORIGINS or
//     LOGO_ALLOWED_HOSTS → keep.
//   - Anything else → drop silently (the picker just renders no logo).
function validateLogoUrl(raw: string): string | undefined {
  const s = raw.trim();
  if (s === '' || s.length > 256) return undefined;
  // Same-origin relative path. Allow only [A-Za-z0-9._-/]; rejects
  // protocol-relative (`//evil.example`), query strings, fragments, etc.
  if (/^\/[A-Za-z0-9._\-/]{1,250}$/.test(s) && !s.includes('//') && !s.includes('..')) {
    return s;
  }
  if (s.startsWith('https://')) {
    let u: URL;
    try { u = new URL(s); } catch { return undefined; }
    const host = u.host.toLowerCase();
    const allowed = [
      ...getPublicOrigins().map((origin) => {
        try { return new URL(origin).host.toLowerCase(); } catch { return origin.toLowerCase(); }
      }),
      ...getLogoAllowedHosts(),
    ];
    if (allowed.includes(host)) return s;
  }
  return undefined;
}

function validate(raw: Partial<ServerRecord>, file: string): ServerRecord {
  const id          = asPositiveInt((raw as Record<string, unknown>).id, 'id', file);
  const slug        = asString(raw.slug, 'slug', file);
  if (!SLUG_REGEX.test(slug)) {
    throw new Error(`${file}: slug '${slug}' must match /^[a-z0-9][a-z0-9-]{0,31}$/`);
  }
  const displayName = asString(raw.displayName, 'displayName', file);
  const description = typeof raw.description === 'string' ? raw.description : '';
  const host        = asString(raw.host, 'host', file);
  validateHost(host, file);
  const port        = asInt(raw.port, 'port', file);
  // v0.8.43: external-gamefiles (owner-hosted) shards never touch our pool, so
  // gamefilesPath is irrelevant for them — accept a placeholder and skip the
  // under-root check. Operator/pool shards keep the strict gate below.
  const hasExternalGf = typeof (raw as Record<string, unknown>).externalGamefilesUrl === 'string'
    && ((raw as Record<string, unknown>).externalGamefilesUrl as string).trim() !== '';
  let gamefilesPath: string;
  if (hasExternalGf) {
    gamefilesPath = typeof raw.gamefilesPath === 'string' && raw.gamefilesPath ? raw.gamefilesPath : '(external)';
  } else {
    gamefilesPath = asString(raw.gamefilesPath, 'gamefilesPath', file);
    // v0.3.13 audit R2-M-3: gamefilesPath becomes the bind-mount root that
    // build-pool.mjs walks/hashes/copies into the public pool, and that
    // nginx serves under `/server-<id>/...`. An admin POST/PUT (anyone in the
    // app-config.json `admins` map, plus dev-tester when DEV_ADMIN=1)
    // could otherwise set this to `/etc` or `../../proc/self/environ`,
    // turning the next pool build into a public-read primitive on host
    // files. Restrict to GAMEFILES_ROOT subdirs (current docker layout)
    // or the legacy ASSETS_PATH for the single-shard fallback.
    const resolvedGfp = path.resolve(gamefilesPath);
    const allowedRoots = [
      GAMEFILES_ROOT ? path.resolve(GAMEFILES_ROOT) : null,
      path.resolve(ASSETS_PATH),
    ].filter((r): r is string => typeof r === 'string' && r.length > 0);
    const isUnderAllowedRoot = allowedRoots.some(
      (root) => resolvedGfp === root || resolvedGfp.startsWith(root + path.sep),
    );
    if (!isUnderAllowedRoot) {
      throw new Error(
        `${file}: gamefilesPath='${gamefilesPath}' must be under ` +
        `GAMEFILES_ROOT (${GAMEFILES_ROOT ?? '<unset>'}) or ASSETS_PATH (${ASSETS_PATH}); ` +
        `resolved to '${resolvedGfp}'.`,
      );
    }
  }

  const encryptRaw = typeof raw.encrypt === 'string'
    ? raw.encrypt.toLowerCase().trim() : 'none';
  if (!VALID_ENCRYPT_SET.has(encryptRaw)) {
    throw new Error(
      `${file}: encrypt='${encryptRaw}' is not valid. ` +
      `Accepted: ${Array.from(VALID_ENCRYPT_SET).join(', ')}.`,
    );
  }
  const encrypt = encryptRaw as EncryptType;

  let clientVersion = DEFAULT_CLIENT_VERSION;
  if (raw.clientVersion !== undefined && raw.clientVersion !== null && raw.clientVersion !== '') {
    if (typeof raw.clientVersion !== 'string' || !CLIENT_VERSION_REGEX.test(raw.clientVersion)) {
      throw new Error(
        `${file}: clientVersion='${String(raw.clientVersion)}' must be MAJOR.MINOR.PATCH[.BUILD]`,
      );
    }
    clientVersion = raw.clientVersion;
  }

  let gamefilesUrlBase = DEFAULT_GAMEFILES_URL_BASE;
  if (raw.gamefilesUrlBase !== undefined && raw.gamefilesUrlBase !== null && raw.gamefilesUrlBase !== '') {
    if (typeof raw.gamefilesUrlBase !== 'string' || !URL_BASE_REGEX.test(raw.gamefilesUrlBase)) {
      throw new Error(
        `${file}: gamefilesUrlBase='${String(raw.gamefilesUrlBase)}' must match /^[a-z0-9][a-z0-9-]{0,62}$/`,
      );
    }
    // A base ending in `-web` would collide with the brotli-twin dir convention
    // (`<base>-web`): the asset-worker treats any `*-web` dir as a twin and skips
    // it, so a `-web` base would never get compressed and could shadow another
    // shard's twins. Reserve the suffix.
    if (raw.gamefilesUrlBase.endsWith('-web')) {
      throw new Error(`${file}: gamefilesUrlBase='${raw.gamefilesUrlBase}' must not end in '-web' (reserved for brotli-twin dirs)`);
    }
    gamefilesUrlBase = raw.gamefilesUrlBase;
  }

  // Optional metadata fields
  const emulator = typeof raw.emulator === 'string' && raw.emulator.length > 0
    ? raw.emulator : undefined;

  let region: RegionCode | undefined;
  const regionInput = (raw as Record<string, unknown>).region;
  if (regionInput != null && regionInput !== '') {
    const r = String(regionInput).toUpperCase().trim();
    if (!VALID_REGIONS.has(r)) {
      throw new Error(
        `${file}: region='${String(regionInput)}' is not valid. Accepted: ${Array.from(VALID_REGIONS).join(', ')}.`,
      );
    }
    region = r as RegionCode;
  }

  let era: EraCode | undefined;
  const eraInput = (raw as Record<string, unknown>).era;
  if (eraInput != null && eraInput !== '') {
    const e = String(eraInput).toUpperCase().trim();
    if (!VALID_ERAS.has(e)) {
      throw new Error(
        `${file}: era='${String(eraInput)}' is not valid. Accepted: ${Array.from(VALID_ERAS).join(', ')}.`,
      );
    }
    era = e as EraCode;
  }

  const logo = typeof raw.logo === 'string' ? validateLogoUrl(raw.logo) : undefined;

  // Optional click-through links (website / Discord) shown in the picker panel.
  // Never fetched server-side — just scheme + length sanitised (https only, so a
  // javascript: URL can't ride into the panel's <a href>). Bad input → dropped.
  const extUrl = (v: unknown): string | undefined => {
    if (typeof v !== 'string') return undefined;
    const s = v.trim();
    if (!s) return undefined;
    if (!/^https:\/\/[^\s]{1,300}$/i.test(s)) return undefined;
    return s;
  };
  const website = extUrl((raw as Record<string, unknown>).website);
  const discord = extUrl((raw as Record<string, unknown>).discord);
  const caRaw = (raw as Record<string, unknown>).createdAt;
  const createdAt = typeof caRaw === 'number' && isFinite(caRaw) && caRaw > 0 ? Math.floor(caRaw) : undefined;

  // v0.7.1: forceClient — per-shard pin to a specific WASM bundle. When
  // set the picker shows a "This server requires <ClientName>" badge and
  // routes the click to that bundle no matter which client is currently
  // mounted or what the user's #client-toggle says. Optional, default
  // undefined = use whatever the visitor picked. Accepted values are
  // intentionally open-ended (lowercase tokens) so new clients added in
  // the future ("outlands", "razor"…) don't need a registry edit — we
  // only validate it's a plain identifier, the front-end maps it to a
  // bundle URL. Unknown tokens just degrade to "no force, ignore badge".
  let forceClient: string | undefined;
  const fcRaw = (raw as Record<string, unknown>).forceClient;
  if (fcRaw !== undefined && fcRaw !== null && fcRaw !== '') {
    if (typeof fcRaw !== 'string' || !/^[a-z][a-z0-9-]{0,15}$/.test(fcRaw)) {
      throw new Error(
        `${file}: forceClient='${String(fcRaw)}' must be a short lowercase identifier ` +
        `(letters, digits, hyphen — e.g. 'cuo', 'tuo'). Omit the field for no pin.`,
      );
    }
    forceClient = fcRaw;
  }

  // v0.3.14: guestsAllowed defaults to true (preserve pre-v0.3.14 behaviour).
  // Explicit false rejects WS upgrades from non-Discord JWTs for this slug.
  let guestsAllowed = true;
  const guestsRaw = (raw as Record<string, unknown>).guestsAllowed;
  if (guestsRaw !== undefined) {
    if (typeof guestsRaw !== 'boolean') {
      throw new Error(`${file}: guestsAllowed='${String(guestsRaw)}' must be a boolean (true/false)`);
    }
    guestsAllowed = guestsRaw;
  }

  // v0.3.15: webIdentity opt-in. When `enabled: true` the proxy emits the
  // 0xA4 preamble carrying real client IP + shared secret. Spec mirror of
  // ClassicUO/packets/WebIdentity.ksy — see docs/WEBIDENTITY.md.
  let webIdentity: { enabled: boolean; secret: string } | undefined;
  const wiRaw = (raw as Record<string, unknown>).webIdentity;
  if (wiRaw !== undefined) {
    if (wiRaw === null || typeof wiRaw !== 'object' || Array.isArray(wiRaw)) {
      throw new Error(`${file}: webIdentity must be a YAML mapping (got ${typeof wiRaw})`);
    }
    const wiObj = wiRaw as Record<string, unknown>;
    if (typeof wiObj.enabled !== 'boolean') {
      throw new Error(`${file}: webIdentity.enabled must be a boolean (got ${typeof wiObj.enabled})`);
    }
    const enabled = wiObj.enabled;
    const secret = typeof wiObj.secret === 'string' ? wiObj.secret : '';
    // Fail-closed when enabled but secret missing/short — silently
    // disabling on a typo would neuter the defence without warning.
    if (enabled && secret.length < 16) {
      throw new Error(
        `${file}: webIdentity.enabled is true but secret is missing or shorter than 16 chars. ` +
        `Set webIdentity.secret to the SAME value configured on the shard's ClassicUO.WebIdentitySecret.`
      );
    }
    if (enabled && /[^\x20-\x7E]/.test(secret)) {
      throw new Error(`${file}: webIdentity.secret must contain only printable ASCII characters`);
    }
    webIdentity = { enabled, secret };
  }

  // v0.9.224: per-shard mini auto-login gate (see ServerRecord.autologin).
  // Only the mini/AoS shard sets `autologin: true`; operator shards omit it so
  // their cuo/tuo Discord+guest sessions are never rewritten.
  const autologin = (raw as Record<string, unknown>).autologin === true;

  // v0.8.43: self-service / external-gamefiles / sponsored.
  const selfService = (raw as Record<string, unknown>).selfService === true;
  const sponsored   = (raw as Record<string, unknown>).sponsored === true;
  // v0.8.73: awaiting operator approval (self-service moderation).
  const pending     = (raw as Record<string, unknown>).pending === true;
  // v0.9.516: operator-hidden shard (see field doc). Same shape as sponsored/pending.
  const hidden      = (raw as Record<string, unknown>).hidden === true;
  // v0.9.131: host/port last set by an untrusted owner-tier editor (see field doc).
  const untrustedHost = (raw as Record<string, unknown>).untrustedHost === true;

  let externalGamefilesUrl: string | undefined;
  const egfRaw = (raw as Record<string, unknown>).externalGamefilesUrl;
  if (egfRaw !== undefined && egfRaw !== null && egfRaw !== '') {
    if (typeof egfRaw !== 'string') throw new Error(`${file}: externalGamefilesUrl must be a string`);
    const v = egfRaw.trim();
    let u: URL;
    try { u = new URL(v); } catch { throw new Error(`${file}: externalGamefilesUrl='${v}' is not a valid URL`); }
    if (u.protocol !== 'https:') throw new Error(`${file}: externalGamefilesUrl must be https`);
    if (u.search || u.hash) throw new Error(`${file}: externalGamefilesUrl must not carry a query or fragment`);
    if (v.length > 300) throw new Error(`${file}: externalGamefilesUrl too long`);
    // The browser fetches gamefile BYTES from here, but our /manifest endpoint
    // ALSO fetches manifest.json server-side — so the host must be public to
    // keep that fetch out of our private network (SSRF). Same gate as the host.
    assertPublicHost(u.hostname);
    externalGamefilesUrl = v.replace(/\/+$/, ''); // normalise: no trailing slash
  }

  // selfService and sponsored are mutually exclusive (self-hosted vs we-host-it).
  if (selfService && sponsored) {
    throw new Error(`${file}: a shard cannot be both selfService and sponsored`);
  }
  // Harden the TCP-bridge target to a public address whenever it is OWNER-
  // controlled — either a self-service shard OR a non-self-service shard whose
  // host/port an owner-tier editor set (untrustedHost; see field doc + audit
  // 2026-06-21). Gamefiles come EITHER from an external https URL the owner hosts,
  // OR — when the operator enables owner uploads — from our pool. v0.8.78: external
  // is therefore OPTIONAL (pool-mode allowed). Operator shards never touched by an
  // owner leave both flags unset and keep their legitimate LAN/docker hosts.
  if (selfService || untrustedHost) {
    assertPublicHost(host);
    // The proxy bridges a raw bidirectional TCP socket to host:port — for an
    // untrusted owner that's a potential outbound TCP RELAY from our IP. The
    // public-host gate stops SSRF into our LAN; this denylist stops abusing
    // the relay to reach sensitive PUBLIC services (mail/SSH/DB/cache/etc) on
    // third-party hosts using our address. A UO server never listens on these.
    if (SELF_SERVICE_BLOCKED_PORTS.has(port)) {
      throw new Error(`${file}: port ${port} is not allowed for an owner-managed shard (reserved/sensitive service port)`);
    }
    // Same denylist on the external gamefiles URL port (audit 2026-06-23): our
    // /manifest + /hashes endpoints fetch that URL server-side, so an owner must
    // not point it at a sensitive PUBLIC service port either. 443 (the https
    // default, explicit or implicit) is always fine — the denylist contains 443
    // for the TCP game bridge, but here it is the legitimate port, so exempt it.
    if (externalGamefilesUrl) {
      const egfPortStr = new URL(externalGamefilesUrl).port;
      const egfPort = egfPortStr ? Number(egfPortStr) : 443;
      if (egfPort !== 443 && SELF_SERVICE_BLOCKED_PORTS.has(egfPort)) {
        throw new Error(`${file}: externalGamefilesUrl port ${egfPort} is not allowed for an owner-managed shard (reserved/sensitive service port)`);
      }
    }
  }

  return {
    id, slug, displayName, description, host, port, gamefilesPath,
    encrypt, clientVersion, gamefilesUrlBase, guestsAllowed,
    ...(webIdentity !== undefined && { webIdentity }),
    ...(emulator    !== undefined && { emulator }),
    ...(region      !== undefined && { region }),
    ...(era         !== undefined && { era }),
    ...(logo        !== undefined && { logo }),
    ...(website     !== undefined && { website }),
    ...(discord     !== undefined && { discord }),
    ...(createdAt   !== undefined && { createdAt }),
    ...(forceClient !== undefined && { forceClient }),
    ...(externalGamefilesUrl !== undefined && { externalGamefilesUrl }),
    ...(selfService && { selfService: true }),
    ...(sponsored && { sponsored: true }),
    ...(pending && { pending: true }),
    ...(hidden && { hidden: true }),
    ...(autologin && { autologin: true }),
    ...(untrustedHost && { untrustedHost: true }),
  };
}

// ── Registry state ────────────────────────────────────────────────────────────

const registry = new Map<string, ServerRecord>();
let currentServersDir = SERVERS_DIR;
let isLoaded = false;

// ── Load helpers ──────────────────────────────────────────────────────────────

function loadDir(dir: string, map: Map<string, ServerRecord>, precedence: 'low' | 'high'): void {
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (e: unknown) {
    const err = e as NodeJS.ErrnoException;
    if (err.code === 'ENOENT' || err.code === 'EACCES') return;
    throw e;
  }

  const yamlFiles = entries
    .filter((e) => e.isFile() && e.name.endsWith('.yaml') && !e.name.startsWith('.'))
    .map((e) => path.join(dir, e.name));

  for (const file of yamlFiles) {
    // v0.3.22 audit fix: per-file try/catch. Pre-fix one malformed
    // YAML aborted the entire registry load and the proxy fell back
    // to the legacy single-shard env vars — taking down EVERY
    // operator-defined shard because a typo in any one file. The doc
    // claimed loadRegistry was resilient; reality was the opposite.
    // Each file's load is now isolated; a bad file logs + skips,
    // valid files still register.
    try {
      const raw = fs.readFileSync(file, 'utf8');
      let parsed: unknown;
      try {
        parsed = parseYaml(raw);
      } catch (e) {
        throw new Error(`${file}: YAML parse failed: ${(e as Error).message}`);
      }
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        throw new Error(`${file}: must be a YAML mapping at the top level`);
      }
      const record = validate(parsed as Partial<ServerRecord>, file);
      if (precedence === 'high' || !map.has(record.slug)) {
        map.set(record.slug, record);
      }
    } catch (e) {
      console.error(`[ServerRegistry] SKIP ${file}: ${(e as Error).message}`);
    }
  }
}

/**
 * Load all YAML files from the operator dir + the admin dir. Throws if any
 * file is malformed or if a duplicate slug exists within a single directory.
 */
export function loadRegistry(dir: string): void {
  currentServersDir = dir;
  const combined = new Map<string, ServerRecord>();

  // Step 1: operator files (low precedence)
  loadDir(dir, combined, 'low');

  // Step 2: admin-written files (high precedence — overwrite operator for same slug)
  loadDir(DATA_SERVERS_DIR, combined, 'high');

  // Step 3: drop any loaded shard that collides with a deletion-queue entry.
  // The deletion queue holds soft-deleted shards still in their 7-day grace
  // window (and their `server-<id>/` gamefiles are still on disk).
  //   - id collision (audit H-1): a live shard reusing the `id` of a soft-
  //     deleted shard would let force-delete `rm -rf` the WRONG server-<id>/
  //     directory. Treat IDs held by deletion-queue entries as occupied.
  //   - slug collision (audit L-3): a shard pending deletion must not be
  //     resurrected as active by the file-watcher reloading its still-present
  //     operator YAML. Skip it so the soft-delete sticks.
  const deletionQueue = getDeletionQueue();
  const deletionIds = new Set<number>(deletionQueue.map((e) => e.id));
  const deletionSlugs = new Set<string>(deletionQueue.map((e) => e.slug));
  for (const [slug, rec] of Array.from(combined)) {
    if (deletionIds.has(rec.id)) {
      console.error(
        `[ServerRegistry] SKIP ${slug}: id ${rec.id} collides with a soft-deleted ` +
        `shard in the deletion queue — refusing to load (force-delete would wipe ` +
        `the wrong server-${rec.id}/ directory)`,
      );
      combined.delete(slug);
      continue;
    }
    if (deletionSlugs.has(slug)) {
      console.error(
        `[ServerRegistry] SKIP ${slug}: slug matches a soft-deleted shard in the ` +
        `deletion queue — refusing to resurrect a shard pending deletion`,
      );
      combined.delete(slug);
    }
  }

  // Validate no duplicate IDs across merged set
  const idsSeen = new Map<number, string>();
  for (const [slug, rec] of combined) {
    const existing = idsSeen.get(rec.id);
    if (existing) {
      throw new Error(
        `Duplicate server ID ${rec.id} on slugs '${existing}' and '${slug}'. ` +
        `Each server must have a unique id.`,
      );
    }
    idsSeen.set(rec.id, slug);
  }

  // v0.8.81: seed the immutable id-registry so assignId() never re-issues an id
  // an existing shard already uses. Operator shards declare their id in YAML
  // (never via assignId), so without this the counter starts BELOW them and a
  // self-service create collides ("Duplicate server ID"). Account for active
  // shards AND soft-deleted ones (their server-<id>/ may linger). seed() only
  // ever RAISES nextId, never lowers it — safe to call on every load.
  let maxId = 0;
  for (const id of idsSeen.keys()) if (id > maxId) maxId = id;
  for (const id of deletionIds) if (id > maxId) maxId = id;
  if (maxId > 0) seedIdRegistry(maxId + 1);

  if (combined.size === 0) {
    console.warn(`[ServerRegistry] no *.yaml files found — falling back to legacy env vars`);
    installLegacyFallback(combined);
  }

  registry.clear();
  for (const [slug, rec] of combined) registry.set(slug, rec);
  isLoaded = true;

  console.log(
    `[ServerRegistry] loaded ${registry.size} server(s):`,
    Array.from(registry.keys()).join(', '),
  );
}

/** Re-run loadRegistry with the same directory (used after admin mutations). */
export function reloadRegistry(): void {
  loadRegistry(currentServersDir);
}

// v0.4.99: file-system watcher so operator edits to *.yaml under SERVERS_DIR
// or DATA_SERVERS_DIR are picked up live, without a proxy container restart.
// Debounced 500 ms so a flurry of writes (text editor "atomic save" =
// write-temp + rename + chmod) coalesces into one reload. Only watches the
// two directories that loadRegistry() reads from; other registry mutations
// (admin API endpoints) still call reloadRegistry() directly and don't
// depend on the watcher firing.
//
// Failure modes: fs.watch is fragile across platforms — on Linux it relies
// on inotify, on macOS it uses FSEvents, on Windows it uses ReadDirectory
// ChangesW. If watch setup fails (dir missing, inotify limit hit, etc.)
// we log a warning and fall back to "no auto-reload" — operator can still
// restart the proxy container to pick up edits. Crashing the proxy because
// the watcher failed to attach would be worse than the no-watch baseline.
let _watchersAttached = false;
export function startRegistryWatcher(): void {
  if (_watchersAttached) return;
  _watchersAttached = true;
  let debounceTimer: NodeJS.Timeout | null = null;
  const onChange = (eventType: string, filename: string | Buffer | null): void => {
    const name = typeof filename === 'string' ? filename : filename?.toString() ?? '';
    if (!name.endsWith('.yaml') || name.startsWith('.')) return;
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      debounceTimer = null;
      console.log(`[ServerRegistry] YAML changed (${eventType} ${name}) — reloading`);
      try {
        reloadRegistry();
      } catch (e) {
        console.warn(`[ServerRegistry] reload after watch event failed: ${(e as Error).message}`);
      }
    }, 500);
  };
  const watchOne = (dir: string, label: string): void => {
    try {
      if (!fs.existsSync(dir)) {
        console.log(`[ServerRegistry] watcher: ${label} dir ${dir} does not exist yet — skipping`);
        return;
      }
      fs.watch(dir, { persistent: false }, onChange);
      console.log(`[ServerRegistry] watcher attached: ${label} ${dir}`);
    } catch (e) {
      console.warn(`[ServerRegistry] failed to attach watcher to ${label} ${dir}: ${(e as Error).message}`);
    }
  };
  watchOne(currentServersDir, 'SERVERS_DIR');
  watchOne(DATA_SERVERS_DIR, 'DATA_SERVERS_DIR');
}

function installLegacyFallback(map: Map<string, ServerRecord>): void {
  if (!UO_SERVER_HOST || !UO_SERVER_PORT) {
    console.warn('[ServerRegistry] no env-var fallback available (UO_HOST/UO_PORT empty) — /api/servers will be empty');
    return;
  }
  map.set('default', {
    id: 0,
    slug: 'default',
    displayName: 'Default shard',
    description: '',
    host: UO_SERVER_HOST,
    port: UO_SERVER_PORT,
    gamefilesPath: ASSETS_PATH,
    encrypt: 'none',
    clientVersion: DEFAULT_CLIENT_VERSION,
    gamefilesUrlBase: DEFAULT_GAMEFILES_URL_BASE,
    guestsAllowed: true,
  });
  console.log('[ServerRegistry] legacy fallback: synthesised default shard from env vars');
}

// ── Public accessors ──────────────────────────────────────────────────────────

/**
 * The ONE projection from a stored shard to what the picker is allowed to see.
 *
 * 🚨 EXPORTED BECAUSE A SECOND COPY OF THIS LIST HAS BEEN WRONG THREE TIMES. `probeAll` used to
 * rebuild the same object field by field, and each time a new ServerRecord field was added it was
 * added here and forgotten there — silently, since a missing optional field is just `undefined` at
 * the client. `forceClient` (v0.7.6) meant a shard's required-client badge never rendered;
 * `hidden` (v0.9.521) meant the owner's own hidden shard showed no badge; and `sponsored`,
 * `selfService` and `externalGamefilesUrl` were missing from /api/servers in production until
 * 2026-08-06, which left the picker's "Hosted" badge unable to be anything but true — latent
 * today, since no live shard sets those flags, and wrong for the first community shard to appear.
 * The fix that stops a fourth is not vigilance, it is that there is only one list.
 */
export function toPublic(s: ServerRecord, status: string, daysUntilDeletion?: number): PublicServer {
  return {
    id: s.id,
    slug: s.slug,
    displayName: s.displayName,
    description: s.description,
    clientVersion: s.clientVersion,
    gamefilesUrlBase: s.gamefilesUrlBase,
    encrypt: s.encrypt,
    guestsAllowed: s.guestsAllowed,
    ...(s.emulator    !== undefined && { emulator:    s.emulator }),
    ...(s.region      !== undefined && { region:      s.region }),
    ...(s.era         !== undefined && { era:         s.era }),
    ...(s.logo        !== undefined && { logo:        s.logo }),
    ...(s.website     !== undefined && { website:     s.website }),
    ...(s.discord     !== undefined && { discord:     s.discord }),
    ...(s.createdAt   !== undefined && { createdAt:   s.createdAt }),
    ...(s.forceClient !== undefined && { forceClient: s.forceClient }),
    ...(s.externalGamefilesUrl !== undefined && { externalGamefilesUrl: s.externalGamefilesUrl }),
    ...(s.selfService && { selfService: true }),
    ...(s.sponsored && { sponsored: true }),
    ...(s.pending && { pending: true }),
    // Only ever reaches a caller who can already see the record, and /api/servers
    // only keeps a hidden shard in the list for its OWNER -- so this tells the owner
    // "yours, hidden on purpose" instead of leaving them to wonder where it went.
    ...(s.hidden && { hidden: true }),
    status,
    ...(daysUntilDeletion !== undefined && { daysUntilDeletion }),
  };
}

/** Returns active servers + pending-deletion servers — all visible in the picker. */
export function listServers(): PublicServer[] {
  if (!isLoaded) throw new Error('ServerRegistry.listServers called before loadRegistry');
  const active = Array.from(registry.values()).map((s) => toPublic(s, 'active'));
  const deletionQueue = getDeletionQueue();
  const pending = deletionQueue.map((e) => toPublic(e.record, 'pending_deletion', daysRemaining(e)));
  return [...active, ...pending];
}

/** Internal list — includes host/port, never expose over HTTP. */
export function listServersInternal(): ServerRecord[] {
  if (!isLoaded) throw new Error('ServerRegistry.listServersInternal called before loadRegistry');
  return Array.from(registry.values());
}

/** Returns the full record for a slug, or null. */
export function getServer(slug: string): ServerRecord | null {
  if (!isLoaded) throw new Error('ServerRegistry.getServer called before loadRegistry');
  return registry.get(slug) ?? null;
}

/** Returns the full record by numeric ID, or null. */
export function getServerById(id: number): ServerRecord | null {
  if (!isLoaded) throw new Error('ServerRegistry.getServerById called before loadRegistry');
  for (const rec of registry.values()) {
    if (rec.id === id) return rec;
  }
  return null;
}

/** Single-shard auto-select shortcut. */
export function defaultSlug(): string | null {
  if (!isLoaded) throw new Error('ServerRegistry.defaultSlug called before loadRegistry');
  return registry.size === 1 ? Array.from(registry.keys())[0] : null;
}

export function registrySize(): number { return registry.size; }

// ── Admin write operations ────────────────────────────────────────────────────

/** Serialise a ServerRecord to YAML (no comments, clean machine-readable format). */
function toYamlString(rec: ServerRecord): string {
  const obj: Record<string, unknown> = {
    id: rec.id,
    slug: rec.slug,
    displayName: rec.displayName,
    description: rec.description,
    host: rec.host,
    port: rec.port,
    gamefilesPath: rec.gamefilesPath,
    encrypt: rec.encrypt,
    clientVersion: rec.clientVersion,
    gamefilesUrlBase: rec.gamefilesUrlBase,
  };
  if (rec.emulator    !== undefined) obj.emulator    = rec.emulator;
  if (rec.region      !== undefined) obj.region      = rec.region;
  if (rec.era         !== undefined) obj.era         = rec.era;
  if (rec.logo        !== undefined) obj.logo        = rec.logo;
  if (rec.website     !== undefined) obj.website     = rec.website;
  if (rec.discord     !== undefined) obj.discord     = rec.discord;
  if (rec.createdAt   !== undefined) obj.createdAt   = rec.createdAt;
  if (rec.forceClient !== undefined) obj.forceClient = rec.forceClient;
  // v0.3.14: only emit guestsAllowed when the operator set it explicitly
  // (i.e. false). Default-true stays implicit so existing YAMLs round-trip
  // through the admin write path unchanged.
  if (rec.guestsAllowed === false) obj.guestsAllowed = false;
  // v0.3.15: preserve webIdentity exactly as the operator wrote it.
  if (rec.webIdentity !== undefined) obj.webIdentity = rec.webIdentity;
  // v0.8.43: self-service / external gamefiles / sponsored.
  if (rec.externalGamefilesUrl !== undefined) obj.externalGamefilesUrl = rec.externalGamefilesUrl;
  if (rec.selfService) obj.selfService = true;
  if (rec.sponsored)   obj.sponsored   = true;
  if (rec.pending)     obj.pending     = true;
  // v0.9.131 SECURITY: untrustedHost gates the connect-time DNS-rebind resolve-and-pin
  // (UOProxy). validate() reads it back, and PUT sets it true whenever a non-general
  // owner edits host/port — but if it isn't SERIALISED, the reloadRegistry() right after
  // the write re-parses a YAML without it → the flag is lost in the same request → the
  // SSRF pin silently disables for owner-edited non-selfService shards. MUST round-trip.
  if (rec.untrustedHost) obj.untrustedHost = true;
  // autologin (mini/AoS) must likewise survive an admin PUT+reload.
  if (rec.autologin) obj.autologin = true;
  // v0.9.518: and so must `hidden`. Shipped in v0.9.516 without this line, which made
  // the admin checkbox look broken: the flag reached the record, the PUT rewrote the
  // YAML WITHOUT it, and the reloadRegistry() on the next line read that YAML straight
  // back — so the shard was un-hidden again before the response was even sent. Exactly
  // the failure the untrustedHost note above warns about, three lines up.
  if (rec.hidden) obj.hidden = true;
  return yamlStringify(obj);
}

/**
 * Write a server YAML to DATA_SERVERS_DIR. Creates the directory if needed.
 * Called by admin POST (create) and admin PUT (update) endpoints.
 */
export function writeServerYaml(rec: ServerRecord): void {
  fs.mkdirSync(DATA_SERVERS_DIR, { recursive: true });
  const file = path.join(DATA_SERVERS_DIR, `${rec.slug}.yaml`);
  // v0.3.21 audit fix: atomic write via tmp + rename. Pre-fix used a
  // direct fs.writeFileSync — a crash mid-write produced a half-written
  // YAML that broke loadRegistry on next boot, taking the proxy down
  // fail-closed (no shard list = no admin UI = manual recovery via
  // file system). tmp + rename is atomic on POSIX (rename(2) is atomic
  // when source and dest are same FS) and on Windows (MoveFileEx with
  // MOVEFILE_REPLACE_EXISTING) — so each individual YAML write is
  // crash-safe in isolation.
  //
  // audit M-3: the per-file write being atomic does NOT make the
  // two-file slug-rename in admin PUT atomic. That path now does
  // writeServerYaml(new) BEFORE removeServerYaml(old): a crash between
  // the two legs leaves a recoverable transient duplicate (both YAMLs
  // present) rather than a vanished record (neither present).
  const tmp = file + `.tmp.${process.pid}.${Date.now()}`;
  fs.writeFileSync(tmp, toYamlString(rec), 'utf8');
  fs.renameSync(tmp, file);
}

/**
 * Remove a server YAML from DATA_SERVERS_DIR (and SERVERS_DIR if it exists
 * there too — operator files are not deleted; the admin must edit them manually
 * if they re-added the server via operator YAML). Called by admin DELETE.
 */
export function removeServerYaml(slug: string): void {
  for (const dir of [DATA_SERVERS_DIR]) {
    const file = path.join(dir, `${slug}.yaml`);
    try { fs.unlinkSync(file); } catch { /* file may not exist */ }
  }
}

// v0.3.23 audit cleanup: removed `validatePartial` — it was a no-op
// cast (returned `body as Partial<ServerRecord>` without validating)
// and had zero callers. Admin POST/PUT both use `validateFull` (re-
// exported below), which actually validates. Keeping the dead export
// confused the dev-doc audit.

export { validate as validateFull };
