// screenshotStore.ts — per-user gameview screenshot showcase (operator 2026-06-23:
// "los clientes cuo y tuo, si haces una screenshot del propio cliente … desde el
// perfil pueda seleccionar … solo de la gameview zone … solo se almacenen las 5
// ultimas … para evitar que me llenen el disco").
//
// This is the SERVER exposition side: a ring-buffer of the LAST 5 screenshots a
// player chose to upload, shown on their public profile. The client captures the
// game viewport in C# (never the browser), keeps its own OPFS ring-buffer of 5,
// and the player explicitly picks which to upload — uploads are NEVER automatic and
// the UI offers no arbitrary file picker, which removes the easy/accidental path to
// non-gameview content.
//
// ACCEPTED LIMIT (not an enforced invariant): this is NOT cryptographic provenance.
// A determined signed-in user can write an arbitrary PNG to their own same-origin
// OPFS dir via devtools and publish it. The real abuse controls are: (1) server-side
// PNG signature + size/dimension bounds validation here, (2) the 30-day content-audit
// log (sub + IP per upload, accessAudit.ts), (3) report/takedown + the last-5 cap.
// Do NOT treat the stored bytes as guaranteed gameview-only content.
//
// Storage: DATA_PATH/screenshots/<sha256(sub)>/<sha256(bytes)>.png — the raw sub
// NEVER appears in a path (GDPR), and content-addressed filenames dedupe re-uploads
// of the same shot. On every add we prune to the newest MAX_SHOTS by mtime. The
// public serve route resolves NICKNAME -> sub server-side (sub stays hidden).

import * as fs from 'node:fs';
import * as fsp from 'node:fs/promises';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { validatePng } from './logoStore.js';

export const SHOT_MAX_BYTES = 4 * 1024 * 1024; // 4 MiB — a gameview PNG
export const SHOT_MAX_DIM = 2560;              // px — generous for a hi-dpi viewport
export const SHOT_MIN_DIM = 64;                // px — reject degenerate/empty captures
export const MAX_SHOTS = 5;                    // ring-buffer depth per user

// Lazy DATA_PATH read (same value config.DATA_PATH resolves) so tests can point it
// at a temp dir without fighting ESM import order.
function userDir(sub: string): string {
  return path.join(process.env.DATA_PATH ?? './data', 'screenshots', createHash('sha256').update(String(sub)).digest('hex').slice(0, 32));
}
const ID_RE = /^[0-9a-f]{16,64}$/; // a content-hash id
function idOf(buf: Buffer): string { return createHash('sha256').update(buf).digest('hex').slice(0, 40); }
function fileFor(sub: string, id: string): string { return path.join(userDir(sub), id + '.png'); }

export interface ShotMeta { id: string; mtime: number; bytes: number; }

/** Newest-first list of a user's stored screenshots (id + mtime + size). */
export function listScreenshots(sub: string): ShotMeta[] {
  if (!sub) return [];
  let names: string[];
  try { names = fs.readdirSync(userDir(sub)); } catch { return []; }
  const out: ShotMeta[] = [];
  for (const n of names) {
    if (!n.endsWith('.png')) continue;
    const id = n.slice(0, -4);
    if (!ID_RE.test(id)) continue;
    try { const st = fs.statSync(path.join(userDir(sub), n)); if (st.isFile()) out.push({ id, mtime: Math.floor(st.mtimeMs), bytes: st.size }); } catch { /* skip */ }
  }
  return out.sort((a, b) => b.mtime - a.mtime);
}

export function screenshotPath(sub: string, id: string): string | null {
  if (!sub || !ID_RE.test(String(id))) return null;
  const f = fileFor(sub, id);
  try { return fs.statSync(f).isFile() ? f : null; } catch { return null; }
}
export function screenshotStat(sub: string, id: string): fs.Stats | null {
  if (!sub || !ID_RE.test(String(id))) return null;
  try { const st = fs.statSync(fileFor(sub, id)); return st.isFile() ? st : null; } catch { return null; }
}

export interface AddOk { ok: true; id: string; width: number; height: number; bytes: number; count: number; }
export interface AddErr { ok: false; error: string; status: number; }

