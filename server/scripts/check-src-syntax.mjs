/**
 * Parse every TypeScript file under src/ at IMAGE BUILD time.
 *
 * src/ is transformed by esbuild at CONTAINER START, not at build time, so a syntax error there
 * produces a healthy-looking image that crash-loops on boot while both proxies serve 502. Failing
 * the image build is the right outcome: a proxy that was never built cannot be deployed broken.
 *
 * 🚨 THE WALK IS RECURSIVE, AND THE COUNT HAS A FLOOR. This used to be `readdirSync('src')`, flat.
 * The day the 39 modules shared with uonexus-minimal moved into src/shared/, that scan would have
 * kept reporting success while checking barely half the tree — the failure this file exists to catch,
 * now invisible in exactly the code most likely to break. It survived only by luck of ordering: the
 * build-tag check ran first and failed on its own hardcoded path.
 *
 * 🚨 BUT THE TRIPWIRE WAS A CONSTANT, AND A CONSTANT BELONGS TO ONE PRODUCT. It was `files.length <
 * 60`, tuned to this monorepo. The published uonexus-minimal build legitimately contains 37 modules
 * — the publisher derives them from the entrypoint's import graph — so this check refused it BY
 * CONSTRUCTION: `docker compose up` on a downloaded release died here, saying the tree was shrunken
 * when the tree was exactly right. Nobody could ever have built it.
 *
 * A scanner that sweeps a directory does need a guard, or it reports success over nothing. The guard
 * is now the SHAPE of the walk rather than a census of it: every directory under src/ that holds a
 * .ts file must be represented in the result. That is precisely what "the walk broke" means — a flat
 * readdir finds the top level and silently misses src/shared/ — and it holds for a 37-module build
 * and an 87-module one alike, with nothing to re-tune when either changes.
 */
import { transformSync } from 'esbuild';
import { readFileSync, readdirSync } from 'node:fs';
import * as path from 'node:path';

const ROOT = 'src';

function walk(dir) {
  const out = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) out.push(...walk(p));
    else if (e.name.endsWith('.ts')) out.push(p);
  }
  return out;
}

const files = walk(ROOT);

// Every directory that HOLDS a .ts file must appear among the files the walk returned. Derived from
// the tree in front of it, so it fits any build; a flat readdir fails it immediately, which is the
// regression this guard was written for.
const dirsWithSource = new Set();
(function scan(dir) {
  let entries;
  try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
  if (entries.some((e) => e.isFile() && e.name.endsWith('.ts'))) dirsWithSource.add(dir);
  for (const e of entries) if (e.isDirectory()) scan(path.join(dir, e.name));
})(ROOT);

const covered = new Set(files.map((f) => path.dirname(f)));
const missed = [...dirsWithSource].filter((d) => !covered.has(d));
if (!files.length || missed.length) {
  console.error(`[image] the walk returned ${files.length} file(s) and missed ${missed.length} `
    + `directory/ies that contain TypeScript: ${missed.slice(0, 5).join(', ') || '(none — but the walk found nothing)'}`);
  console.error('[image] The walk is broken, not the tree. Refusing to certify a build it did not read.');
  process.exit(1);
}

for (const f of files) {
  transformSync(readFileSync(f, 'utf8'), { loader: 'ts' });
}

console.log(`[image] ${files.length} source file(s) transform cleanly`);
