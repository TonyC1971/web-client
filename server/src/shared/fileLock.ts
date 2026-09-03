/**
 * A synchronous, cross-process advisory lock over a file path.
 *
 * 🚨 WHY THIS EXISTS AS A MODULE RATHER THAN A COPY. Two JSON stores in this codebase do a
 * read-modify-write of a whole document and are written by BOTH proxy processes since the
 * web/game split: the runtime config (admin routes in `web`, slug revocation from the cleanup
 * job in `game`) and the shard deletion queue (its three mutators are each called from a web
 * route AND from a scheduled job). The write itself is atomic in both — tmp + rename, so no
 * torn file — but the CYCLE around it is not, and `withAdminLock` is an instance field that
 * serialises each process only against itself.
 *
 * Writing the same protocol twice is how the two copies drift, which this repo has paid for
 * before with the per-shard gamefiles predicate that turned out to exist three times. One
 * implementation, two callers.
 *
 * The protocol is the one `withOverrideLock` already uses, in synchronous form because every
 * caller here is synchronous and making them async would ripple through express handlers:
 *   - an O_EXCL lockfile carrying an owner TOKEN, because a path is not an identity;
 *   - a lock older than STALE_MS is stolen, so a crashed holder cannot wedge admin writes;
 *   - release removes the file only if it still holds our token — a lock stolen from us now
 *     belongs to somebody else, and removing it would let a third writer in mid-write;
 *   - budget exhausted, or a directory we cannot write to, means running UNLOCKED rather than
 *     refusing. An admin action must not fail over lock bookkeeping, and both callers also
 *     re-read their document inside the lock, which is what actually closes their race.
 */
import * as fs from 'fs';

const STALE_MS = 5_000, BUDGET_MS = 500, STEP_MS = 5;

/** Sleep without spinning. These writes are rare (admin actions, hourly jobs), so a few ms of
 *  blocked event loop in the worst case is cheap against losing a revocation or a queue entry. */
function sleepSync(ms: number): void {
  try { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); } catch { /* no SAB */ }
}

function acquire(lockPath: string): string | null {
  const token = `${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  for (let waited = 0; ; waited += STEP_MS) {
    try {
      fs.writeFileSync(lockPath, token, { flag: 'wx' });
      return token;
    } catch (e) {
      const code = (e as NodeJS.ErrnoException).code;
      if (code !== 'EEXIST') return null;              // unwritable dir → run unlocked
      try {
        const st = fs.statSync(lockPath);
        if (Date.now() - st.mtimeMs > STALE_MS) { fs.rmSync(lockPath, { force: true }); continue; }
      } catch { continue; }                             // vanished between open and stat → retry
      if (waited >= BUDGET_MS) return null;             // budget spent → run unlocked
      sleepSync(STEP_MS);
    }
  }
}

function release(lockPath: string, token: string): void {
  try { if (fs.readFileSync(lockPath, 'utf8') === token) fs.rmSync(lockPath, { force: true }); }
  catch { /* already gone, or unreadable — leave it for the staleness sweep */ }
}

/**
 * Run `fn` holding an advisory lock on `<target>.lock`. Best-effort: if the lock cannot be taken
 * within the budget, `fn` still runs. Callers must therefore re-read their document INSIDE `fn`
 * rather than treating the lock as the whole guarantee.
 */
export function withFileLockSync<T>(target: string, fn: () => T): T {
  const lockPath = `${target}.lock`;
  const token = acquire(lockPath);
  try { return fn(); }
  finally { if (token) release(lockPath, token); }
}