/** Validate + store a screenshot, then prune the user's ring-buffer to MAX_SHOTS. */
export async function addScreenshot(sub: string, buf: Buffer): Promise<AddOk | AddErr> {
  if (!sub) return { ok: false, error: 'unauthorized', status: 401 };
  if (!Buffer.isBuffer(buf) || buf.length === 0) return { ok: false, error: 'empty body — send the PNG as image/png', status: 400 };
  if (buf.length > SHOT_MAX_BYTES) return { ok: false, error: `screenshot exceeds ${SHOT_MAX_BYTES} bytes`, status: 413 };
  const png = validatePng(buf);
  if (!png.ok) return { ok: false, error: png.error, status: 400 };
  if (png.width > SHOT_MAX_DIM || png.height > SHOT_MAX_DIM) return { ok: false, error: `screenshot is ${png.width}x${png.height}; max ${SHOT_MAX_DIM}x${SHOT_MAX_DIM}`, status: 400 };
  if (png.width < SHOT_MIN_DIM || png.height < SHOT_MIN_DIM) return { ok: false, error: `screenshot is too small (${png.width}x${png.height})`, status: 400 };
  const dir = userDir(sub);
  await fsp.mkdir(dir, { recursive: true });
  const id = idOf(buf);
  const tmp = path.join(dir, `.${id}.${process.pid}.${Date.now()}.tmp`);
  await fsp.writeFile(tmp, buf);
  await fsp.rename(tmp, fileFor(sub, id)); // atomic; re-upload of same bytes just refreshes mtime
  await pruneToMax(sub);
  return { ok: true, id, width: png.width, height: png.height, bytes: buf.length, count: listScreenshots(sub).length };
}

/** Delete one screenshot by id. Returns true if a file was removed. */
export async function deleteScreenshot(sub: string, id: string): Promise<boolean> {
  if (!sub || !ID_RE.test(String(id))) return false;
  try { await fsp.unlink(fileFor(sub, id)); return true; } catch { return false; }
}

/** Erase EVERY screenshot a user stored, and their (hashed) directory.
 *  Used by the GDPR account-erasure path: these are player-uploaded images tied to
 *  an identity, so "delete my account" has to take them with it. Audit 2026-07-25
 *  found erasure purged DB rows + profile blobs but left this tree on disk.
 *  Returns how many files were removed; best-effort (never throws). */
export async function deleteAllScreenshots(sub: string): Promise<number> {
  if (!sub) return 0;
  const n = listScreenshots(sub).length;
  // 🚨 THE DIRECTORY, NOT THE FILES IT HAPPENS TO LIST. The previous version unlinked every `.png`
  // that `listScreenshots` returned and then called `rmdir`, treating a non-empty directory as
  // "fine". But non-empty means something survived that the listing does not see — and the upload
  // writes `.<id>.<pid>.<ts>.tmp` in THIS directory before renaming it into place. A process that
  // dies between the write and the rename (an OOM kill, ENOSPC, a container recreated mid-upload)
  // leaves that temp behind: the listing skips it because it is not a `.png`, the rmdir then fails,
  // and erasure reports success while a hashed-per-user directory holding player-uploaded image
  // bytes stays on disk. Same family as the cards.db wipe found earlier this session — erasure
  // saying "done" over data that is still there.
  //
  // Removing the tree covers whatever is in it, listed or not. Safe by construction: userDir is
  // `<DATA_PATH>/screenshots/<sha256(sub) truncated>` — a per-user LEAF whose name is a hash, so
  // no value of `sub` can traverse out of it or name a shared path, and an empty `sub` returned
  // above before reaching here.
  try {
    await fsp.rm(userDir(sub), { recursive: true, force: true });
  } catch (e) {
    // Best-effort by contract (the caller voids it), but a GDPR erasure that fails must not do so
    // in silence: this line is the only thing that will ever tell a human the tree is still there.
    console.error(`[screenshots] erasure FAILED to remove the user directory: ${(e as Error).message}`);
  }
  return n;
}

/** Drop the oldest screenshots beyond MAX_SHOTS (by mtime). */
async function pruneToMax(sub: string): Promise<void> {
  const all = listScreenshots(sub); // newest-first
  for (const old of all.slice(MAX_SHOTS)) {
    try { await fsp.unlink(fileFor(sub, old.id)); } catch { /* already gone */ }
  }
}

/** Public showcase URLs (newest-first, by nickname so the sub stays hidden). */
export function screenshotUrlsByNick(nick: string, sub: string): string[] {
  return listScreenshots(sub).map((s) => `/api/u/${encodeURIComponent(nick)}/screenshot/${s.id}?v=${s.mtime}`);
}
