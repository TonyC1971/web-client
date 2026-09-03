/**
 * "This player is now in the world" — written by the GAME process, acted on by the WEB one.
 *
 * 🚨 THE POINT OF THIS FILE IS WHICH PROCESS DOES THE WORK.
 *
 * Handing a player their queued loot and their bought items needs two things that live in
 * different places. The trigger is a login, which ONLY the WebSocket handler sees. The work
 * is a series of calls to the minigames shard, which belongs to the web proxy and nowhere
 * else — operator, 2026-07-31: "todo lo que sea para el servidor de minijuegos como items,
 * cartas y cosas así y llamadas a la api del server de minigames tiene que ser en el proxy
 * web", and the reason: "así si se satura la otra parte web, no afecta al resto de jugadores
 * que solo quieren jugar realmente".
 *
 * Before this, the game process did BOTH: the login handler fired the loot drain and the
 * item drain itself. Those are ~2.5 s bridge round-trips, retries included, running on the
 * single event loop that relays live gameplay for every connected player. It worked, and it
 * put the website's data fetching on the critical path of people who only wanted to play.
 *
 * Now the game process writes one row and stops.
 *
 * WHY A TABLE AND NOT AN HTTP CALL between the processes: they already share this database.
 * A shared table adds no credential to distribute, no internal endpoint, and no new route
 * that has to be kept unreachable from outside — and the nginx map ends in a catch-all that
 * sends `/api/` to the web process, so an internal endpoint would have been externally
 * reachable unless someone remembered to deny it. The cheapest attack surface is the one
 * never opened.
 */
import { db } from './db.js';
import { OWNS_WEB_JOBS } from './proxyRole.js';
// 🚨 NO DIRECT IMPORTS OF WHAT GETS DELIVERED. This module's job is "a player just logged in, drain
// whatever is waiting for them" — the WHAT is uonexus's, the WHEN is generic. Importing the
// deliveries put itemDrops, characterSync and itemMarket in the graph, and itemMarket reaches
// market, cardsdb, cardPricing, cosmetics, points, achievements and minigameRegistry. UOProxy
// imports this file, so the entire economy arrived in any build that could relay a connection.
//
// Each delivery registers itself instead (see the bottom of itemMarket.ts / itemDrops.ts). A
// minimal install registers none and this loop is simply empty — there is nothing waiting for a
// player when nothing can be bought, won or queued.
type LoginDelivery = (sub: string) => Promise<void> | void;
const _deliveries: Array<{ name: string; run: LoginDelivery }> = [];
/** Register work to run once, after a player's login signal is drained. */
export function registerLoginDelivery(name: string, run: LoginDelivery): void {
  _deliveries.push({ name, run });
  console.log(`[registry] login delivery registered: ${name} (${_deliveries.length} total)`);
}

/**
 * How long after the login before the shard is asked to hand anything over.
 *
 * At the instant the login packet is rewritten the character is NOT in the world yet, and
 * the shard answers 409. Being early is harmless — a 409 leaves the row pending and the next
 * login tries again — so this is a delay, not a handshake. It lives on the WATCHER side on
 * purpose: tuning it never requires the game process to change.
 */
const SETTLE_MS = 8_000;

/** How often the watcher looks. Cheap: an indexed read of a table that is empty almost always. */
const TICK_MS = 2_000;

const IS_TEST = process.execArgv.includes('--test') || process.argv.includes('--test')
  || Boolean(process.env.NODE_TEST_CONTEXT);

const qPut = db.prepare('INSERT OR REPLACE INTO login_signals (sub, at) VALUES (?, ?)');
const qDue = db.prepare('SELECT sub, at FROM login_signals WHERE at <= ? ORDER BY at ASC LIMIT 1');
const qDel = db.prepare('DELETE FROM login_signals WHERE sub = ?');

/**
 * Called by the game process the moment a mini login is rewritten. Synchronous and local:
 * one SQLite upsert, microseconds, no network. INSERT OR REPLACE because a player who
 * reconnects twice in a row wants ONE delivery attempt, not a queue of them.
 */
export function signalLogin(sub: string, now = Date.now()): void {
  if (!sub) return;
  try {
    qPut.run(sub, now);
  } catch (e) {
    // Never let bookkeeping break a login. A lost signal costs a delayed delivery, and the
    // rows it would have drained stay pending for the next one.
    console.warn(`[loginSignals] could not record login: ${(e as Error)?.message ?? e}`);
  }
}

/** Drain ONE due player. Exported so a test can drive it without a timer. */
export async function drainOnce(now = Date.now()): Promise<string | null> {
  let row: { sub: string; at: number } | undefined;
  try {
    row = qDue.get(now - SETTLE_MS) as { sub: string; at: number } | undefined;
  } catch (e) {
    console.warn(`[loginSignals] could not read signals: ${(e as Error)?.message ?? e}`);
    return null;
  }
  if (!row) return null;

  // 🚨 DELETE FIRST. A delivery that throws must not leave its signal behind to be retried
  // every tick forever — that would be a loop of bridge writes against the 4-per-second the
  // whole shard gets, which is precisely the denial-of-service shape the stalls audit closed.
  // Nothing is lost by dropping the signal: the loot and item rows stay pending on their own
  // tables and the player's NEXT login re-signals.
  try { qDel.run(row.sub); } catch { /* a duplicate drain is harmless; both are idempotent */ }

  // Each registered delivery runs in ISOLATION, and that is the same reasoning the two hard-coded
  // ones carried: a drop is a reward we generated and could regenerate, while a purchased item is
  // one somebody already paid for, so they have different rules about when a row may be marked
  // done. Merging them into one try would let a failure in the cheap one abandon the expensive one.
  // Failures are logged and skipped, never fatal: nothing is lost, the rows stay pending and the
  // player's NEXT login re-signals.
  for (const d of _deliveries) {
    try {
      await d.run(row.sub);
    } catch (e) {
      console.warn(`[loginSignals] ${d.name} delivery skipped: ${(e as Error)?.message ?? e}`);
    }
  }
  return row.sub;
}

/**
 * ONE player per tick, oldest first. Not a batch: every hand-over is a bridge write, the
 * shard grants four a second for everybody, and a login rush is exactly when that budget is
 * most contended. Pacing here means a crowd logging in drains steadily instead of spiking.
 */
export function startLoginSignalWatcher(): void {
  if (IS_TEST || !OWNS_WEB_JOBS) return;
  let busy = false;
  setInterval(() => {
    if (busy) return;          // a slow shard must not stack overlapping drains
    busy = true;
    void drainOnce().finally(() => { busy = false; });
  }, TICK_MS).unref?.();
}
