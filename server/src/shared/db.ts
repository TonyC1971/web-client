// db.ts — single SQLite database for the relational app data (votes, player
// metrics, achievements, identities, server-order pins). Uses Node 22's built-in
// node:sqlite (DatabaseSync), so ZERO npm deps / native build — works on Alpine /
// NAS / any arch. Requires the runtime flag `--experimental-sqlite` (set in
// package.json "start"). The DB file lives at DATA_PATH/uonexus.db and is just as
// SMB-backuppable as the JSONs it replaces (the Sidecar A snapshot picks it up).
//
// WHY: the previous JSON-per-store pattern rewrote the WHOLE file on every change
// (O(n) writes). Fine for KB-sized stores, but player_metrics grows with active
// players, so at scale that became the bottleneck. SQLite gives per-row writes +
// indexed queries while staying a single backuppable file.
//
// Profiles stay as .tar.gz blobs on disk (opaque, non-relational) — NOT migrated.
//
// MIGRATION: on first boot each store calls migrateJsonOnce(<legacy file>, importer)
// which imports the JSON into its table (in a transaction) then renames the JSON to
// <file>.migrated so it's never re-read. No data loss; the .migrated file stays as a
// belt-and-braces backup until an operator removes it.

import { DatabaseSync } from 'node:sqlite';
import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { DATA_PATH } from './config.js';
import { OWNS_JOBS } from './proxyRole.js';

// `node --test` runs each test file in its own parallel process; an on-disk shared
// DB would race on open/WAL/migrate. Under the test runner, use a per-process
// in-memory DB so every test file is isolated and never touches DATA_PATH.
// NOTE: when the runner spawns each file in a child process it does NOT pass
// `--test` down to that child — it sets process.env.NODE_TEST_CONTEXT instead. The
// argv/execArgv check only catches the single-file/in-process case, so without the
// env check the spawned children fall back to the on-disk DB and race on the WAL
// lock (SQLITE_BUSY). NODE_TEST_CONTEXT is set ONLY by node:test, never in prod.
const IS_TEST =
  process.execArgv.includes('--test') ||
  process.argv.includes('--test') ||
  Boolean(process.env.NODE_TEST_CONTEXT);
const DB_FILE = IS_TEST ? ':memory:' : path.join(DATA_PATH, 'uonexus.db');
if (!IS_TEST) fs.mkdirSync(DATA_PATH, { recursive: true });

export const db = new DatabaseSync(DB_FILE);

// WAL: concurrent readers + faster writes; NORMAL sync is the standard durable-
// enough setting under WAL. busy_timeout absorbs SQLITE_BUSY while another writer
// holds the lock.
//
// 🚨 NO LONGER A SINGLE PROCESS (2026-07-29). The proxy runs as TWO processes over this
// same file: PROXY_ROLE=web serves the portal and does its writes, PROXY_ROLE=game owns
// the scheduled jobs (GDPR reaping, presence sampling, audit maintenance) and does theirs.
// See server/src/proxyRole.ts and docs/PROXY_SPLIT.md. WAL permits many readers and ONE
// writer, so the two serialise on the write lock and busy_timeout is what keeps that
// invisible. Two consequences worth knowing before touching anything here:
//
//   1. Any write transaction held longer than busy_timeout makes the OTHER process throw
//      SQLITE_BUSY. Keep write transactions short; never wrap a network call or a large
//      file operation inside one.
//   2. The schema below is safe under two processes only because it is IDEMPOTENT
//      (CREATE ... IF NOT EXISTS) — both can run it at boot and the second is a no-op.
//      A DATA migration (ALTER + backfill, dedupe, recompute) has NO such protection and
//      would run TWICE, concurrently, on two processes started together by
//      `up -d proxy proxy-web`. Such a migration needs an explicit owner: gate it on
//      OWNS_JOBS from proxyRole.ts, the same way the scheduled jobs are gated.
db.exec('PRAGMA journal_mode = WAL');
db.exec('PRAGMA synchronous = NORMAL');
db.exec('PRAGMA busy_timeout = 4000');
db.exec('PRAGMA foreign_keys = ON');

// REENTRANT (audit hardening 2026-06-25): a top-level call is a real IMMEDIATE
// transaction; a NESTED call (e.g. buyCosmetic wraps its own-check + spendPoints +
// grant in one tx, and spendPoints itself calls tx) becomes a SAVEPOINT so the
// whole compound op is atomic — own-check, debit and grant either all commit or all
// roll back, no longer relying on the fragile "no await between the lines" invariant.
let _txDepth = 0;
export function tx<T>(fn: () => T): T {
  const nested = _txDepth > 0;
  const sp = '_sp' + _txDepth;
  if (nested) db.exec('SAVEPOINT ' + sp); else db.exec('BEGIN IMMEDIATE');
  _txDepth++;
  try {
    const v = fn();
    // 🚨 REFUSE AN ASYNC CALLBACK. The signature says `() => T`, which reads as synchronous,
    // but `() => Promise<T>` satisfies it with T = Promise<...>. An async callback would
    // COMMIT here, before its work has happened — and worse, it would hold the write lock
    // across whatever it awaits.
    //
    // That second half is a cross-process hazard since the web/game split. db.ts states the
    // rule: "any write transaction held longer than busy_timeout makes the OTHER process
    // throw SQLITE_BUSY. Keep write transactions short; never wrap a network call or a large
    // file operation inside one." The other process is the one crediting playtime,
    // achievements and cards from /api/metrics/report, so an await in here does not just
    // corrupt this transaction — it can silently cost players their rewards.
    //
    // The rule was documented and enforced by nothing. It is enforced now, at the one place
    // every write transaction passes through. The item paths added on 2026-07-31 make real
    // ~2.5s bridge calls, so the distance between "documented" and "someone wraps one" got
    // considerably shorter.
    if (v && typeof (v as { then?: unknown }).then === 'function') {
      if (nested) { db.exec('ROLLBACK TO ' + sp); db.exec('RELEASE ' + sp); } else db.exec('ROLLBACK');
      _txDepth--;
      throw new Error('tx() callback returned a Promise. A write transaction must be SYNCHRONOUS: '
        + 'it would commit before the work happened, and hold the write lock across the await, '
        + 'which makes the other proxy process throw SQLITE_BUSY. Do the async work OUTSIDE tx().');
    }
    if (nested) db.exec('RELEASE ' + sp); else db.exec('COMMIT');
    _txDepth--;
    return v;
  } catch (e) {
    try { if (nested) { db.exec('ROLLBACK TO ' + sp); db.exec('RELEASE ' + sp); } else db.exec('ROLLBACK'); } catch { /* */ }
    _txDepth--;
    throw e;
  }
}

