// Immutable integer ID registry for server entries.
//
// IDs are assigned once at creation time and never reused — even after a
// server is deleted. Tombstoning prevents a newly-created server from
// inheriting stale gamefiles that a previous server left in server-<id>/.
//
// Storage: DATA_SERVERS_DIR + '/.id-registry.json'
// Format : { "nextId": <n>, "tombstoned": [<id>, ...] }

import * as fs from 'fs';
import * as path from 'path';
import { DATA_SERVERS_DIR } from './config.js';

const FILENAME = '.id-registry.json';

interface RegistryData {
  nextId: number;
  tombstoned: number[];
}

function filePath(): string {
  return path.join(DATA_SERVERS_DIR, FILENAME);
}

function load(): RegistryData {
  let raw: string;
  try {
    raw = fs.readFileSync(filePath(), 'utf8');
  } catch (e: unknown) {
    const err = e as NodeJS.ErrnoException;
    // A genuinely-absent file is first-run — start fresh.
    if (err.code === 'ENOENT') return { nextId: 1, tombstoned: [] };
    throw e;
  }
  // audit L-4: FAIL-CLOSED on a present-but-corrupt file. Pre-fix this
  // silently reset to { nextId: 1 }, which re-issues already-allocated
  // IDs — newly-created shards then inherit stale gamefiles from
  // server-<id>/ left by a previous shard. Refuse to start instead.
  let obj: unknown;
  try {
    obj = JSON.parse(raw) as unknown;
  } catch (e) {
    throw new Error(
      `[IdRegistry] ${filePath()} is present but contains invalid JSON ` +
      `(${(e as Error).message}). Refusing to reset — that would re-issue ` +
      `already-allocated IDs and cause server-<id>/ gamefiles collisions. ` +
      `Fix or remove the file by hand.`,
    );
  }
  if (
    obj !== null &&
    typeof obj === 'object' &&
    typeof (obj as Record<string, unknown>).nextId === 'number' &&
    Array.isArray((obj as Record<string, unknown>).tombstoned)
  ) {
    return obj as RegistryData;
  }
  throw new Error(
    `[IdRegistry] ${filePath()} is present but malformed (expected ` +
    `{ nextId: number, tombstoned: number[] }). Refusing to reset — that ` +
    `would re-issue already-allocated IDs and cause server-<id>/ gamefiles ` +
    `collisions. Fix or remove the file by hand.`,
  );
}

// v0.3.13 audit R2-M-2: atomic write via temp + rename. Pre-fix,
// `writeFileSync` could leave a torn file on crash mid-write (registry
// refuses to load on next boot — `Duplicate server ID` thrown by
// `loadRegistry`). The tmp filename includes pid + timestamp so
// concurrent persists from the same process don't collide. Single-
// process Node benefits because rename is atomic on POSIX (within a
// filesystem) — the file is either fully old or fully new at any
// observable moment, never partial.
//
// audit M-1 — scope correction: tmp+rename makes a SINGLE write atomic,
// but the load→modify→persist cycle (assignId/tombstoneId/seed) is NOT
// a cross-process transaction. It is safe ONLY because the deploy is
// single-replica and every admin mutation runs under the in-process
// `withAdminLock` mutex. A multi-replica deploy WOULD race the
// load→modify→persist cycle and mint duplicate IDs — that would need a
// cross-process lock, which is NOT implemented here. Single-replica only.
function persist(data: RegistryData): void {
  const finalPath = filePath();
  fs.mkdirSync(path.dirname(finalPath), { recursive: true });
  const tmpPath = `${finalPath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tmpPath, JSON.stringify(data, null, 2) + '\n', 'utf8');
  fs.renameSync(tmpPath, finalPath);
}

/** Allocate the next available ID and persist the updated counter. */
export function assignId(): number {
  const data = load();
  const id = data.nextId;
  data.nextId = id + 1;
  persist(data);
  return id;
}

/** Mark an ID as permanently retired — it will never be reissued. */
export function tombstoneId(id: number): void {
  const data = load();
  if (!data.tombstoned.includes(id)) {
    data.tombstoned.push(id);
    persist(data);
  }
}

/** Returns current registry snapshot for diagnostics/admin API. */
export function inspect(): RegistryData {
  return load();
}

/** Initialise the registry with a pre-seeded nextId (migration helper). */
export function seed(nextId: number): void {
  const existing = load();
  if (existing.nextId < nextId) {
    existing.nextId = nextId;
    persist(existing);
  }
}
