// userData.ts — GDPR right-to-erasure: wipe every personal record we hold for a
// Discord user (operator 2026-06-11; extended 2026-06-18 to the full progression
// suite — comments/wall, points, cosmetics, cards, follows, quests, seasons,
// notifications, market — so an admin "delete user" or a self "delete account"
// truly leaves nothing behind).
//
// The caller also deletes the user's cloud profile blobs (auth.deleteProfilesForUser),
// removes any admin/owner grant, and logs them out. The main-DB wipe is one
// transaction (all-or-nothing); the dedicated cards.db is wiped best-effort after.

import { db, tx } from './db.js';
// 🚨 NO DIRECT IMPORT OF THE CARD STORE. GDPR erasure must reach every store holding the user's
// data — but that does not mean THIS module has to know them all. Knowing them means importing
// them, and importing them means any build that can delete an account also carries the economy. A
// minimal self-hosted install stores identity, settings and profiles; there is nothing else to
// erase there. Inverted: each store registers its own eraser (uonexus registers the card one at
// startup). It is also better here — a new store registers itself instead of editing this file,
// which is exactly how a store gets forgotten and quietly survives a deletion.
type Eraser = (sub: string) => number;
const _erasers = new Map<string, Eraser>();
/** Register a store's eraser, run by eraseUserData. Keyed, so re-registration is harmless. */
export function registerUserDataEraser(name: string, fn: Eraser): void {
  _erasers.set(name, fn);
  console.log(`[registry] GDPR eraser registered: ${name} (${_erasers.size} total)`);
}

// Tables keyed by a single `sub` column.
const SINGLE_TABLES = [
  'identities', 'nicknames', 'player_metrics', 'players', 'achievements', 'votes', 'death_log',
  'cheat_flags', 'points_ledger', 'points_balance', 'points_awarded', 'points_daily',
  'cosmetics_owned', 'cosmetics_equipped', 'cards_owned', 'card_daily', 'market_bans',
  'activity', 'notifications', 'quest_progress', 'quest_claims', 'season_points',
  'discord_prefs', // opt-out-of-rank pref keyed by the Discord sub (audit F1: was a GDPR remnant — nothing else ever deleted it)
  'account_deletions', // pending self-deletion schedule (sub + timestamps) — an admin direct-delete must wipe it too, else the Discord id lingers ~7 days (audit 2026-06-21)
  // ── the item economy (audit 2026-08-03) ──────────────────────────────────────────────────
  // 🚨 EIGHT TABLES CARRYING A SUB SURVIVED "DELETE EVERYTHING", and every one of them was
  // added AFTER this list was written. That is the shape of the defect, not an oversight in
  // any single commit: a hand-maintained list of tables, in a schema that keeps growing. The
  // test that now guards it (ErasureCoverage) enumerates the schema instead of trusting this
  // array, so the next table cannot slip through the same way.
  //
  // Operator 2026-08-03: "si se borra una cuenta de la web, sus objetos han de borrarse."
  'item_instances',   // magic items minted TO this player
  'item_deliveries',  // items bought or won and not yet handed over
  'item_drops',       // their loot-roll history (also the daily cap / cooldown state)
  'shop_purchases',   // what they bought from the operator's shop, and when
  'backpack_pos',     // where they arranged each piece in their bag
  'live_sessions',    // the live-session mirror row, if they are connected as they are erased
  'login_signals',    // a queued login hand-off that would otherwise name them after erasure
  'death_witness',    // an observed death waiting to be published — same reason as login_signals
];

// 🚨 PREPARED ON FIRST USE, NOT AT IMPORT — and that is a correctness fix, not a style choice.
// These used to be prepared while this module loaded, which silently required every table above
// to already exist at that instant. It does not: `item_drops` is created by itemDrops.ts, so
// whether this module or that one loaded first decided whether the PROCESS STARTED AT ALL. It
// threw `no such table: item_drops` out of an import — a boot crash-loop in production, caught
// here only because the suite happens to import the two in the unlucky order.
//
// The list is hand-maintained and will keep growing; preparing lazily means the next name added
// to it can be wrong without taking the site down with it.
let _single: import('node:sqlite').StatementSync[] | null = null;
function singleStatements(): import('node:sqlite').StatementSync[] {
  if (!_single) _single = SINGLE_TABLES.map((t) => db.prepare(`DELETE FROM ${t} WHERE sub = ?`));
  return _single;
}

