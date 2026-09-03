// proxyStats.ts — live, in-memory snapshot of active game WS sessions, for the
// supreme-admin metrics panel's "online now". UOProxy registers each session on
// connect and removes it on close; the metrics route reads a snapshot. ZERO
// persistence and zero duplication of the session objects — we keep only
// {sub, slug, isGuest, since} keyed by the session object's identity.

// 🚨 NOT an import of ./minigames.js. This module only needs to know WHICH slugs are minigame
// shards — a set of strings, not behaviour — and importing the module for it dragged the minigame
// registry into UOProxy's graph, and therefore into every build able to relay a connection.
// Injected instead, empty by default: a self-hosted install has no minigame shards, so the
// distinction below simply never applies there.
let _minigameSlugs: ReadonlySet<string> = new Set();
/** Tell proxyStats which shard slugs are minigame shards (uonexus sets this at startup). */
export function setMinigameSlugs(slugs: ReadonlySet<string>): void {
  _minigameSlugs = slugs;
  console.log(`[registry] minigame slugs injected: ${slugs.size}`);
}

export interface LiveSession {
  sub: string;       // discord sub, or guest-<hex>
  slug: string;      // shard slug this session is on
  isGuest: boolean;
  since: number;     // ms epoch the session opened
  /** Minigame id ('runmatch' | 'towerdefense' | …) when this is a minigame
   *  session — several minigames share ONE shard slug (uonexus), so the slug alone
   *  can't tell them apart. Drives the #112 "playing now" per-game lists. */
  minigame?: string;
  /** UO ACCOUNT NAME on the shard, when the proxy knows it. Needed by the Backpack
   *  mirror: the shard bridge answers `/gear?account=<name>`, and `sub` is a Discord
   *  identity, not a UO account.
   *
   *  Only set for the mini/minigame path, and that asymmetry is the point rather than
   *  an oversight: there the proxy MINTS the account itself (`d<sub>-rm`, `g<hex>-td`)
   *  and rewrites the login packet with it, so it knows the name by construction. On an
   *  ordinary shard the player types their own account into the client and the proxy
   *  only relays that login — it never learns the name, and guessing one would be worse
   *  than admitting we do not have it.
   *
   *  So: undefined means "cannot mirror this session", never "no account". Any consumer
   *  must degrade to the out-of-service panel rather than invent a name. */
  account?: string;
  /** Force-close hook supplied by UOProxy (closure over session.destroy()).
   *  Lets the admin "kick" endpoint terminate a session without proxyStats
   *  importing UOProxy (no circular dep). Optional for back-compat. */
  destroy?: () => void;
}

const live = new Map<object, LiveSession & { id: number }>();
let _nextSessionId = 1;

/** Register an active session keyed by an opaque identity (the UOProxy session). */
export function trackSession(key: object, info: LiveSession): void {
  live.set(key, { ...info, id: _nextSessionId++ });
}

/** Remove a session on close. Safe to call for an unknown key. */
export function untrackSession(key: object): void {
  live.delete(key);
}

/** Snapshot of every live session for the admin panel (no destroy hook leaked). */
export function listSessions(): Array<{ id: number; sub: string; slug: string; isGuest: boolean; since: number }> {
  const out: Array<{ id: number; sub: string; slug: string; isGuest: boolean; since: number }> = [];
  for (const s of live.values()) out.push({ id: s.id, sub: s.sub, slug: s.slug, isGuest: s.isGuest, since: s.since });
  out.sort((a, b) => a.since - b.since);
  return out;
}

/** Active sessions for one minigame id (#112 "playing now"). De-duplicated by sub — a
 *  player with two tabs of the same minigame counts once — keeping the earliest `since`. */
export function listMinigameSessions(minigame: string): Array<{ sub: string; isGuest: boolean; since: number }> {
  const bySub = new Map<string, { sub: string; isGuest: boolean; since: number }>();
  for (const s of live.values()) {
    if (s.minigame !== minigame) continue;
    const cur = bySub.get(s.sub);
    if (!cur || s.since < cur.since) bySub.set(s.sub, { sub: s.sub, isGuest: s.isGuest, since: s.since });
  }
  return [...bySub.values()];
}