// ── Schema (idempotent — safe to run every boot) ─────────────────────────────
db.exec(`
  CREATE TABLE IF NOT EXISTS votes (
    sub        TEXT NOT NULL,
    slug       TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (sub, slug)
  );
  CREATE INDEX IF NOT EXISTS idx_votes_slug ON votes(slug);

  -- Per-server (operator 2026-06-15): counters are tracked per (sub, counter, slug)
  -- so a server's data can be wiped and achievements tracked per server. Existing
  -- DBs are migrated below (legacy rows get slug='_legacy').
  CREATE TABLE IF NOT EXISTS player_metrics (
    sub     TEXT NOT NULL,
    counter TEXT NOT NULL,
    slug    TEXT NOT NULL DEFAULT '_legacy',
    value   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (sub, counter, slug)
  );

  CREATE TABLE IF NOT EXISTS players (
    sub        TEXT PRIMARY KEY,
    first_seen INTEGER NOT NULL,
    last_seen  INTEGER NOT NULL
  );

  CREATE TABLE IF NOT EXISTS achievements (
    sub         TEXT NOT NULL,
    ach_id      TEXT NOT NULL,
    slug        TEXT NOT NULL DEFAULT '_legacy',
    unlocked_at INTEGER NOT NULL,
    manual      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (sub, ach_id, slug)
  );

  CREATE TABLE IF NOT EXISTS identities (
    sub    TEXT PRIMARY KEY,
    name   TEXT NOT NULL,
    avatar TEXT,
    seen   INTEGER NOT NULL
  );
  -- v0.8.43 (GDPR-minimising): a user-chosen PUBLIC nickname. Public rankings
  -- show THIS, never the Discord name/snowflake. Having one = opt-in to appear;
  -- none = excluded from public leaderboards. Unique (case-insensitive via
  -- nickname_lc) to stop impersonation.
  -- locked: the user has spent their ONE custom rename (random default = 0).
  -- hidden: opt-out of public rankings (GDPR right to object — always toggleable,
  --         independent of the name lock). Rankings exclude hidden = 1.
  CREATE TABLE IF NOT EXISTS nicknames (
    sub         TEXT PRIMARY KEY,
    nickname    TEXT NOT NULL,
    nickname_lc TEXT NOT NULL UNIQUE,
    locked      INTEGER NOT NULL DEFAULT 0,
    hidden      INTEGER NOT NULL DEFAULT 0,
    set_at      INTEGER NOT NULL,
    -- Steam-style public profile visibility (operator 2026-06-16) — BINARY:
    -- 'public' (or NULL/legacy) = anyone, 'private' = owner only. (The 'followers'
    -- tier was retired 2026-06-22: a follow is a notify subscription, not privacy.)
    privacy     TEXT NOT NULL DEFAULT 'public'
  );

  CREATE TABLE IF NOT EXISTS server_pins (
    slug TEXT PRIMARY KEY,
    rank INTEGER NOT NULL
  );

  CREATE TABLE IF NOT EXISTS runtime_config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
  );

  -- Vote-tally history for the admin chart: one row per (snapshot ts, slug).
  -- Written by votes.recordTallySnapshot() after every vote change + a 6h
  -- idle timer; read by GET /api/admin/votes/history. Append-only, tiny
  -- (rows only when tallies CHANGE), pruned past 180 days on write.
  CREATE TABLE IF NOT EXISTS vote_history (
    ts    INTEGER NOT NULL,
    slug  TEXT NOT NULL,
    votes INTEGER NOT NULL,
    PRIMARY KEY (ts, slug)
  );
  CREATE INDEX IF NOT EXISTS idx_vote_history_slug ON vote_history(slug, ts);

  -- Death feed (operator 2026-06-11): who died to whom, for the public
  -- rankings widget's "recent deaths" + future forensics. killer is the
  -- CLIENT-reported probable killer name (best-effort heuristic — the
  -- victim's last attack target), sanitized + capped server-side. Pruned
  -- to the most recent 500 rows on insert.
  CREATE TABLE IF NOT EXISTS death_log (
    id     INTEGER PRIMARY KEY AUTOINCREMENT,
    ts     INTEGER NOT NULL,
    sub    TEXT NOT NULL,
    killer TEXT
  );
  CREATE INDEX IF NOT EXISTS idx_death_log_ts ON death_log(ts);

  -- Anti-cheat advisory flags (operator 2026-06-16): heuristics raise a flag when
  -- an achievement unlocks implausibly fast (rate vs online time) or many unlock in
  -- a short window (burst). FLAGS ONLY — never auto-punish; the admin reviews them
  -- and decides to revoke all/one achievement or dismiss. UNIQUE(sub,rule,ach_id,slug)
  -- so each distinct suspicion is recorded once (INSERT OR IGNORE). resolved=1 once
  -- the admin dismisses it (kept for audit, hidden from the active list).
  CREATE TABLE IF NOT EXISTS cheat_flags (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    sub        TEXT NOT NULL,
    slug       TEXT NOT NULL DEFAULT '',
    rule       TEXT NOT NULL,
    ach_id     TEXT NOT NULL DEFAULT '',
    detail     TEXT NOT NULL,
    severity   INTEGER NOT NULL DEFAULT 1,
    created_at INTEGER NOT NULL,
    resolved   INTEGER NOT NULL DEFAULT 0,
    UNIQUE (sub, rule, ach_id, slug)
  );
  CREATE INDEX IF NOT EXISTS idx_cheat_flags_sub ON cheat_flags(sub);
  CREATE INDEX IF NOT EXISTS idx_cheat_flags_active ON cheat_flags(resolved, created_at);

  -- ── Points economy (operator 2026-06-16, Steam-community system) ──────────
  -- APPEND-ONLY ledger; a derived points_balance rollup so reads never SUM the
  -- whole ledger (keeps the synchronous node:sqlite queries O(1) — see the
  -- "keep queries indexed/cheap" rationale). Idempotency via points_awarded so a
  -- source can't double-credit. High-volume chat/playtime points accrue in DAILY
  -- buckets (points_daily) so a chat-farm can't bloat the ledger and the per-day
  -- cap is enforceable. Source weights + the (<=20) Discord channels are
  -- operator-tunable JSON in runtime_config ('points-config').
  CREATE TABLE IF NOT EXISTS points_ledger (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    sub        TEXT NOT NULL,
    delta      INTEGER NOT NULL,            -- +earned / -spent
    reason     TEXT NOT NULL,               -- achievement|leaderboard|playtime|discord|card|shop|admin
    ref        TEXT NOT NULL DEFAULT '',    -- ach_id / channelId / item / listing id
    created_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS idx_points_ledger_sub ON points_ledger(sub, id DESC);

  CREATE TABLE IF NOT EXISTS points_balance (
    sub        TEXT PRIMARY KEY,
    balance    INTEGER NOT NULL DEFAULT 0,  -- spendable now
    lifetime   INTEGER NOT NULL DEFAULT 0,  -- total ever earned (drives level; never decreases)
    updated_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS idx_points_balance_lifetime ON points_balance(lifetime DESC);

  -- Idempotency guard: key = '<reason>:<ref>[:<bucket>]'. A source records the key
  -- the first time it credits a sub; re-credit is a no-op.
  CREATE TABLE IF NOT EXISTS points_awarded (
    sub TEXT NOT NULL,
    key TEXT NOT NULL,
    PRIMARY KEY (sub, key)
  );

  -- Per-(sub,bucket,day) accrual + cap + cooldown tracking for high-frequency
  -- sources (Discord chat per channel, playtime). bucket e.g. 'discord:<channelId>'.
  CREATE TABLE IF NOT EXISTS points_daily (
    sub     TEXT NOT NULL,
    bucket  TEXT NOT NULL,
    day     TEXT NOT NULL,                  -- YYYY-MM-DD (UTC)
    points  INTEGER NOT NULL DEFAULT 0,
    last_at INTEGER NOT NULL DEFAULT 0,     -- ms epoch of last credit (cooldown)
    PRIMARY KEY (sub, bucket, day)
  );

  -- ── Cosmetics (Steam-community Phase 2, operator 2026-06-16) ──────────────
  -- The catalog itself is code (cosmetics.ts). These tables track what each
  -- player OWNS (bought with points, or earned by level/achievement) and what
  -- they have EQUIPPED per slot (frame/nameplate/theme), applied to the profile.
  CREATE TABLE IF NOT EXISTS cosmetics_owned (
    sub         TEXT NOT NULL,
    item_id     TEXT NOT NULL,
    source      TEXT NOT NULL DEFAULT 'buy',   -- buy | level | achievement | admin
    acquired_at INTEGER NOT NULL,
    PRIMARY KEY (sub, item_id)
  );
  CREATE TABLE IF NOT EXISTS cosmetics_equipped (
    sub     TEXT NOT NULL,
    slot    TEXT NOT NULL,                      -- frame | nameplate | theme | discord
    item_id TEXT NOT NULL,
    PRIMARY KEY (sub, slot)
  );

  -- Magic-item INSTANCES (docs/MAGIC_ITEMS.md). Not the catalogue: two drops of the same
  -- base differ, so base_id points at an existing pd: cosmetic and the roll lives here.
  -- 🚨 NO BACKTICKS ANYWHERE IN THIS FILE: the schema is one template literal, so a
  -- backtick in a comment closes it and tsc then reports a syntax error dozens of
  -- lines away from the cause. Written twice today, in this very block, both times.
  -- 🪦 DEAD TABLE. Nothing reads or writes it any more (2026-07-31): itemsdb.ts is deleted,
  -- and POST /api/internal/item-batch, GET /api/me/items and the /admin pool gauge went with
  -- it. The architecture changed on 2026-07-29 (docs/MAGIC_ITEMS.md) — the web stores NO item
  -- state, the shard owns every item, and the web reads them live through /api/gear.
  --
  -- Measured before removing the code, rather than assumed: 2 rows in the whole table, ZERO
  -- of them owned by anybody, both leftovers from probe batches. Nothing belonging to a
  -- player was destroyed.
  --
  -- Kept as a CREATE rather than DROPped on purpose: dropping is irreversible and buys
  -- nothing, while the statement below keeps an older database openable. It is data the
  -- OPERATOR may remove whenever they like — backups are theirs, not ours.
  --
  -- 🚨 DO NOT REBUILD TRADING ON THIS. A web-side owner row is a second source of truth for
  -- who holds an item, which is exactly what the operator vetoed ("duplicidades, exploits en
  -- mercado"). What replaced each half: minting -> the shard on demand (POST /item-grant),
  -- trading -> the shard escrow vault (WebBridgeStalls.cs + itemMarket.ts), showing ->
  -- /api/gear with itemart.js.
  CREATE TABLE IF NOT EXISTS item_instances (
    id        TEXT PRIMARY KEY,
    sub       TEXT,
    base_id   TEXT NOT NULL,
    source    TEXT NOT NULL,
    rarity    TEXT NOT NULL DEFAULT 'common',
    affixes   TEXT NOT NULL DEFAULT '[]',
    rolled_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS idx_item_instances_sub ON item_instances(sub);
  CREATE INDEX IF NOT EXISTS idx_item_instances_pool ON item_instances(rolled_at) WHERE sub IS NULL;

  -- Where each piece SITS inside the backpack (operator 2026-07-28: "deberíamos poder
  -- mover los objetos en el backpack a la posición que queramos"). Positions used to be a
  -- stable hash of nickname+slug -- repeatable, but not chosen.
  --
  -- 🚨 No backticks in this block: the whole schema is a TEMPLATE LITERAL, so a backtick
  -- in a comment closes it and the file stops parsing.
  -- Keyed by ref, deliberately NOT by cosmetic id: today a ref is a paperdoll art slug,
  -- which is what the backpack draws and what a drag carries, and when magic items arrive
  -- (docs/MAGIC_ITEMS.md) an item INSTANCE id drops into the same column. Coordinates are
  -- PERCENTAGES of the container, so a backpack rendered at a different size keeps the
  -- arrangement.
  CREATE TABLE IF NOT EXISTS backpack_pos (
    sub TEXT NOT NULL,
    ref TEXT NOT NULL,
    x   REAL NOT NULL,
    y   REAL NOT NULL,
    PRIMARY KEY (sub, ref)
  );

  -- Admin-managed shop overlay (operator 2026-06-24). Admins curate the shop from
  -- the shop UI itself: disable any item, ADD custom items, and REORDER within a
  -- slot. The hardcoded COSMETICS array (cosmetics.ts) stays the immutable base;
  -- this table layers on top. A row's meaning depends on the custom column:
  --   custom=1 = a full admin-created cosmetic (all definition columns populated)
  --   custom=0 = an OVERRIDE for a hardcoded item (only disabled / sort_order apply)
  -- Removing an item = soft-disable (disabled=1): hidden from the shop but restorable,
  -- owners keep what they bought. PERMANENTLY deleting it sets deleted=1 (a tombstone
  -- for a hardcoded item, so it is gone from the catalog AND the admin view with no
  -- Restore) — a custom item is hard-deleted (row removed) instead; either way the
  -- item's ownership/equip rows are purged.
  CREATE TABLE IF NOT EXISTS cosmetics_overlay (
    id           TEXT PRIMARY KEY,
    custom       INTEGER NOT NULL DEFAULT 0,
    slot         TEXT,
    name         TEXT,
    rarity       TEXT,
    cost         INTEGER,
    unlock_type  TEXT,
    unlock_value TEXT,
    value        TEXT,
    event        TEXT,
    disabled     INTEGER NOT NULL DEFAULT 0,
    deleted      INTEGER NOT NULL DEFAULT 0,      -- 1 = permanently removed (tombstone); excluded everywhere
    sort_order   INTEGER,                        -- explicit per-slot order; NULL = default (catalog position)
    created_by   TEXT,
    created_at   INTEGER,
    updated_at   INTEGER
  );
  CREATE INDEX IF NOT EXISTS idx_cosmetics_overlay_slot ON cosmetics_overlay(slot);

  -- ── Discord per-user preferences (operator 2026-06-19) ────────────────────
  -- opt_out_rank: the player has "desmarcado" themselves from the automatic
  -- level-rank role so their bought Discord nick colour shows instead. The sub
  -- is the Discord user id (OAuth subject). syncMemberRoles honours this flag.
  CREATE TABLE IF NOT EXISTS discord_prefs (
    sub          TEXT PRIMARY KEY,
    opt_out_rank INTEGER NOT NULL DEFAULT 0,
    updated_at   INTEGER NOT NULL DEFAULT 0
  );

  -- Pending self-deletion schedule (mirrors accountDeletion.ts's own
  -- CREATE IF NOT EXISTS — declared here too so it exists before userData.ts
  -- prepares its GDPR-erasure DELETE against it, regardless of module load order).
  CREATE TABLE IF NOT EXISTS account_deletions (
    sub          TEXT PRIMARY KEY,
    requested_at INTEGER NOT NULL,
    scheduled_at INTEGER NOT NULL
  );

  -- ── Profile comments + moderation (Steam-community Phase 3) ───────────────
  -- A wall of comments on a player's profile. author_nick is a SNAPSHOT (GDPR:
  -- shown publicly; the sub stays server-side). Soft-delete via hidden=1. Reports
  -- bump a counter; comment_reports dedupes per reporter; comment_blocks lets a
  -- wall owner stop a user from posting on their wall.
  CREATE TABLE IF NOT EXISTS profile_comments (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_sub TEXT NOT NULL,
    author_sub  TEXT NOT NULL,
    author_nick TEXT NOT NULL,
    body        TEXT NOT NULL,
    created_at  INTEGER NOT NULL,
    hidden      INTEGER NOT NULL DEFAULT 0,
    reports     INTEGER NOT NULL DEFAULT 0
  );
  CREATE INDEX IF NOT EXISTS idx_profile_comments_wall ON profile_comments(profile_sub, id DESC);
  CREATE TABLE IF NOT EXISTS comment_reports (
    comment_id   INTEGER NOT NULL,
    reporter_sub TEXT NOT NULL,
    created_at   INTEGER NOT NULL,
    PRIMARY KEY (comment_id, reporter_sub)
  );
  CREATE TABLE IF NOT EXISTS comment_blocks (
    owner_sub   TEXT NOT NULL,
    blocked_sub TEXT NOT NULL,
    created_at  INTEGER NOT NULL,
    PRIMARY KEY (owner_sub, blocked_sub)
  );

  -- Follows (Phase 3): follower_sub follows target_sub. Drives follower/following
  -- counts + the activity feed.
  CREATE TABLE IF NOT EXISTS follows (
    follower_sub TEXT NOT NULL,
    target_sub   TEXT NOT NULL,
    created_at   INTEGER NOT NULL,
    PRIMARY KEY (follower_sub, target_sub)
  );
  CREATE INDEX IF NOT EXISTS idx_follows_target ON follows(target_sub);

  -- ── Trading cards (Steam-community Phase 4) ───────────────────────────────
  -- The catalog is code (cards.ts). cards_owned tracks per-player counts (>1 =
  -- spare copies, tradeable). card_daily caps how many cards drop per day from
  -- playtime (Steam-style drops). The marketplace (Phase 4b) adds listings.
  CREATE TABLE IF NOT EXISTS cards_owned (
    sub         TEXT NOT NULL,
    card_id     TEXT NOT NULL,
    qty         INTEGER NOT NULL DEFAULT 1,
    acquired_at INTEGER NOT NULL,
    PRIMARY KEY (sub, card_id)
  );
  CREATE TABLE IF NOT EXISTS card_daily (
    sub   TEXT NOT NULL,
    day   TEXT NOT NULL,
    drops INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (sub, day)
  );

  -- Card marketplace (Phase 4b): sell spare cards for POINTS — the only path that
  -- moves points between players. The listed card is ESCROWED (removed from the
  -- seller on listing, returned on cancel, handed to the buyer on sale). market_bans
  -- shadowbans a user from listing/buying.
  CREATE TABLE IF NOT EXISTS card_listings (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    seller_sub  TEXT NOT NULL,
    seller_nick TEXT NOT NULL,
    card_id     TEXT NOT NULL,
    foil        INTEGER NOT NULL DEFAULT 0,   -- v0.9.87: a listing sells a SPECIFIC instance
    aura        TEXT NOT NULL DEFAULT '',     --          (card_id+foil+aura), escrowed as that state
    price       INTEGER NOT NULL,
    status      TEXT NOT NULL DEFAULT 'active',  -- active|sold|cancelled|reversed
    buyer_sub   TEXT,
    buyer_nick  TEXT,
    created_at  INTEGER NOT NULL,
    sold_at     INTEGER,
    escrow_token TEXT                            -- links to cards.db card_escrow (cross-DB durability)
  );
  CREATE INDEX IF NOT EXISTS idx_listings_active ON card_listings(status, card_id);
  -- El bazar por defecto lista TODO lo activo ordenado por precio, y el indice de
  -- arriba (status, card_id) no sirve para esa ordenacion: SQLite filtra por status y
  -- luego ordena el conjunto entero. Medido con 50.000 anuncios (75% activos):
  -- 10,52 ms -> 0,13 ms, unas 80 veces mas rapido. Cabe recordar que DatabaseSync es
  -- SINCRONO, asi que esos 10 ms no eran latencia de una peticion: eran 10 ms en los
  -- que el proxy no relayaba un solo paquete de juego.
  --
  -- Se anade aqui y NO en player_metrics, donde el equivalente se midio y se descarto
  -- (ver docs/SERVER_QUERY_COST.md): alli el indice util abarcaba la fila entera y la
  -- tabla se escribe en cada credito de presencia. card_listings se escribe al publicar
  -- o vender — raro — y este indice son tres columnas estrechas.
  CREATE INDEX IF NOT EXISTS idx_listings_price ON card_listings(status, price, id);
  CREATE INDEX IF NOT EXISTS idx_listings_seller ON card_listings(seller_sub, id DESC);
  CREATE TABLE IF NOT EXISTS market_bans (
    sub        TEXT PRIMARY KEY,
    created_at INTEGER NOT NULL
  );

  -- ── Vendor Stalls: player listings + the operator's own shop ──────────────
  -- A player stall sells a REAL SHARD ITEM, never a web record of one. The item is MOVED
  -- into the shard's escrow vault (Custom/Web/WebBridgeStalls.cs) when it is listed and
  -- moved into the buyer's pack when it sells, so exactly ONE copy exists at every
  -- instant. That is what keeps this from being a second source of truth for ownership --
  -- the thing the operator vetoed when the pre-minted pool was dropped ("duplicidades,
  -- exploits en mercado").
  --
  -- item_json is a DISPLAY SNAPSHOT taken at listing time: it is what the stall draws, it
  -- is NOT authoritative, and a listing whose serial has left escrow is reconciled against
  -- the shard rather than believed.
  --
  -- 'pending' is the state between "row written" and "shard confirmed the move". A hard
  -- kill in that window leaves a pending row, which the boot reconciler resolves by ASKING
  -- the shard whether that serial is in escrow -- so no separate journal is needed here,
  -- unlike cards, where the counterpart lives in a second database we cannot query mid-tear.
  CREATE TABLE IF NOT EXISTS item_listings (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    seller_sub  TEXT NOT NULL,
    seller_nick TEXT NOT NULL,
    shard       TEXT NOT NULL,                   -- slug of the bridge holding the escrow
    serial      INTEGER NOT NULL,                -- the shard item, sitting in the vault
    item_json   TEXT NOT NULL DEFAULT '',        -- display snapshot (see above)
    price       INTEGER NOT NULL,
    status      TEXT NOT NULL DEFAULT 'pending', -- pending|active|sold|cancelled|reversed
    buyer_sub   TEXT,
    buyer_nick  TEXT,
    created_at  INTEGER NOT NULL,
    sold_at     INTEGER
  );
  CREATE INDEX IF NOT EXISTS idx_item_listings_active ON item_listings(status, price, id);
  CREATE INDEX IF NOT EXISTS idx_item_listings_seller ON item_listings(seller_sub, id DESC);

  -- The operator's OWN shop. No escrow at all: nothing pre-exists, so a purchase MINTS the
  -- item from a (type, tier) recipe and decrements stock. That is why it can offer X units
  -- of the same thing while a player stall holds one of each.
  CREATE TABLE IF NOT EXISTS shop_items (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    type_name  TEXT NOT NULL,                   -- a ModernUO type name, e.g. "Kryss"
    label      TEXT NOT NULL DEFAULT '',        -- what the stall calls it ('' = the type's own name)
    tier       TEXT NOT NULL DEFAULT '',        -- ''|rich|filthyrich|ultrarich|superboss
    -- Art id and weight, resolved from the shard when the row is SAVED. Cached rather than
    -- looked up per view: the stall panel draws every row on every render, and the shard's
    -- /item-info costs an instantiate-and-delete on its game loop per type. They cannot go
    -- stale in a way that matters — a base item's sprite and weight are fixed by the fileset.
    art        INTEGER NOT NULL DEFAULT 0,
    weight     REAL NOT NULL DEFAULT 0,
    price      INTEGER NOT NULL,
    stock      INTEGER NOT NULL,                -- units left; 0 = sold out
    sort       INTEGER NOT NULL DEFAULT 0,      -- operator ordering, like the cosmetic shop
    enabled    INTEGER NOT NULL DEFAULT 1,
    created_at INTEGER NOT NULL
  );

  -- 🚨 ONE UNIT PER PLAYER PER DAY of any given official item (operator 2026-07-31).
  -- Without it somebody drains the twenty daggers and re-lists them in their own stall at
  -- a markup, which turns the shop into a points printer.
  --
  -- A LEDGER of purchases rather than a "last bought" column, because the window has to be
  -- a ROLLING 24h: a calendar day would let the same player buy at 23:59 and again at
  -- 00:01, which is exactly the burst the cap exists to stop.
  CREATE TABLE IF NOT EXISTS shop_purchases (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    sub       TEXT NOT NULL,
    shop_item INTEGER NOT NULL,
    bought_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS idx_shop_purchases ON shop_purchases(sub, shop_item, bought_at);

  -- A sale completes even when the counterparty is not connected: the item waits and this
  -- row remembers whose it is now. ONE queue for both kinds of hand-over, because the thing
  -- that actually varies is a single step at the end:
  --   serial IS NOT NULL -> RELEASE that escrowed item (a player stall sale, or a cancel
  --                         returning it to the seller). The item already exists.
  --   serial IS NULL     -> MINT type_name at tier (an official-shop purchase). Nothing
  --                         exists yet, so there is nothing to escrow.
  -- Two tables would have duplicated the delivery-on-connect machinery, which is the part
  -- with the sharp edges, to express a difference of one branch.
  --
  -- 🚨 Marked delivered ONLY when the shard confirms. Same rule as a queued loot drop and
  -- for the same reason: this is property somebody already paid for, not state that
  -- re-converges on the next login. Marking it optimistically loses the item for good.
  -- Loot rolls: what the web decided to hand out, and the daily cap / cooldown state.
  -- Lives here rather than in itemDrops.ts so that anything erasing it (userData) can
  -- prepare against it without depending on module import order.
  CREATE TABLE IF NOT EXISTS item_drops (
    id    INTEGER PRIMARY KEY AUTOINCREMENT,
    sub   TEXT NOT NULL,
    tier  TEXT NOT NULL,
    at    INTEGER NOT NULL,
    day   TEXT NOT NULL,
    -- 'pending' until the shard confirms it landed, then 'granted'. Rows are never deleted
    -- on delivery: this is the audit trail for what the web handed out.
    state TEXT NOT NULL DEFAULT 'pending',
    item  TEXT
  );
  CREATE INDEX IF NOT EXISTS idx_item_drops_sub_day ON item_drops(sub, day);
  CREATE INDEX IF NOT EXISTS idx_item_drops_pending ON item_drops(sub, state);

  CREATE TABLE IF NOT EXISTS item_deliveries (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    sub          TEXT NOT NULL,
    shard        TEXT NOT NULL,
    serial       INTEGER,                       -- escrowed item to release, or NULL to mint
    type_name    TEXT NOT NULL DEFAULT '',      -- mint only: ModernUO type
    tier         TEXT NOT NULL DEFAULT '',      -- mint only: loot envelope, '' = plain
    -- 🚨 The idempotency key of the ORIGINAL attempt, carried so a retry cannot mint twice.
    -- A mint that succeeded but whose response was lost is indistinguishable from one that
    -- never happened; the shard settles that by remembering the ref. Generating a fresh ref
    -- for the retry — which is what this did before — told the shard it was a NEW request and
    -- it dutifully minted a SECOND item for one payment.
    ref          TEXT NOT NULL DEFAULT '',
    reason       TEXT NOT NULL,                 -- buy|cancel|shop|admin
    created_at   INTEGER NOT NULL,
    delivered_at INTEGER
  );
  CREATE INDEX IF NOT EXISTS idx_item_deliveries ON item_deliveries(sub, delivered_at);

  -- 🚨 THE ONE THING THE GAME PROCESS KNOWS AND THE WEB PROCESS CANNOT: a player is now in
  -- the world. Handing an item over needs the character actually present (the shard answers
  -- 409 otherwise), and only the WebSocket handler sees the login — but the hand-over itself
  -- is a call to the minigames shard, which is the WEB proxy's job and nobody else's
  -- (operator 2026-07-31: "que no vaya al proxy normal ... si se satura la otra parte web, no
  -- afecta al resto de jugadores que solo quieren jugar realmente").
  --
  -- So the game process writes one row here and stops. That is a local synchronous SQLite
  -- insert measured in microseconds, against the ~2.5 s of bridge calls it used to make from
  -- inside the loop that relays live gameplay for EVERY player.
  --
  -- Deliberately a shared TABLE rather than an HTTP call between the two processes: they
  -- already share this database, so this adds no credential, no internal endpoint, and no
  -- new externally-reachable route to get wrong. The at column is the login instant, not a
  -- due time -- the watcher owns the delay, so tuning it never needs the game process
  -- rebuilt. (No backticks in this comment: the schema is a template literal.)
  CREATE TABLE IF NOT EXISTS login_signals (
    sub TEXT PRIMARY KEY,
    at  INTEGER NOT NULL
  );

  -- The killer name the GAME process observed in the shard's own packets, waiting for the WEB
  -- process to publish it. Same one-row handoff as login_signals above and for the same reason:
  -- the observation can only happen where the bytes flow, the publishing belongs to the web side.
  --
  -- Why it exists at all: the public "Recent falls" widget used to print the killer name that
  -- arrived in the body of the metrics report -- free text from the browser, on a page anybody can
  -- read. See deathWatch.ts for why a character filter is not the fix (an advert reads fine in
  -- letters and spaces) and why constraining the VOCABULARY to names the shard sent is.
  --
  -- One row per player, overwritten: only the most recent unpublished death matters, and a player
  -- who dies twice before reporting has nothing useful in the older row. Consumed by the reader,
  -- so a stale name can never be attached to a later death.
  CREATE TABLE IF NOT EXISTS death_witness (
    sub    TEXT PRIMARY KEY,
    killer TEXT,
    at     INTEGER NOT NULL
  );

  -- The live-session map, published BY the game process so the web one can read it. Same
  -- reasoning as login_signals above, one direction further: proxyStats holds those sessions
  -- in memory and only the WebSocket handler fills it, which stranded five portal/admin routes
  -- on the gameplay process for no reason but that. See liveSessionsMirror.ts.
  --
  -- REPLACED wholesale each tick inside one transaction, so a concurrent reader in the other
  -- process never sees it half-emptied and concludes the shard is deserted. Freshness is
  -- stamped separately (runtime_config 'live-sessions-at') and REPORTED, never assumed: a
  -- frozen mirror must make a reader say "cannot tell", not "nobody is online".
  CREATE TABLE IF NOT EXISTS live_sessions (
    id       INTEGER PRIMARY KEY,
    sub      TEXT NOT NULL,
    slug     TEXT NOT NULL,
    is_guest INTEGER NOT NULL DEFAULT 0,
    since    INTEGER NOT NULL,
    minigame TEXT NOT NULL DEFAULT ''
  );
  CREATE INDEX IF NOT EXISTS idx_live_sessions_mg ON live_sessions(minigame);

  -- Kicking is an ACTION on a socket, and a snapshot cannot close one. The web process files
  -- the request here; the game process, which owns the sockets, executes and clears it. The
  -- row is deleted BEFORE the attempt for the same reason a login signal is: a request that
  -- can never succeed (the session already closed) must not be retried for ever.
  CREATE TABLE IF NOT EXISTS session_kicks (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL,
    at         INTEGER NOT NULL
  );

  -- ── Activity feed + notifications (Steam-community) ───────────────────────
  -- activity = PUBLIC events (achievement unlock, level-up, set complete, sale)
  -- by an actor; drives the followed-players feed + a profile's recent activity.
  -- notifications = PERSONAL (comment on your wall, your card sold, new follower).
  CREATE TABLE IF NOT EXISTS activity (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    sub        TEXT NOT NULL,   -- actor
    kind       TEXT NOT NULL,   -- achievement|level|cardset|sale
    detail     TEXT NOT NULL,
    created_at INTEGER NOT NULL
  );
  CREATE INDEX IF NOT EXISTS idx_activity_sub ON activity(sub, id DESC);
  CREATE INDEX IF NOT EXISTS idx_activity_id ON activity(id DESC);
  CREATE TABLE IF NOT EXISTS notifications (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    sub        TEXT NOT NULL,   -- recipient
    kind       TEXT NOT NULL,   -- comment|sale|follow|achievement
    detail     TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    read       INTEGER NOT NULL DEFAULT 0
  );
  CREATE INDEX IF NOT EXISTS idx_notif_sub ON notifications(sub, id DESC);

  -- ── Daily/weekly quests (Steam-community) ─────────────────────────────────
  -- quest_progress accumulates a metric per (sub, period_key, metric) where
  -- period_key is 'd:<YYYY-MM-DD>' or 'w:<7-day-bucket>'; a new period = fresh
  -- progress (implicit reset). quest_claims records a claimed reward per period.
  CREATE TABLE IF NOT EXISTS quest_progress (
    sub        TEXT NOT NULL,
    period_key TEXT NOT NULL,
    metric     TEXT NOT NULL,
    value      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (sub, period_key, metric)
  );
  CREATE TABLE IF NOT EXISTS quest_claims (
    sub        TEXT NOT NULL,
    quest_id   TEXT NOT NULL,
    period_key TEXT NOT NULL,
    claimed_at INTEGER NOT NULL,
    PRIMARY KEY (sub, quest_id, period_key)
  );

  -- ── Leaderboard seasons (Steam-community) ─────────────────────────────────
  -- season_points accrues "earned-from-playing" points per (sub, season=YYYY-MM).
  -- When a month closes, the top players are finalised into season_winners and
  -- paid a bonus (once). The all-time leaderboard is unaffected.
  CREATE TABLE IF NOT EXISTS season_points (
    sub    TEXT NOT NULL,
    season TEXT NOT NULL,
    points INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (sub, season)
  );
  CREATE INDEX IF NOT EXISTS idx_season_points ON season_points(season, points DESC);
  CREATE TABLE IF NOT EXISTS season_winners (
    season   TEXT NOT NULL,
    rank     INTEGER NOT NULL,
    nickname TEXT NOT NULL,
    points   INTEGER NOT NULL,
    PRIMARY KEY (season, rank)
  );
`);

