#!/usr/bin/env node
// precompress-gamefiles.mjs — brotli q11 a multi-worker compressor that
// runs on the operator's workstation against the NAS via SMB. Native
// Node CPU (modern x86 / Apple Silicon) is 5-10× faster than a low-end
// NAS DSM CPU at brotli q11 — same total work, fraction of the
// wall time.
//
// Inputs:
//   --in     directory of original .mul / .uop / .def / .txt files (RO)
//   --out    directory where .br twins will be written (RW, created if
//            missing). MUST be a parallel dir alongside --in so the
//            symlinks (created later by precompress-symlinks docker
//            run) resolve via `../<in-basename>/file`.
//   --quality  brotli quality 1..11. Default 11. Lower if you need a
//              quick run with ~80% of the gain.
//   --workers  parallel worker count. Default = os.cpus().length - 1.
//
// Example:
//   node precompress-gamefiles.mjs \
//     --in 'Z:\gamefiles' --out 'Z:\gamefiles-web' --quality 11 --workers 6
//
// Output: per-file lines on stdout, summary at end. Exits non-zero on
// any compression error.

import { readdir, mkdir, rm } from 'node:fs/promises';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { brotliCompressSync, constants } from 'node:zlib';
import os from 'node:os';
import { Worker, isMainThread, parentPort, workerData } from 'node:worker_threads';
import { fileURLToPath } from 'node:url';

const EXT_MATCH = /\.(mul|uop|def|txt)$/i;

function parseArgs(argv) {
  const a = {};
  for (let i = 2; i < argv.length; i++) {
    const k = argv[i];
    if (k.startsWith('--')) {
      a[k.slice(2)] = argv[i + 1]; i++;
    }
  }
  return a;
}

async function listFiles(dir) {
  // v0.3.19 the gamefiles tree is canonicalised to lowercase via the
  // gamefiles-lowercase.sh one-shot, but we still defensively skip any
  // mixed-case stragglers in case the operator hasn't run that
  // migration yet on a fresh deploy.
  const ents = await readdir(dir, { withFileTypes: true });
  const out = [];
  for (const e of ents) {
    if (!e.isFile() || e.isSymbolicLink()) continue;
    if (!EXT_MATCH.test(e.name)) continue;
    if (e.name !== e.name.toLowerCase()) continue;  // skip case-variants
    out.push(e.name);
  }
  return out.sort();
}

// Worker entry — receives a chunk of filenames, brotli's each one,
// writes .br twins. Reports each file synchronously so progress is
// visible in real time (the original async-batched version made the
// 113-file run feel hung — workers only flushed results at chunk
// completion).
//
// brotliCompressSync is the right primitive here:
//   - Each worker_thread owns its own JS thread, so a sync call
//     blocks ONLY that worker, not the main thread or other workers.
//   - It avoids the libuv threadpool (default size 4) which would
//     cap real parallelism regardless of how many worker_threads
//     we spawn.
function runWorker(work) {
  const { inDir, outDir, quality, names } = work;
  const params = {
    [constants.BROTLI_PARAM_QUALITY]: quality,
    [constants.BROTLI_PARAM_MODE]: constants.BROTLI_MODE_GENERIC,
  };
  for (const name of names) {
    const srcPath = join(inDir, name);
    const dstPath = join(outDir, `${name}.br`);
    const t0 = Date.now();
    try {
      const buf = readFileSync(srcPath);
      const out = brotliCompressSync(buf, { params });
      // Skip writing if brotli would inflate (rare on very small or
      // already-compressed-internally inputs).
      if (out.length >= buf.length) {
        parentPort.postMessage({ name, raw: buf.length, br: buf.length, skipped: true, ms: Date.now() - t0 });
        continue;
      }
      writeFileSync(dstPath, out);
      parentPort.postMessage({ name, raw: buf.length, br: out.length, skipped: false, ms: Date.now() - t0 });
    } catch (e) {
      parentPort.postMessage({ name, error: e.message, ms: Date.now() - t0 });
    }
  }
  parentPort.postMessage({ done: true });
}

