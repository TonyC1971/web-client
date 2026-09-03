// v0.3.14: persistent admin ban-list with two key types.
//
//   - `discordId` — durable handle for authed bad actors. Survives cookie
//     clears, IP changes, and VPNs. Operator picks this when the abuse is
//     by a logged-in account.
//   - `ipCidr` — operator picks this for guest abuse where there's no
//     Discord ID to lock onto. Real client IP, not the docker bridge IP,
//     so it's precise even though the shards can't see real IPs (the
//     shard side still observes 172.22.0.1; the proxy side sees the
//     CF-Connecting-IP / XFF tail).
//
// Storage: DATA_PATH + '/admin-bans.json'
// Format :
//   {
//     "bans": [
//       {
//         "id": "ban_<random>",
//         "discordId":  "12345..."   // OR
//         "ipCidr":     "1.2.3.0/24",
//         "reason":     "spam",
//         "createdAt":  1714838400000,
//         "expiresAt":  1715443200000  // null = permanent
//       },
//       ...
//     ]
//   }

import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';
import { DATA_PATH, parseCidr, type ParsedCidr } from './config.js';
import { isSnowflake } from './discordId.js';

const FILENAME = 'admin-bans.json';

export interface BanEntry {
  id: string;
  discordId?: string;
  ipCidr?: string;
  reason: string;
  createdAt: number;
  expiresAt: number | null;
}

interface BanFile {
  bans: BanEntry[];
}

function filePath(): string {
  return path.join(DATA_PATH, FILENAME);
}

function load(): BanFile {
  let raw: string;
  try {
    raw = fs.readFileSync(filePath(), 'utf8');
  } catch (e: unknown) {
    const err = e as NodeJS.ErrnoException;
    // A genuinely-absent file is first-run — start with no bans.
    if (err.code === 'ENOENT') return { bans: [] };
    throw e;
  }
  // audit L-4: FAIL-CLOSED on a present-but-corrupt file. Pre-fix this
  // silently reset to an empty list, which lifts every active ban
  // without any signal — abusers walk back in. Refuse to start instead.
  let obj: unknown;
  try {
    obj = JSON.parse(raw) as unknown;
  } catch (e) {
    throw new Error(
      `[BanRegistry] ${filePath()} is present but contains invalid JSON ` +
      `(${(e as Error).message}). Refusing to reset — that would silently ` +
      `lift every active ban. Fix or remove the file by hand.`,
    );
  }
  if (
    obj !== null &&
    typeof obj === 'object' &&
    Array.isArray((obj as Record<string, unknown>).bans)
  ) {
    const bans = (obj as { bans: unknown[] }).bans
      .filter((b): b is BanEntry => isValidEntry(b));
    return { bans };
  }
  throw new Error(
    `[BanRegistry] ${filePath()} is present but malformed (expected ` +
    `{ bans: [...] }). Refusing to reset — that would silently lift every ` +
    `active ban. Fix or remove the file by hand.`,
  );
}

function isValidEntry(b: unknown): b is BanEntry {
  if (b === null || typeof b !== 'object') return false;
  const e = b as Record<string, unknown>;
  if (typeof e.id !== 'string' || !e.id) return false;
  if (typeof e.reason !== 'string') return false;
  if (typeof e.createdAt !== 'number') return false;
  if (e.expiresAt !== null && typeof e.expiresAt !== 'number') return false;
  const hasDid = isSnowflake(e.discordId);
  const hasIp  = typeof e.ipCidr === 'string' && parseCidr(e.ipCidr as string) !== null;
  return hasDid || hasIp;
}