// 🚨 THE VENDOR STALL ROW IS NOT SYMMETRIC, and copying card_listings would have been wrong.
// Their own listings go, item and all. But a listing they BOUGHT is somebody else's sale, and
// deleting it would erase a third party's history because a stranger closed their account. The
// personal data in that row is the buyer columns, so those are what get removed.
//
// (card_listings above does delete both sides. Stated here so the difference reads as a
// decision rather than as one of the two having been forgotten.)
const qDelMyListings = db.prepare("DELETE FROM item_listings WHERE seller_sub = ?");
const qAnonBuyer = db.prepare('UPDATE item_listings SET buyer_sub = NULL, buyer_nick = NULL WHERE buyer_sub = ?');
const qReporter = db.prepare('DELETE FROM comment_reports WHERE reporter_sub = ?');
// season_winners stores only (season, rank, nickname, points) — no sub column — so
// it must be wiped by the user's nickname, captured BEFORE the nicknames row goes
// (audit 2026-06-23: a GDPR erasure was leaving the pseudonymous snapshot behind).
const qNickForErase = db.prepare('SELECT nickname FROM nicknames WHERE sub = ?');
const qDelSeasonWinners = db.prepare('DELETE FROM season_winners WHERE nickname = ?');
// Tables where the user can be on either side — wiped for both roles.
const PAIRS = [
  'DELETE FROM profile_comments WHERE author_sub = ? OR profile_sub = ?', // their comments + their wall
  'DELETE FROM comment_blocks   WHERE owner_sub = ? OR blocked_sub = ?',
  'DELETE FROM follows          WHERE follower_sub = ? OR target_sub = ?',
  'DELETE FROM card_listings    WHERE seller_sub = ? OR buyer_sub = ?',
].map((sql) => db.prepare(sql));

const UNSAFE = new Set(['__proto__', 'constructor', 'prototype']);

/** Erase every personal record for `sub` across both SQLite databases. Returns
 *  total rows removed. Idempotent. Never throws on a clean call (main-DB wipe is
 *  transactional; cards.db is best-effort). */
export function eraseUserData(sub: string): number {
  if (!sub || sub.startsWith('guest-') || UNSAFE.has(sub)) return 0;
  let removed = 0;
  // IMMEDIATE, via tx(). This transaction READS the nickname before it writes, which is exactly
  // the shape a deferred BEGIN cannot make wait: SQLite fails the upgrade instantly rather than
  // honouring busy_timeout, because retrying would break the snapshot the read already took.
  // Measured in the container: 0 ms to fail deferred, 4003 ms of grace with IMMEDIATE. A GDPR
  // erasure is the last thing that should lose a coin-flip against the other process.
  tx(() => {
    const wnick = (qNickForErase.get(sub) as { nickname?: string } | undefined)?.nickname; // capture before SINGLE wipes nicknames
    for (const st of singleStatements()) removed += Number(st.run(sub).changes) || 0;
    removed += Number(qReporter.run(sub).changes) || 0;
    removed += Number(qDelMyListings.run(sub).changes) || 0;
    removed += Number(qAnonBuyer.run(sub).changes) || 0;
    for (const st of PAIRS) removed += Number(st.run(sub, sub).changes) || 0;
    if (wnick) removed += Number(qDelSeasonWinners.run(wnick).changes) || 0;
  });
  // Dedicated cards.db (separate connection), outside the txn above because two databases cannot
  // share one — that part is unavoidable. What is NOT is what used to happen next.
  //
  // 🚨 THIS FAILURE MUST NOT BE SWALLOWED, and the reason is stronger than tidiness. `eraseUserCards`
  // clears card_owned, card_showcase, card_drops, card_progress and card_escrow, and cardsdb.ts says
  // in its own comment why the last one matters: the boot reconciler settles escrow rows and MINTS
  // the card back to the sub on them. A swallowed failure therefore does not leave a harmless
  // remnant — it leaves a row that RESURRECTS the erased user's ownership on the next restart, while
  // the endpoint has already told them, and the operator, that everything was deleted. Being wrong
  // about a deletion is worse than failing at one: the user stops asking.
  //
  // SQLITE_BUSY is the realistic trigger and it is a RATE here, not an impossibility: two processes
  // have shared this WAL since the proxy split, and the buyItemListing incident was exactly a BUSY
  // on a write nobody expected to fail.
  //
  // One retry, then throw. Throwing is safe precisely because every statement in both halves is a
  // DELETE or an anonymising UPDATE keyed by sub: a caller that retries finishes the job instead of
  // double-counting, so the failure mode is "try again", never "half-erased twice".
  // Same contract for every registered store, and the retry-then-THROW is the load-bearing part:
  // see the reasoning above. A store that fails twice aborts the whole call rather than reporting a
  // deletion that did not happen.
  for (const [name, erase] of _erasers) {
    try {
      removed += erase(sub);
    } catch (first) {
      try {
        removed += erase(sub);
      } catch (second) {
        throw new Error(
          `GDPR erasure incomplete: the main database was wiped but the "${name}" store was not `
          + `(${(second as Error).message}; first attempt: ${(first as Error).message}). `
          + `Retry — every statement is idempotent.`,
        );
      }
    }
  }
  return removed;
}
