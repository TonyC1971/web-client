// Soft-delete queue for server entries.
//
// When an admin deletes a server, the YAML is removed from servers/ and
// a DeletionEntry is written here. After 7 days the entry is auto-expired
// by the hourly cleanup job in AssetServer.ts. Before that window an admin
// can cancel (restores the YAML) or force-delete (immediate, wipes gamefiles).
//
// Storage: DATA_SERVERS_DIR + '/.deletion-queue.json'
// Format : [ { id, slug, scheduledAt, record }, ... ]

import * as fs from 'fs';
import * as path from 'path';
import { withFileLockSync } from './fileLock.js';
import { DATA_SERVERS_DIR } from './config.js';
import type { ServerRecord } from './serverRegistry.js';

const FILENAME = '.deletion-queue.json';
export const GRACE_MS = 7 * 24 * 60 * 60 * 1000; // 7 days

export interface DeletionEntry {
  id: number;
  slug: string;
  scheduledAt: number;   // Unix ms timestamp when deletion was requested
  record: ServerRecord;  // full record snapshot for cancel-deletion restore
}

function filePath(): string {
  return path.join(DATA_SERVERS_DIR, FILENAME);
}

function load(): DeletionEntry[] {
  try {
    const raw = fs.readFileSync(filePath(), 'utf8');
    const arr = JSON.parse(raw) as unknown;
    return Array.isArray(arr) ? (arr as DeletionEntry[]) : [];
  } catch (e: unknown) {
    const err = e as NodeJS.ErrnoException;
    if (err.code === 'ENOENT') return [];
    throw e;
  }
}

// v0.3.13 audit R3-L-2: atomic write via tmp + rename. Round 2 closed
// the same bug for `idRegistry.persist` (R2-M-2) and missed this twin —
// a SIGKILL or ENOSPC mid-write produces torn JSON, then `JSON.parse`
// throws on the next `getDeletionQueue()` call, propagating into the
// admin handlers and the cleanup interval. POSIX rename is atomic
// within a filesystem so observers see either fully-old or fully-new.
//
// audit M-1 — scope correction: tmp+rename makes a SINGLE write atomic,
// but the load→modify→persist cycle (enqueue/cancelDeletion/removeEntry)
// is NOT a cross-process transaction.
//
// 🚨 THE OLD ANSWER STOPPED BEING TRUE AT THE WEB/GAME SPLIT. It read: "safe ONLY because the
// deploy is single-replica and every admin mutation runs under the in-process withAdminLock
// mutex". Single-replica still holds. The second half does not: `withAdminLock` is an instance
// field, and this queue is mutated from BOTH processes — enqueue, cancelDeletion and removeEntry
// are each called from a `/api/admin` route (owned by `web`) AND from the expiry cleanup or owner
// reaper (scheduled jobs, owned by `game`). Each mutation rewrites the WHOLE array from a copy it
// just loaded, so two processes removing different entries undo one another: an entry that should
// be gone comes back, or a shard queued for deletion loses its place.
//
// Two things close it, and it is worth being clear which does the work. The boot runs of both
// jobs are now ownership-gated (`jobAtBoot`), which removed the one window that was not a
// coincidence — a deploy recreates both proxies in one command, so they used to run these
// simultaneously every time. What remains is a cron in one process against a route in the other,
// and for that `mutateQueue` below holds a cross-process lockfile across the WHOLE cycle.
//
// 🚨 THE LOCK HAS TO WRAP THE CYCLE, NOT THE WRITE. Wrapping only `persist` looks like a fix and
// is not: the load happens before it, so another process can still load the same array in between
// and rewrite ours. The rename was already atomic — locking it adds nothing. The thing that needs
// to be indivisible is load → modify → write, which is why the three mutators go through one
// helper instead of each calling load() and persist() in sequence.
function persistLocked(queue: DeletionEntry[]): void {
  const finalPath = filePath();
  fs.mkdirSync(path.dirname(finalPath), { recursive: true });
  const tmpPath = `${finalPath}.${process.pid}.${Date.now()}.tmp`;
  fs.writeFileSync(tmpPath, JSON.stringify(queue, null, 2) + '\n', 'utf8');
  fs.renameSync(tmpPath, finalPath);
}

/**
 * Read-modify-write the queue as one unit. `fn` receives the queue as it is on disk RIGHT NOW —
 * loaded inside the lock, never a caller's older copy — and says whether it changed anything, so
 * a no-op lookup does not rewrite the file.
 */
function mutateQueue<T>(fn: (queue: DeletionEntry[]) => { value: T; changed: boolean }): T {
  return withFileLockSync(filePath(), () => {
    const queue = load();
    const { value, changed } = fn(queue);
    if (changed) persistLocked(queue);
    return value;
  });
}

/** Add a server to the deletion queue. Idempotent if already enqueued. */
export function enqueue(record: ServerRecord): DeletionEntry {
  return mutateQueue((queue) => {
    const existing = queue.find((e) => e.id === record.id);
    if (existing) return { value: existing, changed: false };
    const entry: DeletionEntry = {
      id: record.id,
      slug: record.slug,
      scheduledAt: Date.now(),
      record,
    };
    queue.push(entry);
    return { value: entry, changed: true };
  });
}

/** Remove a server from the queue (cancel soft-delete). Returns entry if found. */
export function cancelDeletion(id: number): DeletionEntry | null {
  return mutateQueue<DeletionEntry | null>((queue) => {
    const idx = queue.findIndex((e) => e.id === id);
    if (idx === -1) return { value: null, changed: false };
    const [entry] = queue.splice(idx, 1);
    return { value: entry, changed: true };
  });
}

/** Entries whose grace period has expired (scheduledAt + 7d ≤ now). */
export function getExpired(): DeletionEntry[] {
  return load().filter((e) => Date.now() - e.scheduledAt >= GRACE_MS);
}

/** All current pending-deletion entries. */
export function getAll(): DeletionEntry[] {
  return load();
}

/** Remove an entry by server id (after force/auto-delete completes). */
export function removeEntry(id: number): void {
  mutateQueue((queue) => {
    const idx = queue.findIndex((e) => e.id === id);
    if (idx === -1) return { value: undefined, changed: false };
    queue.splice(idx, 1);
    return { value: undefined, changed: true };
  });
}

/** True if the server is currently awaiting deletion. */
export function isPendingDeletion(id: number): boolean {
  return load().some((e) => e.id === id);
}

/** Whole-number days remaining in the grace period (0 when expired). */
export function daysRemaining(entry: DeletionEntry): number {
  const ms = GRACE_MS - (Date.now() - entry.scheduledAt);
  return Math.max(0, Math.ceil(ms / (24 * 60 * 60 * 1000)));
}