// ── Per-server migration (operator 2026-06-15) ──────────────────────────────
// Rebuild player_metrics + achievements so `slug` is part of the PK. Idempotent
// (skips once the column exists), transactional (rolls back on any error), and
// loss-free (pre-existing rows are carried over as slug='_legacy'). On a FRESH
// DB the CREATE statements above already include slug, so this no-ops.
function _colExists(table: string, col: string): boolean {
  try { return (db.prepare(`PRAGMA table_info(${table})`).all() as Array<{ name: string }>).some((c) => c.name === col); }
  catch { return false; }
}
function _migrateAddSlugPk(table: string, newCols: string, copyCols: string): void {
  if (_colExists(table, 'slug')) return;
  console.log(`[db] migrating ${table} → per-server (slug in PK)…`);
  db.exec('BEGIN IMMEDIATE');
  try {
    db.exec(`CREATE TABLE ${table}__v2 (${newCols});`);
    db.exec(`INSERT OR IGNORE INTO ${table}__v2 (${copyCols}, slug) SELECT ${copyCols}, '_legacy' FROM ${table};`);
    db.exec(`DROP TABLE ${table};`);
    db.exec(`ALTER TABLE ${table}__v2 RENAME TO ${table};`);
    db.exec('COMMIT');
    console.log(`[db] ${table} migrated to per-server OK`);
  } catch (e) {
    try { db.exec('ROLLBACK'); } catch { /* */ }
    console.error(`[db] per-server migration of ${table} FAILED — left intact:`, e);
    throw e;
  }
}
_migrateAddSlugPk('player_metrics',
  "sub TEXT NOT NULL, counter TEXT NOT NULL, slug TEXT NOT NULL DEFAULT '_legacy', value INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (sub, counter, slug)",
  'sub, counter, value');
