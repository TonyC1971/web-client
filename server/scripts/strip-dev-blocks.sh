#!/usr/bin/env bash
# strip-dev-blocks.sh — Linux/macOS sibling of strip-dev-blocks.ps1.
# Must produce byte-identical output to the .ps1 so the release.bat
# (Windows) and release.sh (Linux) flows ship the same client/ bytes.
#
# See strip-dev-blocks.ps1 header for full rationale + invariants.
#
# Args:
#   --client-dir <path>   Required. The release client/ directory.
#
# Exit codes:
#   0 - strip completed (or no-op)
#   1 - strip failed (no main.js, multiple main.js, balance failure,
#       or HTML integrity entry not found)

set -eu

CLIENT_DIR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --client-dir) CLIENT_DIR="$2"; shift 2 ;;
    -h|--help) echo "Usage: $0 --client-dir <path>"; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; exit 1 ;;
  esac
done

if [ -z "${CLIENT_DIR}" ]; then
  echo "ERROR: --client-dir is required" >&2
  exit 1
fi
if [ ! -d "${CLIENT_DIR}" ]; then
  echo "ERROR: client dir not found: ${CLIENT_DIR}" >&2
  exit 1
fi

INDEX_PATH="${CLIENT_DIR}/index.html"
if [ ! -f "${INDEX_PATH}" ]; then
  echo "ERROR: index.html not found: ${INDEX_PATH}" >&2
  exit 1
fi

# Find the single main.<HASH>.js at client/ root.
MAIN_FILES=$(find "${CLIENT_DIR}" -maxdepth 1 -name 'main.*.js' -type f)
MAIN_COUNT=$(echo "${MAIN_FILES}" | grep -c . || true)
if [ "${MAIN_COUNT}" -eq 0 ]; then
  echo "ERROR: no main.*.js found under ${CLIENT_DIR}" >&2
  exit 1
fi
if [ "${MAIN_COUNT}" -gt 1 ]; then
  echo "ERROR: multiple main.*.js found under ${CLIENT_DIR}:" >&2
  echo "${MAIN_FILES}" >&2
  exit 1
fi
MAIN_FILE="${MAIN_FILES}"
MAIN_BASENAME=$(basename "${MAIN_FILE}")

