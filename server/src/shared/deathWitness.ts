// deathWitness.ts — hand the observed killer name from the GAME process to the WEB one.
//
// The observation can only happen where the bytes flow (deathWatch.ts, inside the relay) and the
// publishing belongs to the web side, which owns /api/metrics/report and the widget. They are two
// processes sharing one SQLite file, so this is a table rather than an internal HTTP call — exactly
// the reasoning login_signals already carries: no new credential, no internal endpoint, no new
// externally reachable route to get wrong.
//
// 🚨 THE ROW IS CONSUMED, NOT READ. A name left behind would be attached to the NEXT death, which
// is how a player ends up "slain by" something they last fought an hour ago — and worse, how a name
// observed on one shard could surface on another. take() deletes in the same statement it reads.

import { db } from './db.js';

/** Matches what the widget truncates to, so what is stored is what can be shown. */
const NAME_MAX = 40;

const qPut = db.prepare(
  'INSERT INTO death_witness(sub, killer, at) VALUES(?, ?, ?) ' +
  'ON CONFLICT(sub) DO UPDATE SET killer = excluded.killer, at = excluded.at');
const qTake = db.prepare('DELETE FROM death_witness WHERE sub = ? RETURNING killer, at');
const qPrune = db.prepare('DELETE FROM death_witness WHERE at < ?');

/** How long an unclaimed observation stays usable. The client reports every 30 s, so a few minutes
 *  is generous; past that the death was never reported and the name is only a chance to mislabel a
 *  later one. */
const MAX_AGE_MS = 5 * 60 * 1000;

/** Called from the game process the moment the shard's own packets say this player died.
 *  `killer` is null when the stream carried no name for the mobile — a nameless death is published
 *  as such, never backfilled from anything the client said. */
export function recordDeathWitness(sub: string, killer: string | null, now = Date.now()): void {
  if (!sub) return;
  const clean = typeof killer === 'string' ? killer.trim().slice(0, NAME_MAX) : '';
  qPut.run(sub, clean || null, now);
}

/**
 * Called from the web process when a metrics report carries a death. Returns the observed name, or
 * null when there is nothing to publish — which includes the encrypted-shard case, where the relay
 * cannot read the stream at all. Callers must publish a nameless death then, NOT fall back to the
 * client's own string: falling back would restore the whole problem on the one path that matters.
 */
export function takeDeathWitness(sub: string, now = Date.now()): string | null {
  if (!sub) return null;
  // Sweep expired rows first so a stale one cannot be returned by the DELETE below.
  qPrune.run(now - MAX_AGE_MS);
  const row = qTake.get(sub) as { killer: string | null; at: number } | undefined;
  if (!row) return null;
  return row.killer || null;
}

/** Test seam: how many observations are waiting. Not used in production code. */
export function pendingWitnesses(): number {
  return (db.prepare('SELECT COUNT(*) AS c FROM death_witness').get() as { c: number }).c;
}