_migrateAddSlugPk('achievements',
  "sub TEXT NOT NULL, ach_id TEXT NOT NULL, slug TEXT NOT NULL DEFAULT '_legacy', unlocked_at INTEGER NOT NULL, manual INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (sub, ach_id, slug)",
  'sub, ach_id, unlocked_at, manual');

// ── Slug rename migration (operator 2026-07-20: "renombra el slug eternal por
// slug modernuo") ─────────────────────────────────────────────────────────────
// The picker slug is data-keyed all over the DB; renaming without migrating
// would orphan every player's achievements/hours/votes/pins on that shard.
// Idempotent: a second boot finds no 'eternal' rows and no-ops. UPDATE OR
// IGNORE keeps a (theoretical) pre-existing target row instead of clobbering;
// leftovers under the old slug are preserved, never deleted.
function _migrateSlugRename(oldSlug: string, newSlug: string): void {
  const tables = ['achievements', 'player_metrics', 'presence_samples', 'server_pins', 'votes', 'vote_history'];
  let touched = 0;
  for (const t of tables) {
    if (!_colExists(t, 'slug')) continue;
    try {
      const n = Number(db.prepare(`UPDATE OR IGNORE ${t} SET slug = ? WHERE slug = ?`).run(newSlug, oldSlug).changes);
      touched += n;
      if (n) console.log(`[db] slug-rename ${oldSlug}→${newSlug}: ${t} ${n} row(s)`);
    } catch (e) { console.error(`[db] slug-rename ${t} failed (left intact):`, e); }
  }
  // runtime_config per-shard keys (script policy block-list)
  try {
    const oldKey = `script-verbs-block:${oldSlug}`;
    const newKey = `script-verbs-block:${newSlug}`;
    const has = db.prepare('SELECT 1 FROM runtime_config WHERE key = ?');
    if (has.get(oldKey) && !has.get(newKey)) {
      db.prepare('UPDATE runtime_config SET key = ? WHERE key = ?').run(newKey, oldKey);
      console.log(`[db] slug-rename: runtime_config ${oldKey} → ${newKey}`);
      touched++;
    }
  } catch (e) { console.error('[db] slug-rename runtime_config failed:', e); }
  if (touched) console.log(`[db] slug-rename ${oldSlug}→${newSlug} complete (${touched} change(s))`);
}
_migrateSlugRename('eternal', 'modernuo');

