import fs from 'node:fs';
import path from 'node:path';

/**
 * Write a file to a temp name and rename it over the target.
 *
 * A rename within a filesystem is atomic: readers see the old file or the new one, never a prefix
 * of the new one. A plain `writeFileSync` opens the target with O_TRUNC, so a crash, an OOM kill,
 * a full disk or a container recreate mid-write leaves a file that parses as nothing.
 *
 * 🚨 THIS LIVES IN ITS OWN MODULE ON PURPOSE. Several call sites had already grown their own copy
 * of the tmp+rename dance (auth, banRegistry, deletionQueue, idRegistry, logoStore, gamefileUpload)
 * and the card catalog was still written in place — because a convention that every new writer has
 * to remember is the one the next writer forgets. New code should import this rather than re-derive
 * it; the existing copies are correct and are left alone.
 *
 * Whether an in-place write actually hurts depends on what the READER does, which is why the
 * remaining direct writers were triaged instead of swept:
 *
 *   cardUpload      overwrites live, site-wide state whose loader CHOOSES the torn file. Fixed.
 *   auth settings   reader is `catch { return {} }` — which READS like a graceful degrade and is
 *                   not: the client GETs settings and PUTs the whole object back, so a torn file
 *                   makes the next boot see {} and save {} over it. LARGE_KEYS are `railScripts`
 *                   and `railAgents` — the user's saved scripts and agents, gone silently and
 *                   unrecoverably. Fixed too; the "degrade" was the trap.
 *   gamefileUpload  CREATES the manifest under a `crypto.randomBytes(16)` id it just minted, and
 *                   `loadManifest` returns null on a bad parse, so the client re-registers.
 *                   Genuinely nothing to destroy — left in place, verified at both sites.
 *
 * The first one is the reason this exists: `catalogPath()` picks the volume copy IF IT EXISTS and
 * falls back to the bundled catalog otherwise. A half-written file exists — so it is chosen over
 * the good bundled copy, fails JSON.parse, is swallowed by the loader's catch, and leaves the
 * catalog EMPTY site-wide on the next boot, with one console line to explain it.
 */
function tempName(finalPath: string): string {
  return `${finalPath}.${process.pid}.${Date.now()}.${Math.random().toString(36).slice(2, 8)}.tmp`;
}

export function writeFileAtomic(finalPath: string, text: string): void {
  fs.mkdirSync(path.dirname(finalPath), { recursive: true });
  const tmpPath = tempName(finalPath);
  try {
    fs.writeFileSync(tmpPath, text, 'utf8');
    fs.renameSync(tmpPath, finalPath);
  } catch (e) {
    // Never leave the temp behind on a failed write: these directories are listed elsewhere.
    try { fs.unlinkSync(tmpPath); } catch { /* already gone */ }
    throw e;
  }
}

/** Same contract, for callers already on the promises API. */
export async function writeFileAtomicAsync(finalPath: string, text: string): Promise<void> {
  await fs.promises.mkdir(path.dirname(finalPath), { recursive: true });
  const tmpPath = tempName(finalPath);
  try {
    await fs.promises.writeFile(tmpPath, text, 'utf8');
    await fs.promises.rename(tmpPath, finalPath);
  } catch (e) {
    try { await fs.promises.unlink(tmpPath); } catch { /* already gone */ }
    throw e;
  }
}
