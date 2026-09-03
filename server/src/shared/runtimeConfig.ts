import * as fs from 'fs';
import * as path from 'path';
import { withFileLockSync } from './fileLock.js';

// ── Hot-reloadable runtime configuration ─────────────────────────────────────
// Everything the operator CHANGES over time (admins + their scopes, allowed
// origins, logo hosts, the rate-limit whitelist, the display name) lives in a
// bind-mounted JSON file so editing it does NOT require recreating the proxy
// container — the proxy watches the file and re-reads on change. Adding an admin
// is now: edit the file, save. No `docker compose up --force-recreate`.
//
// What STAYS in .env (NOT here): secrets (JWT_SECRET, DISCORD_CLIENT_SECRET),
// the Discord client id/redirect, and container/infra-bound values (ports,
// mount paths, NODE_ENV, DEV_MODE, TRUST_PROXY_HOPS). Those are deploy-bound and
// don't change at runtime.
//
// File path: APP_CONFIG_PATH env, default <DATA_PATH>/app-config.json. DATA_PATH
// is the proxy-data bind-mount (read-write, survives recreation), so the file is
// editable on the host (e.g. via SMB on the NAS) without touching the image.
//
// Fallback contract: an absent OR malformed file is non-fatal — each getter in
// config.ts falls back to its old env var, so existing .env-only deploys keep
// working unchanged. A malformed file keeps the last-good parse + warns.

/** Per-admin value: a scope array (general admin) OR a granular record with a
 *  `servers` list (server owner — may edit only those slugs). */
export type AdminValue = string[] | { scopes?: string[]; servers?: string[] };

export interface RuntimeConfigFile {
  /** discordId → AdminValue. ["*"]/[] = full general access; {scopes,servers} = owner. */
  admins?: Record<string, AdminValue>;
  /** CSRF/CORS allow-list (overrides PUBLIC_ORIGINS env when non-empty). */
  publicOrigins?: string[];
  /** extra hosts allowed for shard logo <img src> (overrides LOGO_ALLOWED_HOSTS). */
  logoAllowedHosts?: string[];
  /** CIDRs exempt from the WS rate limit + ban list (overrides PROXY_RATE_LIMIT_WHITELIST). */
  rateLimitWhitelist?: string[];
  /** login-screen display name (overrides SERVER_NAME). */
  serverName?: string;
  /** Per-IP WS-upgrade rate-limit BURST capacity (overrides PROXY_RATE_LIMIT_BURST env).
   *  Positive int; absent/invalid falls back to the env value. Applies to NEW buckets. */
  rateLimitBurst?: number;
  /** Per-IP WS-upgrade sustained refill in attempts/min (overrides PROXY_RATE_LIMIT_PER_MIN). */
  rateLimitPerMin?: number;
  /** MINI asset profiles (weight presets) — edited via PUT /api/admin/asset-profiles,
   *  served at GET /api/asset-profiles. Overrides the built-in DEFAULT_ASSET_PROFILES. */
  assetProfiles?: Record<string, { skip: string[]; defer: string[] }>;
  /** Active profile when the client URL gives no ?ultra/?profile (e.g. "ultra"
   *  so the mini boots light by default). */
  assetProfileDefault?: string;
  /** Per-mode rail opt-in for the mini ({strip?,mobile?,window?,embed?}: bool) —
   *  served as `_railModes` so the client shows the rail only in those modes. */
  assetRailModes?: Record<string, boolean>;
  /** MINI use-cases (modes of use) — edited via PUT /api/admin/usecases, served at
   *  GET /api/usecases. Per-id override of DEFAULT_USECASES (hot, no restart). */
  miniUseCases?: Record<string, unknown>;
  /** Per-shard-slug address of that shard's read-only web bridge (Custom/Web/WebBridge.cs),
   *  which the Backpack mirror queries live. Hot config rather than an env var on purpose:
   *  a new env needs the container RECREATED, while this is re-read on the usual memo and
   *  converges in both proxy processes with no restart.
   *
   *  Only the READ token lives here. The bridge has a SECOND, separate write token for
   *  creating items, and it stays out of this object: leaking the credential that paints a
   *  Backpack panel must never become the ability to fabricate gear. */
  gearBridges?: Record<string, { url: string; readToken: string }>;

  /** Same bridges, WRITE credential — kept in its own object rather than as a field on
   *  gearBridges above, and that separation is the point rather than tidiness.
   *
   *  gearBridges is handed to the read paths (/api/gear, /api/item-info). If the write
   *  token rode along on that object, every one of those handlers would be holding the
   *  credential that can create and rename characters, and one careless echo of a bridge
   *  config would leak it. Two objects means a read path cannot leak what it never had.
   *
   *  Named for what the credential DOES, not for where it points: anything reading this is
   *  about to change a player's character. */
  characterSync?: Record<string, { url: string; writeToken: string }>;
}

const APP_CONFIG_PATH = process.env.APP_CONFIG_PATH
  ?? path.join(process.env.DATA_PATH ?? './data', 'app-config.json');

let _cache: RuntimeConfigFile = {};
let _loadedOnce = false;