// cosmetics_overlay.deleted (permanent-delete tombstone) — added 2026-06-24 after the
// table shipped without it; a guarded ALTER backfills existing DBs (fresh DBs already
// have the column from the CREATE above).
if (!_colExists('cosmetics_overlay', 'deleted')) {
  try { db.exec('ALTER TABLE cosmetics_overlay ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0'); }
  catch (e) { console.error('[db] add cosmetics_overlay.deleted failed:', e); }
}

// Boot-time bucket cleanup — NO new table, idempotent (nothing matches once empty):
//   '_legacy'  — pre-per-server rows the migration carried over. Operator
//                2026-06-16 ("borra los logros del before tracking") chose a clean
//                slate: everyone re-earns per server. Pre-economy achievements had
//                no points tied to them, so balances are unaffected.
//   '_unknown' — garbage rows from malformed slugs (aslug/safeSlug fallback). Junk.
// '_manual' (real admin-awarded achievements) is intentionally kept.
try {
  let a = 0, m = 0;
  for (const slug of ['_legacy', '_unknown']) {
    a += Number(db.prepare('DELETE FROM achievements WHERE slug = ?').run(slug).changes) || 0;
    m += Number(db.prepare('DELETE FROM player_metrics WHERE slug = ?').run(slug).changes) || 0;
  }
  if (a || m) console.log(`[db] bucket cleanup — removed ${a} achievement + ${m} metric junk/legacy rows`);
} catch (e) { console.error('[db] bucket cleanup failed (non-fatal):', e); }