// Atomic write via tmp + rename — same pattern as idRegistry.ts (audit
// R2-M-2). A torn ban file on a crash mid-write would either look empty
// (active bans silently lifted) or fail to parse (boot warning + reset),
// both bad. tmp+rename gives all-or-nothing.
function persist(data: BanFile): void {
  const finalPath = filePath();
  fs.mkdirSync(path.dirname(finalPath), { recursive: true });
  const tmpPath = `${finalPath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tmpPath, JSON.stringify(data, null, 2) + '\n', 'utf8');
  fs.renameSync(tmpPath, finalPath);
  // Our own write is not a foreign change: record the new stamp so getCache() does not re-read
  // the file we just produced from the object we already hold.
  loadedStamp = fileStamp();
}

let cache: BanFile | null = null;
/** Decoded `ipCidr` strings for fast lookup. Indexed by ban.id. Rebuilt on
 *  every cache invalidation so we never re-parse a CIDR on the hot path. */
let cidrCache: Map<string, ParsedCidr> = new Map();
/** What the file looked like when `cache` was filled. Null = never read / absent. */
let loadedStamp: string | null = null;

/** mtime+size of the ban file, or null when it does not exist. */
function fileStamp(): string | null {
  try {
    const st = fs.statSync(filePath());
    return `${st.mtimeMs}:${st.size}`;
  } catch { return null; }
}

/**
 * 🚨 THE FILE IS SHARED BY TWO PROCESSES, AND THE CACHE WAS FROZEN AT BOOT.
 *
 * Since the split, `/api/` — including `POST /api/admin/bans` — is owned by the WEB process, while
 * the WebSocket upgrade that ENFORCES a ban runs on the GAME one. Each keeps its own in-memory
 * copy of admin-bans.json, and the only thing that ever cleared it was a test seam. So adding a
 * ban wrote the file, updated the web process's copy, and left the process that actually refuses
 * connections reading a snapshot from its last boot.
 *
 * The effect: an admin bans somebody, the panel lists it, the audit log records it — and it does
 * nothing at all until the game proxy is restarted. Removing a ban had the mirror image, which is
 * the half that punishes an innocent: the unbanned player stayed locked out.
 *
 * Re-reading when the file MOVES fixes it without inventing a channel — both processes already
 * mount the same volume, so the file IS the shared medium. A stat is a few microseconds and this
 * runs once per WebSocket upgrade, not per packet.
 */
function getCache(): BanFile {
  const stamp = fileStamp();
  if (cache !== null && stamp === loadedStamp) return cache;
  cache = load();
  loadedStamp = stamp;
  rebuildCidrCache();
  return cache;
}
function rebuildCidrCache(): void {
  cidrCache = new Map();
  if (!cache) return;
  for (const ban of cache.bans) {
    if (ban.ipCidr) {
      const parsed = parseCidr(ban.ipCidr);
      if (parsed) cidrCache.set(ban.id, parsed);
    }
  }
}
function invalidate(): void {
  cache = null;
  loadedStamp = null;
  cidrCache = new Map();
}

function isExpired(ban: BanEntry, now: number): boolean {
  return ban.expiresAt !== null && ban.expiresAt <= now;
}

function pruneExpired(now: number = Date.now()): void {
  const data = getCache();
  const before = data.bans.length;
  data.bans = data.bans.filter((b) => !isExpired(b, now));
  if (data.bans.length !== before) {
    persist(data);
    rebuildCidrCache();
  }
}

/** Returns the matching active ban for the given handles, or null. */
export function findActiveBan(opts: {
  discordId?: string | null;
  ip?: string | null;
}): BanEntry | null {
  const now = Date.now();
  const data = getCache();
  for (const ban of data.bans) {
    if (isExpired(ban, now)) continue;
    if (opts.discordId && ban.discordId === opts.discordId) return ban;
    if (opts.ip && ban.ipCidr) {
      const cidr = cidrCache.get(ban.id);
      if (!cidr) continue;
      const lc = opts.ip.toLowerCase();
      const stripped = lc.startsWith('::ffff:') ? lc.slice('::ffff:'.length) : lc;
      const addr = parseCidr(stripped);
      if (!addr || addr.family !== cidr.family) continue;
      if (cidrContainsBuf(cidr, addr.net)) return ban;
    }
  }
  return null;
}

/**
 * v0.3.22: evaluate a SPECIFIC ban against a given identity tuple.
 * Used by `UOProxy.closeMatching` so a freshly-added ban can match
 * its own active sessions even when ANOTHER ban already covers the
 * same IP/CIDR — `findActiveBan` returns only the first hit, so the
 * second-issued ban's `id !== first.id` made `closeMatching`
 * silently skip every overlapping session.
 */
export function banMatches(ban: BanEntry, opts: {
  discordId?: string | null;
  ip?: string | null;
}): boolean {
  const now = Date.now();
  if (isExpired(ban, now)) return false;
  if (ban.discordId && opts.discordId && ban.discordId === opts.discordId) return true;
  if (ban.ipCidr && opts.ip) {
    const cidr = cidrCache.get(ban.id);
    if (!cidr) return false;
    const lc = opts.ip.toLowerCase();
    const stripped = lc.startsWith('::ffff:') ? lc.slice('::ffff:'.length) : lc;
    const addr = parseCidr(stripped);
    if (!addr || addr.family !== cidr.family) return false;
    if (cidrContainsBuf(cidr, addr.net)) return true;
  }
  return false;
}

function cidrContainsBuf(cidr: ParsedCidr, addr: Buffer): boolean {
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

export function listBans(): BanEntry[] {
  pruneExpired();
  return getCache().bans.slice();
}

export interface AddBanOpts {
  discordId?: string;
  ipCidr?: string;
  reason: string;
  expiresAt?: number | null;
}

export function addBan(opts: AddBanOpts): BanEntry {
  if (!opts.discordId && !opts.ipCidr) {
    throw new Error('Ban requires either discordId or ipCidr');
  }
  if (opts.discordId && opts.ipCidr) {
    throw new Error('Ban accepts discordId OR ipCidr, not both — split into two entries');
  }
  if (opts.discordId && !isSnowflake(opts.discordId)) {
    throw new Error('discordId must be a 15-20 digit numeric snowflake');
  }
  if (opts.ipCidr && parseCidr(opts.ipCidr) === null) {
    throw new Error(`ipCidr "${opts.ipCidr}" is not a valid CIDR`);
  }
  const reason = (opts.reason || '').trim().slice(0, 200);
  if (!reason) throw new Error('reason is required');
  // 🚨 A BAD EXPIRY USED TO BECOME "PERMANENT", SILENTLY. Every other invalid input in this
  // function throws — a malformed snowflake, a malformed CIDR, an empty reason — and this one
  // alone fell back to `null`, which is not a lesser value: it is the harshest one available.
  // The admin asked for a temporary ban and got a forever one, and the API answered 201.
  //
  // Reachable by one plausible slip: the panel computes `Date.now() + days * 86400000`, so a
  // future edit sending the DURATION instead (3600000) lands as a past timestamp and silently
  // escalates. Refusing is the only reading that cannot punish somebody more than intended, and
  // it matches what the route already does with the other throws — a 400 with the reason.
  if (opts.expiresAt !== undefined && opts.expiresAt !== null) {
    if (!Number.isFinite(opts.expiresAt) || opts.expiresAt <= Date.now()) {
      throw new Error(
        'expiresAt must be a timestamp in the future (milliseconds since the epoch). '
        + 'Omit it for a permanent ban — a past or malformed value will not be treated as one.');
    }
  }
  const expiresAt = opts.expiresAt === undefined || opts.expiresAt === null ? null : opts.expiresAt;

  const id = `ban_${crypto.randomBytes(8).toString('hex')}`;
  const entry: BanEntry = {
    id,
    ...(opts.discordId ? { discordId: opts.discordId } : {}),
    ...(opts.ipCidr ? { ipCidr: opts.ipCidr } : {}),
    reason,
    createdAt: Date.now(),
    expiresAt,
  };
  const data = getCache();
  data.bans.push(entry);
  persist(data);
  rebuildCidrCache();
  return entry;
}

export function removeBan(id: string): boolean {
  const data = getCache();
  const before = data.bans.length;
  data.bans = data.bans.filter((b) => b.id !== id);
  if (data.bans.length === before) return false;
  persist(data);
  rebuildCidrCache();
  return true;
}

/** Test-only: drop the in-memory cache so the next read reloads from disk. */
export function _invalidateForTests(): void {
  invalidate();
}