# Use node for the actual transform — bash regex is too limited for
# the balanced-brace walk and multi-line block stripping. Bash's job
# is just to call node with the right args and check the exit code.
#
# The transform script is inlined here (no separate .mjs file) so
# this stays a single-script deliverable.
node --input-type=module - "${MAIN_FILE}" "${INDEX_PATH}" <<'EOF'
import { readFileSync, writeFileSync, renameSync, unlinkSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { basename } from 'node:path';

const [, , mainPath, indexPath] = process.argv;
const mainName = basename(mainPath);

let text = readFileSync(mainPath, 'utf8');
if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
const originalLen = text.length;

// --- Strip pass 1: `if (devMode) { ... }` block via balanced brace walk ---
function removeIfBlockByMarker(source, marker) {
  const idx = source.indexOf(marker);
  if (idx < 0) return source;
  const openIdx = source.indexOf('{', idx);
  if (openIdx < 0) return source;
  let depth = 1;
  let i = openIdx + 1;
  while (i < source.length && depth > 0) {
    const c = source[i];
    if (c === '{') depth++;
    else if (c === '}') depth--;
    i++;
  }
  if (depth !== 0) {
    console.warn('Brace walk failed to balance — leaving block intact');
    return source;
  }
  let end = i;
  while (end < source.length && (source[end] === '\r' || source[end] === '\n')) end++;
  let start = idx;
  while (start > 0 && (source[start - 1] === ' ' || source[start - 1] === '\t')) start--;
  return source.slice(0, start) + source.slice(end);
}

const beforeP1 = text.length;
text = removeIfBlockByMarker(text, 'if (devMode)');
const p1Removed = beforeP1 - text.length;
if (p1Removed > 0) {
  console.log(`  [pass1] removed if (devMode) { ... } block (${p1Removed} chars)`);
}

// --- Strip pass 2: line comments with forbidden keywords ---
const lineKeywords = ['Mercury', 'deputy', '\\.NET 10 MT', 'MAIN_THREAD_'];
const linePattern = new RegExp(`^[ \\t]*//.*(${lineKeywords.join('|')}).*\\r?\\n`, 'gm');
const beforeP2 = text.length;
text = text.replace(linePattern, '');
const p2Removed = beforeP2 - text.length;
if (p2Removed > 0) {
  console.log(`  [pass2] removed ${p2Removed} chars of single-line // … comments with sensitive keywords`);
}

// --- Strip pass 3: multi-line /* ... */ blocks with forbidden keywords ---
const blockKeywords = ['Mercury', 'deputy', '\\.NET 10 MT', 'MAIN_THREAD_'];
const blockPattern = new RegExp(
  `/\\*[^*]*?(?:\\*(?!/)[^*]*?)*?(${blockKeywords.join('|')})[^*]*?(?:\\*(?!/)[^*]*?)*?\\*/`,
  'gs',
);
const beforeP3 = text.length;
text = text.replace(blockPattern, '');
const p3Removed = beforeP3 - text.length;
if (p3Removed > 0) {
  console.log(`  [pass3] removed ${p3Removed} chars of multi-line /* … */ blocks with sensitive keywords`);
}

// --- Strip pass 4: standalone console.X(...) calls leaking keywords ---
const consoleKeywords = ['Mercury', 'deputy', '\\.NET 10 MT', 'MAIN_THREAD_', 'mt-canvas'];
const consolePattern = new RegExp(
  `^[ \\t]*console\\.\\w+\\([^)]*(${consoleKeywords.join('|')})[^)]*\\);\\r?\\n`,
  'gm',
);
const beforeP4 = text.length;
text = text.replace(consolePattern, '');
const p4Removed = beforeP4 - text.length;
if (p4Removed > 0) {
  console.log(`  [pass4] removed ${p4Removed} chars of standalone console.X() calls with sensitive keywords`);
}

// --- Strip pass 5 (final): all remaining comments via esbuild ---
import { existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, dirname } from 'node:path';

const scriptDir = dirname(new URL(import.meta.url).pathname);
const esbuildBin = join(scriptDir, '..', 'node_modules', '.bin', 'esbuild');
if (existsSync(esbuildBin)) {
  const beforeP5 = text.length;
  try {
    // --loader=js: needed because we feed via stdin (no file ext to infer)
    const out = execFileSync(esbuildBin, [
      '--loader=js',
      '--bundle=false',
      '--legal-comments=none',
      // v0.4.87: must be `true` to strip non-legal // and /* */ comments.
      // With `false`, esbuild's --legal-comments=none only drops `/*!`
      // and `/* @license */` blocks — regular comments survive. The
      // sibling strip-dev-blocks.ps1 (line 161) uses true; this file
      // had drifted to false and produced ~80KB MORE in the served
      // main.<HASH>.js on Linux vs Windows, breaking SRI reproducibility.
      '--minify-whitespace=true',
      '--minify-identifiers=false',
      '--minify-syntax=false',
      '--format=esm',
      '--target=esnext',
    ], { input: text, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
    text = out.replace(/\/\*\s*@__PURE__\s*\*\/\s*/g, '');
    const p5Removed = beforeP5 - text.length;
    console.log(`  [pass5] esbuild comment-strip: ${p5Removed} chars delta`);
  } catch (e) {
    console.error(`  [pass5] esbuild failed: ${e.message}`);
    process.exit(1);
  }
} else {
  console.warn(`  [pass5] esbuild not found at ${esbuildBin} -- skipping final comment-strip`);
}

const totalRemoved = originalLen - text.length;
if (totalRemoved === 0) {
  console.log(`  [strip-dev-blocks] nothing to strip in ${mainName} — exiting clean`);
  process.exit(0);
}

writeFileSync(mainPath, text, 'utf8');

const sha = createHash('sha256').update(text, 'utf8').digest('base64');
const newIntegrity = `sha256-${sha}`;

// v0.7.9 re-fingerprint: the WASM SDK fingerprints main.<hash>.js from its
// PRE-strip content, but we just mutated the bytes. Keeping the same filename
// lets a dev (un-stripped) build and a prod (stripped) build serve DIFFERENT
// bytes under the SAME `immutable`-cached name → any client/CDN that cached the
// other variant SRI-blocks main.js ("Failed to find a valid digest") → black
// screen until a manual cache purge. Rename to a token derived from the
// STRIPPED bytes so dev/prod never collide, and poisoned immutable caches MISS
// the new name → refetch fresh automatically. Token = first 10 lowercased
// alphanumerics of the stripped-content base64 sha (identical derivation in the
// .ps1 sibling for byte-identical cross-platform output).
const token = sha.replace(/[^a-zA-Z0-9]/g, '').toLowerCase().slice(0, 10);
const newName = `main.${token}.js`;

const mainDir = dirname(mainPath);
if (newName !== mainName) {
  renameSync(mainPath, join(mainDir, newName));
  // Drop the pre-rename .br twin (regenerated for the new name downstream).
  try { unlinkSync(join(mainDir, `${mainName}.br`)); } catch { /* none yet */ }
  console.log(`  [strip-dev-blocks] re-fingerprinted ${mainName} -> ${newName}`);
}
console.log(`  [strip-dev-blocks] ${newName} -> ${newIntegrity} (total -${totalRemoved} chars)`);

// Patch index.html: (1) set the integrity VALUE on the old name, then (2)
// rewrite EVERY reference of the old filename to the new one (importmap remap
// value, integrity key, and the <script src>). Literal split/join touches all
// occurrences; verify-sri (run post-strip by release.bat/.sh) is the guardrail.
let indexText = readFileSync(indexPath, 'utf8');
if (indexText.charCodeAt(0) === 0xFEFF) indexText = indexText.slice(1);
const escaped = mainName.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const rx = new RegExp(`"\\./${escaped}":\\s*"sha256-[A-Za-z0-9+/=]+"`);
if (!rx.test(indexText)) {
  console.error(`ERROR: no integrity entry for ${mainName} in index.html`);
  process.exit(1);
}
indexText = indexText.replace(rx, `"./${mainName}": "${newIntegrity}"`);
indexText = indexText.split(mainName).join(newName);
writeFileSync(indexPath, indexText, 'utf8');
console.log('  [strip-dev-blocks] index.html integrity + filename refreshed');
EOF