// Add nicknames.privacy to pre-existing DBs (operator 2026-06-16). A constant
// DEFAULT makes the ALTER safe + instant; fresh DBs already have it from CREATE.
if (!_colExists('nicknames', 'privacy')) {
  try { db.exec("ALTER TABLE nicknames ADD COLUMN privacy TEXT NOT NULL DEFAULT 'public'"); console.log('[db] nicknames.privacy added'); }
  catch (e) { console.error('[db] could not add nicknames.privacy:', (e as Error).message); }
}
// Retire the 'followers' privacy tier (operator 2026-06-22: "a follow is a
// notification subscription, not a privacy level"). Privacy is now binary
// public/private; any legacy 'followers' row meant hidden-from-all (it was always
// treated as restricted), so migrate it to 'private' to preserve that visibility.
try { const c = Number(db.prepare("UPDATE nicknames SET privacy = 'private' WHERE privacy = 'followers'").run().changes) || 0;
  if (c) console.log(`[db] migrated ${c} legacy 'followers' privacy row(s) -> 'private'`); }
catch (e) { console.error("[db] could not migrate 'followers' privacy:", (e as Error).message); }
// Add nicknames.featured_ach (operator 2026-06-23): the one achievement the player
// pins to highlight on their public profile. NULL = none (the profile falls back to
// the rarest unlocked). Validated against the player's held achievements on write.
// 🚨 session_kicks.epoch — a kick names a session id, and session ids are an IN-MEMORY counter
// that restarts at 1 with the game process (proxyStats: `let _nextSessionId = 1`). The kick queue
// is durable SQLite. So a request written just before a restart, and drained just after, would
// close whichever NEW session happened to inherit that number: an admin action landing on an
// unrelated player. The freshness gate does not help, because it guards INSERTION, not execution.
//
// The epoch names the process lifetime the id belongs to. A restart mints a new one and every
// queued kick from the old namespace becomes inexpressible rather than wrong. NULL on legacy rows,
// which are dropped without executing — an unexecuted kick is enormously better than a misdirected
// one, so this fails closed.
if (!_colExists('session_kicks', 'epoch')) {
  try { db.exec("ALTER TABLE session_kicks ADD COLUMN epoch TEXT"); console.log('[db] session_kicks.epoch added'); }
  catch (e) { console.error('[db] could not add session_kicks.epoch:', (e as Error).message); }
}

