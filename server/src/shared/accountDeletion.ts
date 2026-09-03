// accountDeletion.ts — 7-day grace period for player-initiated account deletion
// (operator 2026-06-18). Instead of erasing immediately, "delete my account" schedules
// the erasure 7 days out; the player can cancel any time during the grace by logging
// back in. A reaper (wired in AssetServer) performs the real, irreversible erasure once
// the countdown expires. Durable in SQLite so the countdown survives restarts.
//
// Mirrors the server soft-delete pattern (deletionQueue.ts) but for accounts. The real
// erasure is auth.ts adminEraseUser (identity, metrics, achievements, profile blobs,
// admin grants, sessions) — this module only owns the schedule.

import { db } from './db.js';

export const ACCOUNT_GRACE_MS = 7 * 24 * 60 * 60 * 1000; // 7 days

db.exec(`CREATE TABLE IF NOT EXISTS account_deletions (
  sub          TEXT PRIMARY KEY,
  requested_at INTEGER NOT NULL,
  scheduled_at INTEGER NOT NULL
)`);

// INSERT OR IGNORE: re-clicking "delete" while already pending keeps the original
// countdown rather than extending it. A cancel removes the row, so a later re-delete
// starts a fresh 7 days.
const qSchedule = db.prepare('INSERT OR IGNORE INTO account_deletions(sub, requested_at, scheduled_at) VALUES(?, ?, ?)');
const qGet      = db.prepare('SELECT requested_at, scheduled_at FROM account_deletions WHERE sub = ?');
const qCancel   = db.prepare('DELETE FROM account_deletions WHERE sub = ?');
const qExpired  = db.prepare('SELECT sub FROM account_deletions WHERE scheduled_at <= ?');

export interface PendingDeletion { requestedAt: number; scheduledAt: number; }

/** Schedule (or keep) a pending deletion for `sub`. Returns the scheduled erasure time. */
export function scheduleAccountDeletion(sub: string, now = Date.now()): PendingDeletion {
  if (!sub || sub.startsWith('guest-')) return { requestedAt: now, scheduledAt: now };
  qSchedule.run(sub, now, now + ACCOUNT_GRACE_MS);
  return getPendingDeletion(sub) || { requestedAt: now, scheduledAt: now + ACCOUNT_GRACE_MS };
}

/** The pending deletion for `sub`, or null if none scheduled. */
export function getPendingDeletion(sub: string): PendingDeletion | null {
  if (!sub) return null;
  const r = qGet.get(sub) as { requested_at: number; scheduled_at: number } | undefined;
  return r ? { requestedAt: Number(r.requested_at), scheduledAt: Number(r.scheduled_at) } : null;
}

/** Cancel a pending deletion. Returns true if one was actually removed. */
export function cancelAccountDeletion(sub: string): boolean {
  return !!sub && Number(qCancel.run(sub).changes) > 0;
}

/** Subs whose grace period has elapsed (scheduled_at ≤ now) — ready for real erasure. */
export function getExpiredDeletions(now = Date.now()): string[] {
  return (qExpired.all(now) as Array<{ sub: string }>).map((r) => r.sub);
}

/** Reaper: erase every account whose countdown has expired, then drop its schedule row.
 *  `erase` is auth.ts adminEraseUser. Returns how many accounts were erased. */
export async function reapExpiredDeletions(
  erase: (sub: string) => void | Promise<void>,
  now = Date.now(),
): Promise<number> {
  let n = 0;
  for (const sub of getExpiredDeletions(now)) {
    // Drop the schedule ONLY after a SUCCESSFUL erase (audit F2): the old code cancelled
    // unconditionally after the catch, so a throwing erase (e.g. a SQLite failure) lost
    // the schedule and was NEVER retried — a silent partial GDPR erasure. Now a failure
    // leaves the row so the next sweep retries it.
    //
    // 🚨 AWAITED SINCE 2026-08-03, and that word is load-bearing. Erasure now reaches the SHARD
    // first (their escrowed items, characters and account), which is a network call — and an
    // un-awaited promise would have made every rejection invisible to this try, dropping the
    // schedule for an erasure that had not happened. The retry guarantee above is only worth
    // anything if a failure can actually be seen from here.
    try { await erase(sub); n++; cancelAccountDeletion(sub); } catch { /* leave the schedule for the next sweep to retry */ }
  }
  return n;
}
