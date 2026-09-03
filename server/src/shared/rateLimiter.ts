// v0.3.14: per-handle token-bucket rate limiter for WS upgrades.
//
// Two key types:
//   - Discord-authed users: keyed on JWT `sub` (= Discord snowflake — stable
//     across cookie clears, IP changes, VPNs).
//   - Guests / unauthenticated: keyed on the real client IP (CF-Connecting-IP
//     → X-Forwarded-For → req.socket.remoteAddress, after `trust proxy`).
//
// Bucket parameters come from config.ts (RATE_LIMIT_BURST / RATE_LIMIT_PER_MIN).
// Defaults: burst 30 attempts, sustained 10/min. Real reconnect noise (page
// refresh, shard switch, tab close+reopen) stays well under; a script
// hammering reconnects hits close-1008 at attempt 31.
//
// Identifier key is `${kind}:${value}` so a Discord user and a guest from
// the same IP can't share a bucket.

import {
  RATE_LIMIT_DEV_MULT,
  getRateLimitBurst,
  getRateLimitPerMin,
  isIpWhitelisted,
} from './config.js';

interface Bucket {
  /** Whole-token capacity (post-multiplier). */
  capacity: number;
  /** Token refill rate (whole tokens / second, post-multiplier). */
  refillPerSec: number;
  /** Current token count (float; refill is fractional). */
  tokens: number;
  /** ms timestamp of last refill calculation. */
  lastRefill: number;
  /** ms timestamp of last `consume()` call — used to evict idle buckets. */
  lastTouch: number;
}

const buckets = new Map<string, Bucket>();

function compute(now: number, b: Bucket): void {
  const elapsedMs = now - b.lastRefill;
  if (elapsedMs <= 0) return;
  const add = (elapsedMs / 1000) * b.refillPerSec;
  b.tokens = Math.min(b.capacity, b.tokens + add);
  b.lastRefill = now;
}

function newBucket(now: number): Bucket {
  // v0.9.137: read the LIVE knobs (admin App-config override else env) so a panel
  // change applies to new buckets at once; DEV_MODE ×10 multiplier still on top.
  const capacity     = getRateLimitBurst()   * RATE_LIMIT_DEV_MULT;
  const refillPerSec = (getRateLimitPerMin() * RATE_LIMIT_DEV_MULT) / 60;
  return {
    capacity,
    refillPerSec,
    tokens: capacity,        // start full so cold sessions aren't penalised
    lastRefill: now,
    lastTouch: now,
  };
}

/**
 * Attempt to consume one upgrade-attempt token for a (kind, identifier)
 * pair. Returns true if accepted, false if the bucket is empty.
 *
 * `ip` is checked against PROXY_RATE_LIMIT_WHITELIST whenever provided —
 * whitelisted IPs always pass. For Discord-authed callers we still pass
 * the underlying IP so a shared CIDR (e.g. an operator's office network)
 * keeps its exemption.
 */
export function allowUpgrade(kind: 'discord' | 'ip', identifier: string, ip: string | null): boolean {
  if (ip && isIpWhitelisted(ip)) return true;
  const now = Date.now();
  const key = `${kind}:${identifier}`;
  let b = buckets.get(key);
  if (!b) {
    b = newBucket(now);
    buckets.set(key, b);
  }
  compute(now, b);
  b.lastTouch = now;
  if (b.tokens >= 1) {
    b.tokens -= 1;
    return true;
  }
  return false;
}

/**
 * Periodic eviction so the map doesn't grow unbounded. A bucket idle for
 * more than 30 min is dropped — when its key reappears we'll recreate
 * with a full bucket, which is what an idle user would have refilled to
 * anyway.
 */
const IDLE_EVICT_MS = 30 * 60 * 1000;
function sweep(): void {
  const now = Date.now();
  for (const [key, b] of buckets) {
    if (now - b.lastTouch > IDLE_EVICT_MS) buckets.delete(key);
  }
}
setInterval(sweep, 5 * 60 * 1000).unref();

/** Test-only: drop all buckets so a fresh allowUpgrade starts a new bucket. */
export function _resetForTests(): void {
  buckets.clear();
}