if (!_colExists('nicknames', 'featured_ach')) {
  try { db.exec("ALTER TABLE nicknames ADD COLUMN featured_ach TEXT"); console.log('[db] nicknames.featured_ach added'); }
  catch (e) { console.error('[db] could not add nicknames.featured_ach:', (e as Error).message); }
}

// Add nicknames.paperdoll_body (operator 2026-07-16: "el paperdoll debería ser hombre o
// mujer a elección del jugador"). 'm' | 'f' — the deathshroud bust shown on the profile
// banner. NULL/absent = 'm' (what every profile showed before the choice existed, so no
// migration surprise). Purely cosmetic; validated on write.
if (!_colExists('nicknames', 'paperdoll_body')) {
  try { db.exec("ALTER TABLE nicknames ADD COLUMN paperdoll_body TEXT"); console.log('[db] nicknames.paperdoll_body added'); }
  catch (e) { console.error('[db] could not add nicknames.paperdoll_body:', (e as Error).message); }
}

// item_deliveries.ref for DBs created before 2026-07-31. Without it a queued mint carried no
// idempotency key of its own, so the retry told the shard it was a brand-new request and a
// grant whose response had merely been LOST was performed a second time — one payment, two
// items. Escrow releases never needed it (release is by serial and checks vault membership,
// so it is idempotent by construction); only the mint path could duplicate.
if (!_colExists('item_deliveries', 'ref')) {
  try { db.exec("ALTER TABLE item_deliveries ADD COLUMN ref TEXT NOT NULL DEFAULT ''"); console.log('[db] item_deliveries.ref added'); }
  catch (e) { console.error('[db] could not add item_deliveries.ref:', (e as Error).message); }
}

