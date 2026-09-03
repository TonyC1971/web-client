#!/usr/bin/env node
/**
 * build-minimal-bundle.mjs — produce a minimal client bundle from a fork you have already compiled.
 *
 *   node server/scripts/build-minimal-bundle.mjs cuo    ->  client/minimal      (ClassicUO)
 *   node server/scripts/build-minimal-bundle.mjs tuo    ->  client/minimal-tuo  (TazUO)
 *
 * 🚨 THIS STEP WAS MISSING ENTIRELY, and its absence made the published repo unable to build the
 * client its own README tells you to run. The publisher ships SOURCE, never `client/`; build.sh
 * dispatches cuo|tuo|mini and has no `minimal`; and build-web REFUSES to run against a bundle
 * directory that does not already exist ("no prior full build"). So a fresh clone had every
 * ingredient and no recipe — and nobody noticed, because in the monorepo `client/minimal` had been
 * sitting there since the day it was first made by hand.
 *
 * WHAT A MINIMAL BUNDLE ACTUALLY IS, and why this is cheap: the compiled WASM is identical to the
 * fork's. `client/minimal/_framework` is byte-for-byte `client/cuo/_framework`; the difference is
 * entirely the web layer, which build-web re-derives from source/webclient/minimal-www. So this
 * copies the fork's build and re-derives the web layer on top. No second WASM compile, ever.
 *
 * It is deliberately NOT part of build.sh's CLIENT_NAME dispatch: those branches each drive a real
 * `dotnet publish` of a different C# project, and putting a step that compiles nothing beside them
 * would suggest a fourth WASM build exists.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const log = (m) => console.log(`[build-minimal] ${m}`);

/** fork -> [the bundle it is built from, the bundle produced]. */
const FORKS = new Map([
  ['cuo', { from: 'cuo', to: 'minimal', label: 'ClassicUO' }],
  ['tuo', { from: 'tuo', to: 'minimal-tuo', label: 'TazUO' }],
]);

const fork = (process.argv[2] || '').toLowerCase();
const spec = FORKS.get(fork);
if (!spec) {
  console.error('Usage: build-minimal-bundle.mjs <cuo|tuo>');
  console.error('  cuo -> client/minimal      (ClassicUO, served at /)');
  console.error('  tuo -> client/minimal-tuo  (TazUO, served at /tuo/)');
  process.exit(2);
}

const src = path.join(REPO, 'client', spec.from);
const dst = path.join(REPO, 'client', spec.to);

// Refuse with the command that fixes it. "Directory not found" would send someone hunting for a
// build step that does not exist, which is exactly the hole this script fills.
if (!fs.existsSync(path.join(src, '_framework'))) {
  console.error(`[build-minimal] ${spec.label} has not been compiled yet: client/${spec.from}/_framework is missing.`);
  console.error(`[build-minimal] Build the fork first, then re-run this:`);
  console.error(`[build-minimal]   CLIENT_NAME=${spec.from} ./server/scripts/build.sh prod`);
  process.exit(1);
}

// 🚨 config.json is the SELF-HOSTER'S file and must survive a rebuild. Everything else in the target
// is regenerated, so the copy is wholesale except for this one path. Overwriting it is not a
// hypothetical: a plain `cp -r` of the bundle over a deployment reset a configured shard back to the
// placeholder, which reads as the client silently forgetting where it points.
const keepConfig = path.join(dst, 'config.json');
const preserved = fs.existsSync(keepConfig) ? fs.readFileSync(keepConfig) : null;

log(`seeding client/${spec.to} from client/${spec.from} (${spec.label} WASM, unchanged)`);
fs.rmSync(dst, { recursive: true, force: true });
fs.cpSync(src, dst, { recursive: true });
if (preserved) {
  fs.writeFileSync(keepConfig, preserved);
  log('kept your existing config.json');
}

// Now re-derive the web layer: minimal-www's index.html, main.js, rail.js, rail.css, minimal-boot.js
// and admin.html, re-fingerprinted and SRI-patched against the WASM that is already there.
log(`deriving the minimal web layer into client/${spec.to}`);
execFileSync(process.execPath, [path.join(REPO, 'server', 'scripts', 'build-web.mjs'), '--client', spec.to],
  { cwd: REPO, stdio: 'inherit' });

log(`done — client/${spec.to} is ${spec.label} plus the minimal web layer`);
