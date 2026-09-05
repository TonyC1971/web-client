// serverFlags.ts — operator runtime toggles, panel-managed, SQLite-backed.
//
// These are the on/off switches an operator used to flip by touching sentinel
// FILES on the NAS (flags/disable-dev, gamefiles/nocompress) + restarting. They
// now live in the `runtime_config` table (db.ts) and are toggled from the admin
// panel (PUT /api/admin/runtime-config). SQLite is authoritative.
//
// Two of them need a bridge to their existing consumer (which can't read this DB):
//   - nocompress: the asset-worker (a separate process, no DB access) checks for a
//     `<GAMEFILES_ROOT>/nocompress` FILE every tick. setFlag mirrors the SQLite
//     value to that file (write when on, remove when off) so the worker picks it up
//     next tick — RUNTIME, no restart. syncFlagBridges() re-asserts it on boot.
//   - disable-dev: served to the CLIENT via GET /api/runtime-config; main.js reads
//     it at boot and self-gates `?dev=*` (in addition to the legacy nginx meta
//     rewrite, which still works — the client disables dev if EITHER says so).

import * as fs from 'fs';
import * as path from 'path';
import { db } from './db.js';
import { GAMEFILES_ROOT } from './config.js';

export interface FlagDef {
  key: string;
  label: string;
  description: string;
  default: boolean;
  /** true = exposed to the client via the public /api/runtime-config endpoint. */
  client?: boolean;
}

// The catalog. Add a switch = one entry here (+ wire its consumer if it needs a
// bridge). Keys match the legacy sentinel filenames for operator familiarity.
export const FLAG_DEFS: FlagDef[] = [
  {
    key: 'disable-dev',
    label: 'Disable dev mode',
    description: 'Silence the ?dev=* debug instrumentation (console traces, packet/class introspection) for all visitors. Recovery via ?nocache=1 is unaffected.',
    default: false,
    client: true,
  },
  {
    key: 'chunk-snapshot-cache',
    label: 'Chunk snapshot cache (ClassicUO)',
    description: 'Cache decoded map chunks in the browser so revisited areas skip the .mul read. It is a LARGE win — an A/B on the same build measured up to 30 fps and visibly fewer stutters with it on — but it is also the cause of the open ghost-statics defect: a chunk captured while part of its contents had already been freed is stored and reused, so walls and other static art go missing from an area until the cache is discarded. OFF by default while that is unfixed. ClassicUO only: TazUO has no such cache, so this does nothing on /tuo/.',
    default: false,
    client: true,
  },
  {
    key: 'nocompress',
    label: 'Pause asset compression',
    description: 'Tell the asset-worker to stop (re)compressing gamefiles to .br on its next poll. Use during a bulk upload; turn back off to resume.',
    default: false,
  },
  {
    key: 'allow-self-service-shards',
    label: 'Allow self-service shards',
    description: 'Let any logged-in Discord user register their OWN shard (auto-live in the picker). They host their own gamefiles at an external https URL with CORS + a manifest.json — we store nothing. Off = only you create shards.',
    default: false,
  },
  {
    key: 'allow-user-gamefile-uploads',
    label: 'Allow owner gamefile uploads to our NAS',
    description: 'Let shard OWNERS upload their gamefiles into our pool (we host them), capped at 1.5 GB per shard. Off = owners must self-host externally; you (general admin) can always upload.',
    default: false,
  },
  {
    key: 'allow-gamefile-overrides',
    label: 'Allow owner gamefile overrides',
    description: 'Let shard OWNERS replace INDIVIDUAL gamefiles for their shard (a per-slug layer over the shared pool — the shared game data and sibling shards are never touched), capped at 512 MB per shard. Off = only you (general admin) can override. Distinct from full fileset uploads above.',
    default: false,
  },
  {
    key: 'cosmetics-shop-enabled',
    label: 'Enable cosmetics shop',
    description: 'Open the profile cosmetics shop to everyone. Off = "Coming soon" — only supreme admins (admins:manage) can browse/buy, for testing, until you launch it.',
    default: false,
  },
  {
    key: 'marketplace-enabled',
    label: 'Enable card marketplace',
    description: 'Open the trading-card marketplace (buy/sell spare cards for points) to everyone. Off = "Coming soon" — only supreme admins (admins:manage) can access it, for testing, until you launch it.',
    default: false,
  },
  {
    key: 'stalls-enabled',
    label: 'Enable Vendor Stalls',
    description: 'Open Vendor Stalls (sell your own gear, and buy from the shard shop) to everyone. Off = "Coming soon" — only supreme admins (admins:manage) can access it, for testing, until you launch it. Turning it OFF never strands anything: withdrawing your own listing keeps working, so gear already in escrow can always be recovered.',
    default: false,
  },
  {
    key: 'lean-gamefiles-manifest',
    label: 'Lean gamefiles manifest',
    description: 'Serve each shard only the gamefiles the web client actually loads. Strips ~29 dead EA classic-client files that NEITHER ClassicUO nor TazUO ever opens (unused .enu help/UI text, intro.bik, langcode.iff, blueprints.tbp, default.mac, desktop.nwb) so the client never downloads them. Off = serve the full manifest verbatim. Music and the legacy/diff/facet map files are ALWAYS kept (different client eras use them).',
    default: true,
  },
];

const DEFS_BY_KEY = new Map(FLAG_DEFS.map((d) => [d.key, d]));

const qGet = db.prepare('SELECT value FROM runtime_config WHERE key = ?');
const qSet = db.prepare(
  'INSERT INTO runtime_config(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value',
);

/** Current value of a flag (its catalog default if never set / unknown key). */
export function getFlag(key: string): boolean {
  const r = qGet.get(key) as { value: string } | undefined;
  if (r) return r.value === '1';
  return DEFS_BY_KEY.get(key)?.default ?? false;
}

/** All flags as { key: bool } (catalog order). */
export function getAllFlags(): Record<string, boolean> {
  const out: Record<string, boolean> = {};
  for (const d of FLAG_DEFS) out[d.key] = getFlag(d.key);
  return out;
}

/** Set a flag (unknown keys ignored) + apply its file bridge. */
export function setFlag(key: string, value: boolean): boolean {
  if (!DEFS_BY_KEY.has(key)) return false;
  qSet.run(key, value ? '1' : '0');
  applyBridge(key, value);
  return true;
}

// Mirror a flag's value to the sentinel file its consumer reads.
function applyBridge(key: string, value: boolean): void {
  if (key === 'nocompress' && GAMEFILES_ROOT) {
    const file = path.join(GAMEFILES_ROOT, 'nocompress');
    try {
      if (value) { fs.writeFileSync(file, ''); }
      else { fs.rmSync(file, { force: true }); }
    } catch (e) {
      console.error(`[flags] nocompress file bridge failed: ${(e as Error).message}`);
    }
  }
}

/** Re-assert every file bridge from SQLite on boot (SQLite is authoritative, so a
 *  manually-created/removed sentinel file is corrected to match the stored state). */
export function syncFlagBridges(): void {
  for (const d of FLAG_DEFS) applyBridge(d.key, getFlag(d.key));
}