// Add card_listings.foil/aura to pre-existing DBs (v0.9.87, operator 2026-06-19): a listing
// now sells one SPECIFIC instance (card_id+foil+aura), escrowed and handed over as that state.
if (!_colExists('card_listings', 'foil')) {
  try { db.exec("ALTER TABLE card_listings ADD COLUMN foil INTEGER NOT NULL DEFAULT 0"); console.log('[db] card_listings.foil added'); }
  catch (e) { console.error('[db] could not add card_listings.foil:', (e as Error).message); }
}
if (!_colExists('card_listings', 'aura')) {
  try { db.exec("ALTER TABLE card_listings ADD COLUMN aura TEXT NOT NULL DEFAULT ''"); console.log('[db] card_listings.aura added'); }
  catch (e) { console.error('[db] could not add card_listings.aura:', (e as Error).message); }
}
// Add card_listings.escrow_token (audit 2026-06-23): links a listing to its cards.db
// card_escrow journal row so the market reconciler can repair a cross-DB tear. Legacy
// listings keep NULL and fall back to the pre-journal grant/take path in market.ts.
if (!_colExists('card_listings', 'escrow_token')) {
  try { db.exec("ALTER TABLE card_listings ADD COLUMN escrow_token TEXT"); console.log('[db] card_listings.escrow_token added'); }
  catch (e) { console.error('[db] could not add card_listings.escrow_token:', (e as Error).message); }
}

/**
 * One-time JSON → table import. If `file` exists, parse it, run `importer` inside a
 * transaction, then rename it to `<file>.migrated` so it's never re-imported. Any
 * failure is logged and swallowed (the store falls back to an empty table rather
 * than crashing the proxy). Returns true if a migration ran.
 */
export function migrateJsonOnce(file: string, importer: (data: unknown) => void): boolean {
  let raw: unknown;
  try {
    if (!fs.existsSync(file)) return false;
    raw = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (e) {
    console.error(`[db] could not read ${path.basename(file)} for migration: ${(e as Error).message}`);
    return false;
  }
  try {
    db.exec('BEGIN IMMEDIATE');
    importer(raw);
    db.exec('COMMIT');
  } catch (e) {
    try { db.exec('ROLLBACK'); } catch { /* ignore */ }
    console.error(`[db] migration of ${path.basename(file)} failed: ${(e as Error).message}`);
    return false;
  }
  try { fs.renameSync(file, `${file}.migrated`); } catch { /* keep original; table already has the data */ }
  console.log(`[db] migrated ${path.basename(file)} → SQLite`);
  return true;
}

// ── Daily compressed DB backup (operator 2026-06-11) ─────────────────────────
// A corrupt uonexus.db would lose votes/metrics/achievements/identities, so we
// snapshot it daily. `VACUUM INTO` produces a CONSISTENT single-file copy of
// the live WAL database (unlike tarring the .db/-wal/-shm mid-write), which we
// then gzip. Kept 14 days; older snapshots pruned. Runs in-process (one writer,
// no external cron) ~30s after boot and every 24h after. Skipped under :memory:
// (tests). The docker data-backup sidecar tars the whole /data dir too — this
// is the DB-specific, integrity-safe layer.
const BACKUP_DIR = path.join(DATA_PATH, 'db-backups');
const BACKUP_KEEP_DAYS = 14;
// Integrity-safe daily snapshot of a WAL SQLite DB: VACUUM INTO a CONSISTENT single-file
// copy (NOT a mid-write tar of .db/-wal/-shm), gzip it, keep BACKUP_KEEP_DAYS. Generic over
// the connection + base name so BOTH uonexus.db (scheduled below) and cards.db (scheduled
// from cardsdb.ts) get this layer (operator 2026-06-18 audit: cards.db previously had no
// integrity-safe backup — only the docker sidecar's whole-/data tar, which is NOT WAL-
// consistent and so is not a safe standalone restore source for a live DB).
export function backupSqliteDb(conn: DatabaseSync, baseName: string): void {
  try {
    fs.mkdirSync(BACKUP_DIR, { recursive: true });
    const stamp = new Date().toISOString().slice(0, 10); // YYYY-MM-DD (one per day)
    const snap = path.join(BACKUP_DIR, `${baseName}-${stamp}.db`);
    const gz = `${snap}.gz`;
    if (fs.existsSync(gz)) { pruneBackups(baseName); return; } // already done today
    // Consistent snapshot of the live DB. SQLite string-escapes the path.
    conn.exec(`VACUUM INTO '${snap.replace(/'/g, "''")}'`);
    const raw = fs.readFileSync(snap);
    fs.writeFileSync(gz, zlib.gzipSync(raw, { level: 9 }));
    fs.rmSync(snap, { force: true });
    console.log(`[db] backup written ${path.basename(gz)} (${(fs.statSync(gz).size / 1024).toFixed(0)} KB)`);
    pruneBackups(baseName);
  } catch (e) {
    console.error(`[db] backup failed for ${baseName}: ${(e as Error).message}`);
  }
}
function pruneBackups(baseName: string): void {
  try {
    const cutoff = Date.now() - BACKUP_KEEP_DAYS * 24 * 60 * 60 * 1000;
    const re = new RegExp('^' + baseName + '-\\d{4}-\\d{2}-\\d{2}\\.db\\.gz$'); // baseName is a fixed literal, not user input
    for (const f of fs.readdirSync(BACKUP_DIR)) {
      if (!re.test(f)) continue;
      const fp = path.join(BACKUP_DIR, f);
      try { if (fs.statSync(fp).mtimeMs < cutoff) fs.rmSync(fp, { force: true }); } catch { /* skip */ }
    }
  } catch { /* dir gone — nothing to prune */ }
}
// OWNS_JOBS: since the web/game split there are TWO processes on this file, and an
// UNGATED snapshot means both run `VACUUM INTO` on the same source into the SAME target
// path, staggered by nothing that guarantees they miss each other. Two writers producing
// one file is not a slow backup, it is a CORRUPT one -- and a corrupt snapshot is worse
// than no snapshot, because it is discovered on the day it is needed.
if (!IS_TEST && OWNS_JOBS) {
  setTimeout(() => backupSqliteDb(db, 'uonexus'), 30_000).unref();
  setInterval(() => backupSqliteDb(db, 'uonexus'), 24 * 60 * 60 * 1000).unref();
} else if (!IS_TEST) {
  // Logged rather than returned silently: the boot line is what exposed the duplicated
  // timers in the first place, so removing the signal would hide the next one.
  console.log('[db] snapshot timer skipped (this process does not own scheduled jobs)');
}

export default db;