function load(): void {
  try {
    // Strip a leading UTF-8 BOM — Windows editors (Notepad, PowerShell
    // Set-Content -Encoding utf8) prepend one, and JSON.parse rejects it. Being
    // BOM-tolerant matters here because the operator hand-edits this file; a BOM
    // would otherwise make the admin list silently fall back to env (lockout).
    let raw = fs.readFileSync(APP_CONFIG_PATH, 'utf8');
    if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);  // strip UTF-8 BOM
    const parsed = JSON.parse(raw) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      _cache = parsed as RuntimeConfigFile;
      console.log(`[runtime-config] loaded ${APP_CONFIG_PATH}`);
    } else {
      console.warn(`[runtime-config] ${APP_CONFIG_PATH} is not a JSON object — ignoring`);
    }
  } catch (e) {
    const err = e as NodeJS.ErrnoException;
    if (err.code === 'ENOENT') {
      if (!_loadedOnce) console.log(`[runtime-config] ${APP_CONFIG_PATH} not present — using .env fallbacks`);
    } else {
      // Keep the last-good cache on a parse error so a fat-fingered save can't
      // wipe the live admin list out from under the running proxy.
      console.warn(`[runtime-config] failed to read/parse ${APP_CONFIG_PATH} (keeping last value): ${err.message}`);
    }
  }
  _loadedOnce = true;
}

load();

// Watch the DIRECTORY, not the file: editors/atomic saves replace the inode via
// rename, which fs.watch on the file path stops following. Debounce because a
// single save often fires multiple events.
try {
  const dir = path.dirname(APP_CONFIG_PATH);
  const base = path.basename(APP_CONFIG_PATH);
  let timer: NodeJS.Timeout | null = null;
  fs.watch(dir, (_evt, fname) => {
    if (fname && fname.toString() !== base) return;
    if (timer) clearTimeout(timer);
    timer = setTimeout(load, 200);
  });
} catch {
  // Some filesystems (certain network mounts) don't support fs.watch. The file
  // still loads once at boot; operator can `docker restart` to pick up changes
  // if watch is unavailable. Non-fatal.
  console.warn('[runtime-config] fs.watch unavailable — changes need a proxy restart on this filesystem');
}

/** The current parsed config file (live — reflects the latest on-disk save). */
export function getRuntimeConfigFile(): RuntimeConfigFile {
  return _cache;
}

// ── Cross-process write lock ────────────────────────────────────────────────
// 🚨 TWO PROCESSES WRITE THIS FILE, AND NOTHING SERIALISED THEM. Since the web/game split
// the admin routes (`/api/` is owned by `web`, and its own rule says "…leaderboards, admin")
// run in one process, while the destructive scheduled jobs — expiry cleanup hourly, owner
// reaper every 6h, both of which call revokeSlugFromAllOwners → updateRuntimeConfig — run in
// the other (`OWNS_JOBS = ROLE !== 'web'`). `withAdminLock` in AssetServer is a method on the
// instance, so it serialises within ONE process and says nothing about the other; two comments
// there claimed it covered the cron, which was true before the split and not after.
//
// The write itself was already atomic (tmp + rename, no torn file). The READ-MODIFY-WRITE was
// not: the mutation was seeded from `_cache`, this process's own copy, refreshed only by an
// fs.watch that is debounced 200ms — and that this file already documents as unavailable on
// some network mounts, where it never refreshes at all. So a job could revoke a slug and an
// admin write moments later could put it back, silently, by rewriting the whole document from
// a stale seed. The loser is a REVOCATION: a deleted shard's slug left granted to its former
// owner, which is the exact state the reaper exists to clear.
//
// Same protocol as withOverrideLock, in synchronous form because every caller here is sync and
// making this async would ripple through ten call sites including express handlers: an O_EXCL
// lockfile carrying an owner token, a stale lock stolen so a crashed holder cannot wedge admin
// writes forever, and a release that only removes a lock still its own. Budget exhausted or a
// directory we cannot write to means proceeding UNLOCKED rather than refusing the write — an
// admin action must not fail because of lock bookkeeping, and the fresh re-read below already
// removes most of the window on its own.
// The protocol itself lives in fileLock.ts — the deletion queue needs the same one, and two
// copies of a lock protocol is how they drift.

/** The file as it is on disk RIGHT NOW, which is what a mutation must be seeded from. Falls back
 *  to the cache on a parse error, so a fat-fingered hand-edit cannot be amplified into a wipe. */
function readFresh(): RuntimeConfigFile {
  try {
    let raw = fs.readFileSync(APP_CONFIG_PATH, 'utf8');
    if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);
    const parsed = JSON.parse(raw) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) return parsed as RuntimeConfigFile;
    return _cache;
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code === 'ENOENT') return {};   // no file yet: start empty
    return _cache;
  }
}

/**
 * Mutate + persist the config file atomically (temp write + rename) and update
 * the in-memory cache immediately. Used by the web admin-management endpoints
 * (admins:manage) so an operator can add/remove admins from the UI without
 * touching files or recreating the container — the on-disk write also triggers
 * the fs.watch reload (idempotent). Throws on write failure so the caller can
 * surface a 500 instead of silently losing the change.
 */
export function updateRuntimeConfig(mutate: (cfg: RuntimeConfigFile) => void): RuntimeConfigFile {
  return withFileLockSync(APP_CONFIG_PATH, () => {
    // Seeded from the FILE, not from `_cache`, and INSIDE the lock. That is the whole fix: the
    // cache is this process's view, refreshed by a debounced watch that some network mounts never
    // fire, so seeding from it rewrites the entire document over whatever the other process just
    // wrote. The lock is best-effort, so the re-read has to be what carries the guarantee.
    const next: RuntimeConfigFile = JSON.parse(JSON.stringify(readFresh()));
    mutate(next);
    const tmp = `${APP_CONFIG_PATH}.tmp-${process.pid}`;
    fs.writeFileSync(tmp, JSON.stringify(next, null, 2) + '\n', 'utf8');
    fs.renameSync(tmp, APP_CONFIG_PATH);   // atomic replace
    _cache = next;                          // immediate consistency
    return next;
  });
}

export { APP_CONFIG_PATH };