async function main() {
  const a = parseArgs(process.argv);
  const inDir = a.in;
  const outDir = a.out;
  const quality = parseInt(a.quality ?? '11', 10);
  const workers = parseInt(a.workers ?? String(Math.max(1, os.cpus().length - 1)), 10);

  if (!inDir || !outDir) {
    console.error('Usage: precompress-gamefiles.mjs --in <dir> --out <dir> [--quality 11] [--workers N]');
    process.exit(2);
  }
  console.log(`[precompress] in=${inDir}`);
  console.log(`[precompress] out=${outDir}`);
  console.log(`[precompress] quality=${quality} workers=${workers}`);

  await mkdir(outDir, { recursive: true });
  // Wipe stale .br twins from a prior run.
  const stale = await readdir(outDir, { withFileTypes: true });
  for (const e of stale) {
    if (e.isFile() && e.name.endsWith('.br')) {
      await rm(join(outDir, e.name));
    }
  }

  const names = await listFiles(inDir);
  console.log(`[precompress] ${names.length} input files`);
  if (names.length === 0) { process.exit(0); }

  // Round-robin chunks (one per worker). Round-robin distributes
  // mixed file sizes evenly — sequential blocks would dump all the
  // big animX.mul files onto the same worker.
  const chunks = Array.from({ length: workers }, () => []);
  names.forEach((n, i) => chunks[i % workers].push(n));

  const t0 = Date.now();
  let totalRaw = 0;
  let totalBr = 0;
  let okCount = 0;
  let skipCount = 0;
  let errCount = 0;

  let completedCount = 0;
  await Promise.all(chunks.map((chunk, i) => new Promise((res, rej) => {
    if (chunk.length === 0) { res(); return; }
    const w = new Worker(fileURLToPath(import.meta.url), {
      workerData: { inDir, outDir, quality, names: chunk },
    });
    w.on('message', (r) => {
      if (r.done) { w.terminate(); res(); return; }
      // Per-file message — print immediately for live progress.
      completedCount++;
      const prefix = `[${completedCount}/${names.length}]`;
      if (r.error) {
        console.error(`  ${prefix} ERR  ${r.name.padEnd(32)} ${r.error}`);
        errCount++;
      } else if (r.skipped) {
        console.log(`  ${prefix} skip ${r.name.padEnd(32)} raw=${String(r.raw).padStart(10)}  (no win)`);
        skipCount++;
        totalRaw += r.raw;
        totalBr += r.raw;
      } else {
        const pct = Math.round(r.br * 100 / r.raw);
        console.log(`  ${prefix} ok   ${r.name.padEnd(32)} raw=${String(r.raw).padStart(10)}  br=${String(r.br).padStart(10)} (${String(pct).padStart(2)}%) ${r.ms}ms`);
        okCount++;
        totalRaw += r.raw;
        totalBr += r.br;
      }
    });
    w.on('error', rej);
  })));

  const t1 = Date.now();
  const elapsed = ((t1 - t0) / 1000).toFixed(1);
  const mbRaw = (totalRaw / 1048576).toFixed(1);
  const mbBr = (totalBr / 1048576).toFixed(1);
  const mbSaved = ((totalRaw - totalBr) / 1048576).toFixed(1);
  const pctSaved = totalRaw > 0 ? Math.round(100 * (totalRaw - totalBr) / totalRaw) : 0;
  console.log();
  console.log('[precompress] === SUMMARY ===');
  console.log(`[precompress] ok=${okCount}  skip=${skipCount}  err=${errCount}  elapsed=${elapsed}s`);
  console.log(`[precompress] raw    = ${mbRaw} MB`);
  console.log(`[precompress] served = ${mbBr} MB  (saved ${mbSaved} MB, ${pctSaved}%)`);

  if (errCount > 0) process.exit(1);
}

if (isMainThread) {
  main().catch(e => { console.error(e); process.exit(1); });
} else {
  runWorker(workerData);
}
