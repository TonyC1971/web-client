// Validate the PROXY_BUILD_TAG line in server/src/UOProxy.ts.
//
// The tag is hand-edited on EVERY release, it is the boot banner that says what is
// actually running, and it is the only line in that file anyone routinely retypes. So it
// is where syntax breaks, and a break there is expensive: the proxy image is transformed
// by esbuild at container start, so a malformed literal does not fail the build -- it
// CRASH-LOOPS THE LIVE PROXY. The client release is unaffected (the bundles never compile
// this file), which is exactly what makes it easy to ship without noticing.
//
// Two real failures this has caused, both now covered:
//   - v0.9.542: writing a new tag while briefly keeping the old one on a second line left
//     the new line with no closing quote+semicolon. Unterminated string literal, prod down.
//   - earlier: apostrophes / backticks / ${...} inside the text, which end or interpolate
//     the single-quoted literal.
//
// The regex demands the WHOLE line be a well-formed single-quoted literal with no
// apostrophe and no backslash, which settles termination and early-close.
//
// Backticks and ${...} are then rejected SEPARATELY, and the distinction matters: inside a
// single-quoted JS string they are inert, so a syntax check will never object to them.
// They are banned because the tag does not only live in JS -- it is echoed through
// release.bat/.sh and the docker build, where a backtick is command substitution and ${..}
// is expansion. (First draft of this checker asserted one regex covered them; it did not,
// and only mutating the tag on purpose showed that. A check trusted for something it does
// not do is worse than no check.)
//
// Usage: node check-build-tag.mjs <path-to-UOProxy.ts> [expected-version]
// Exit 0 = fine, 1 = refuse to release.

import fs from 'node:fs';

const [file, want] = process.argv.slice(2);
if (!file) { console.error('[check-build-tag] usage: check-build-tag.mjs <UOProxy.ts> [version]'); process.exit(1); }

let txt;
try { txt = fs.readFileSync(file, 'utf8'); }
catch (e) { console.error(`[check-build-tag] cannot read ${file}: ${e.message}`); process.exit(1); }

const lines = txt.split(/\r?\n/);
const idx = lines.findIndex((l) => /^export const PROXY_BUILD_TAG\b/.test(l));
if (idx < 0) { console.error('[check-build-tag] ERROR: no `export const PROXY_BUILD_TAG` line found.'); process.exit(1); }

const line = lines[idx];
const m = /^export const PROXY_BUILD_TAG = '([^'\\]*)';\s*$/.exec(line);
if (!m) {
  console.error(`[check-build-tag] ERROR: ${file}:${idx + 1} is not a well-formed single-quoted literal.`);
  console.error('[check-build-tag]   The whole tag must be ONE line: export const PROXY_BUILD_TAG = \'...\';');
  console.error('[check-build-tag]   and the text may not contain an apostrophe, a backslash, a backtick or ${...}.');
  // Name the likely cause rather than leaving it to be diffed by eye.
  if (!/;\s*$/.test(line)) console.error('[check-build-tag]   → this line does not end in `;` — the string is probably unterminated.');
  else if (/[^=]\s*'.*'.*'/.test(line)) console.error("[check-build-tag]   → the text contains an apostrophe, which closes the literal early.");
  else if (line.includes('`') || line.includes('${')) console.error('[check-build-tag]   → the text contains a backtick or ${...}.');
  console.error(`[check-build-tag]   line reads: ${line.slice(0, 100)}${line.length > 100 ? '…' : ''}`);
  console.error('[check-build-tag]   A malformed tag CRASH-LOOPS the live proxy: esbuild transforms this file at container start.');
  process.exit(1);
}
// Inert in JS, dangerous in the shell layers this string is echoed through.
const shellUnsafe = [];
if (m[1].includes('`')) shellUnsafe.push('a backtick (command substitution in sh)');
if (m[1].includes('${')) shellUnsafe.push('${...} (variable expansion in sh and in .bat/docker)');
if (m[1].includes('%')) shellUnsafe.push('a % (variable expansion in cmd.exe / release.bat)');
if (shellUnsafe.length) {
  console.error(`[check-build-tag] ERROR: ${file}:${idx + 1} the tag text contains ${shellUnsafe.join(' and ')}.`);
  console.error('[check-build-tag]   Valid JS inside single quotes, but the tag is echoed through release.bat/.sh');
  console.error('[check-build-tag]   and the docker build, where those are interpreted. Reword without them.');
  process.exit(1);
}
if (want && !m[1].includes(want)) {
  console.error(`[check-build-tag] ERROR: PROXY_BUILD_TAG does not mention ${want}.`);
  console.error('[check-build-tag]   The boot banner is the canonical "what is actually running" signal, so it must');
  console.error('[check-build-tag]   name the version being shipped.');
  process.exit(1);
}
console.log(`[check-build-tag] OK — well-formed literal (${m[1].length} chars)${want ? `, mentions ${want}` : ''}.`);
