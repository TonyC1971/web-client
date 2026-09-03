// jwtSecrets.ts — hot JWT-secret rotation with a dual-key grace window
// (operator 2026-06-11: secrets manageable from /admin, no container rebuild).
//
// Model: the SIGNING secret is `jwt-secret-current` from SQLite runtime_config,
// falling back to the .env-derived JWT_SECRET (so existing deploys change
// nothing). "Rotate" captures whatever secret is currently signing into
// `jwt-secret-previous`, generates a fresh 64-hex `jwt-secret-current`, and
// stamps `jwt-rotated-at`. VERIFICATION accepts the current secret always and
// the previous one for GRACE_MS (24h) after rotation — so every session signed
// before the rotation keeps working seamlessly until it naturally re-issues,
// and a leaked old secret still dies within a day.
//
// The secret value never leaves the server: the admin API only exposes
// source + rotation timestamps, and rotation generates the value server-side
// (no operator-typed weak secrets).

import { randomBytes } from 'crypto';
import { db } from './db.js';
import { JWT_SECRET } from './config.js';

const qGet = db.prepare('SELECT value FROM runtime_config WHERE key = ?');
const qSet = db.prepare('INSERT INTO runtime_config(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value');

const KEY_CURRENT  = 'jwt-secret-current';
const KEY_PREVIOUS = 'jwt-secret-previous';
const KEY_ROTATED  = 'jwt-rotated-at';
// v0.9.132 (audit 2026-06-21): global token epoch (Unix SECONDS). A HARD rotate
// stamps this to "now" so verifyJWT rejects EVERY token issued before it — the
// platform-wide kill switch the per-sub loggedOutAt map can't provide. 0 = unset.
const KEY_MIN_IAT  = 'jwt-min-iat';
export const ROTATION_GRACE_MS = 24 * 60 * 60 * 1000;

function rcGet(key: string): string | null {
  const r = qGet.get(key) as { value: string } | undefined;
  return r ? r.value : null;
}

// verifyJWT is HOT (every authenticated request + every WS upgrade), so the
// three SQLite reads getVerifySecrets needs are memoised for 5s. Audit note
// 2026-06-11: without this, moving the secret from a constant into SQLite
// turned a per-request constant lookup into 1-3 DB SELECTs. The 5s staleness
// is irrelevant — a freshly rotated secret's PREVIOUS still verifies for 24h,
// so a JWT minted in the 5s gap is accepted either way. invalidate() drops it
// immediately on rotate so a rotation's effect on SIGNING is never delayed.
let _memo: { at: number; current: string; verify: string[]; minIat: number } | null = null;
function snapshot(): { current: string; verify: string[]; minIat: number } {
  if (_memo && Date.now() - _memo.at < 5000) return _memo;
  const current = rcGet(KEY_CURRENT) || JWT_SECRET;
  const verify = [current];
  const prev = rcGet(KEY_PREVIOUS);
  const rotatedAt = Number(rcGet(KEY_ROTATED)) || 0;
  if (prev && prev !== current && rotatedAt > 0 && Date.now() - rotatedAt < ROTATION_GRACE_MS) verify.push(prev);
  const minIat = Number(rcGet(KEY_MIN_IAT)) || 0;
  _memo = { at: Date.now(), current, verify, minIat };
  return _memo;
}

/** The secret new JWTs are signed with right now. */
export function getSigningSecret(): string {
  return snapshot().current;
}

/** Every secret a presented JWT may verify against: the signing secret plus,
 *  within the 24h grace window after a rotation, the previous one. */
export function getVerifySecrets(): string[] {
  return snapshot().verify;
}

/** Global token-issuance floor (Unix SECONDS). verifyJWT rejects any token whose
 *  `iat` is below this. 0 = unset (no floor). Bumped to "now" by a HARD rotate so
 *  a single action invalidates every outstanding token (incident response). */
export function getMinIssuedAt(): number {
  return snapshot().minIat;
}

/**
 * Rotate the JWT signing secret.
 *  - SOFT (default): previous ← whatever signs today (keeps VERIFYING for the 24h
 *    grace so no live session is cut), current ← fresh random. For routine
 *    zero-downtime rotation.
 *  - HARD (`{hard:true}`): current ← fresh, previous CLEARED (the old/leaked secret
 *    stops verifying immediately), and the global token epoch is stamped to "now"
 *    so EVERY token issued before this instant is rejected — including any an
 *    attacker could forge with a leaked secret, and the admin's own session.
 *    This is the incident-response containment the soft grace can't give: the
 *    `rotate-jwt` button exists precisely for a suspected leak, and a soft rotate
 *    leaves the leaked secret valid for 24h.
 */
export function rotateJwtSecret(opts: { hard?: boolean } = {}): { rotatedAt: number; graceUntil: number; hard: boolean } {
  const now = Date.now();
  if (opts.hard) {
    qSet.run(KEY_PREVIOUS, '');                         // no grace — drop the (possibly leaked) old secret now
    qSet.run(KEY_CURRENT, randomBytes(32).toString('hex'));
    qSet.run(KEY_ROTATED, String(now));
    qSet.run(KEY_MIN_IAT, String(Math.floor(now / 1000)));  // reject every pre-rotation token
    _memo = null;
    return { rotatedAt: now, graceUntil: now, hard: true };
  }
  qSet.run(KEY_PREVIOUS, getSigningSecret());
  qSet.run(KEY_CURRENT, randomBytes(32).toString('hex'));
  qSet.run(KEY_ROTATED, String(now));
  _memo = null; // drop the memo so signing/verify pick up the rotation now
  return { rotatedAt: now, graceUntil: now + ROTATION_GRACE_MS, hard: false };
}

/** Status for the admin panel — never the values themselves. */
export function jwtSecretStatus(): { source: 'panel' | 'env'; rotatedAt: number | null; graceActive: boolean; minIssuedAt: number } {
  const rotatedAt = Number(rcGet(KEY_ROTATED)) || null;
  return {
    source: rcGet(KEY_CURRENT) ? 'panel' : 'env',
    rotatedAt,
    graceActive: !!(rotatedAt && Date.now() - rotatedAt < ROTATION_GRACE_MS && rcGet(KEY_PREVIOUS)),
    minIssuedAt: Number(rcGet(KEY_MIN_IAT)) || 0,
  };
}