/** Force-close one session by its snapshot id. Returns true if found+kicked. */
export function kickSession(id: number): boolean {
  for (const s of live.values()) {
    if (s.id === id) {
      try { s.destroy?.(); } catch { /* already gone */ }
      return true;
    }
  }
  return false;
}

export interface OnlineSnapshot {
  total: number;
  discord: number;
  guest: number;
  byShard: Record<string, number>;
}

/** Aggregate of who is connected to a game server right now. */
export function onlineSnapshot(): OnlineSnapshot {
  let discord = 0;
  let guest = 0;
  const byShard: Record<string, number> = {};
  for (const s of live.values()) {
    if (s.isGuest) guest++; else discord++;
    byShard[s.slug] = (byShard[s.slug] || 0) + 1;
  }
  return { total: live.size, discord, guest, byShard };
}

/** True if `sub` has at least one live game session right now. */
export function hasLiveSession(sub: string): boolean {
  for (const s of live.values()) if (s.sub === sub) return true;
  return false;
}

/** The shard slug of `sub`'s most-recent live session, or null. Drives per-server
 *  metric/achievement attribution at metrics-report time.
 *
 *  `preferMinigame` (2026-07-02, Minigames overlay): a player can now run a game
 *  client AND the TBH mini AT ONCE — two live sessions, one JWT. "Most recent"
 *  then cross-attributes one client's report to the other's shard, which either
 *  robs real play of achievement/quest credit or lets the idle bar's simulated
 *  kills AFK-farm the default ladders (the exact leak the minigame partition
 *  closed). The reporting client declares its bundle (`mini` flag) and we pick
 *  the most-recent live session MATCHING it; no match (or no flag from an older
 *  client) falls back to the most-recent session overall. Only a disambiguator
 *  among the user's own REAL sessions — the slug itself stays server-side. */
/** The UO account + shard of `sub`'s most-recent live session that HAS an account,
 *  or null. Feeds the Backpack mirror, which must ask the right shard about the right
 *  character.
 *
 *  Returns both together on purpose: they are only meaningful as a pair. An account
 *  name means nothing without knowing which shard to ask, and asking the wrong shard
 *  would either 404 or — worse — hit a same-named account somewhere else.
 *
 *  Sessions without an account are SKIPPED rather than returned with a blank: a session
 *  the proxy cannot name is one the mirror cannot show, and the caller must render
 *  "out of service" instead of an empty backpack. An empty backpack is a claim about
 *  the player; "unknown" is a claim about us. */
export function liveAccountForSub(sub: string): { account: string; slug: string } | null {
  let best: { id: number; account: string; slug: string } | null = null;
  for (const s of live.values()) {
    if (s.sub !== sub || !s.account) continue;
    if (!best || s.id > best.id) best = { id: s.id, account: s.account, slug: s.slug };
  }
  return best ? { account: best.account, slug: best.slug } : null;
}

export function liveSlugForSub(sub: string, preferMinigame?: boolean): string | null {
  let best: { id: number; slug: string } | null = null;
  let bestAny: { id: number; slug: string } | null = null;
  for (const s of live.values()) {
    if (s.sub !== sub) continue;
    if (!bestAny || s.id > bestAny.id) bestAny = { id: s.id, slug: s.slug };
    if (preferMinigame === undefined || _minigameSlugs.has(s.slug) !== preferMinigame) continue;
    if (!best || s.id > best.id) best = { id: s.id, slug: s.slug };
  }
  // Review fix 2026-07-02: an EXPLICIT flag with no matching live session must NOT fall back to
  // the opposite session — the pagehide `keepalive` beat races its own WS teardown, so the mini's
  // final kills would land on the still-live GAME shard (or a real-play beat on the minigame slug)
  // on every overlay close. Explicit-but-unmatched → null ('_unknown' at the caller: counted in an
  // internal bucket, excluded from quests/unlocks/public surfaces). No flag (older client) keeps
  // the legacy most-recent fallback.
  if (preferMinigame !== undefined) return best ? best.slug : null;
  return bestAny ? bestAny.slug : null;
}
