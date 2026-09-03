// userLogoStore.ts — per-user custom profile logo (operator 2026-06-23:
// "logo customizable por el usuario"). A signed-in player uploads a small PNG
// that becomes their profile avatar/logo, framed by whatever FX aura ring they
// have equipped. One logo per user, replaced on re-upload.
//
// Storage: DATA_PATH/user-logos/<key>.png, where <key> is a filesystem-safe
// SHA-256 of the Discord sub — the raw sub NEVER appears in a path or a public
// URL (GDPR). The public serve route resolves NICKNAME -> sub -> key entirely
// server-side, so the profile image is fetched by nickname only.
//
// Security: upload/delete are auth-gated + same-origin (the AssetServer route
// wraps these helpers). The body is validated as a real PNG (signature + IHDR,
// shared with logoStore.validatePng) and bounded in bytes + pixel dimensions
// before it touches disk. The client also re-encodes through a canvas, so the
// stored bytes are always a freshly-rasterised PNG.

import * as fs from 'node:fs';
import * as fsp from 'node:fs/promises';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { validatePng } from './logoStore.js';

export const USER_LOGO_MAX_BYTES = 512 * 1024; // 512 KiB — an avatar renders ~92px
export const USER_LOGO_MAX_DIM = 512;          // px — cap the source

// Read DATA_PATH lazily (same value config.DATA_PATH resolves) so tests can point
// it at a temp dir without fighting ESM import order.
function baseDir(): string { return path.join(process.env.DATA_PATH ?? './data', 'user-logos'); }

// Filesystem-safe, collision-free key — the raw sub never lands in a path.
function keyFor(sub: string): string { return createHash('sha256').update(String(sub)).digest('hex').slice(0, 32); }
function fileFor(sub: string): string { return path.join(baseDir(), keyFor(sub) + '.png'); }

export function userLogoStat(sub: string): fs.Stats | null {
  if (!sub) return null;
  try { const st = fs.statSync(fileFor(sub)); return st.isFile() ? st : null; } catch { return null; }
}
export function userLogoPath(sub: string): string | null {
  return userLogoStat(sub) ? fileFor(sub) : null;
}

export interface SaveOk { ok: true; width: number; height: number; bytes: number; }
export interface SaveErr { ok: false; error: string; status: number; }

export async function saveUserLogo(sub: string, buf: Buffer): Promise<SaveOk | SaveErr> {
  if (!sub) return { ok: false, error: 'unauthorized', status: 401 };
  if (!Buffer.isBuffer(buf) || buf.length === 0) return { ok: false, error: 'empty body — send the PNG as image/png', status: 400 };
  if (buf.length > USER_LOGO_MAX_BYTES) return { ok: false, error: `logo exceeds ${USER_LOGO_MAX_BYTES} bytes`, status: 413 };
  const png = validatePng(buf);
  if (!png.ok) return { ok: false, error: png.error, status: 400 };
  if (png.width > USER_LOGO_MAX_DIM || png.height > USER_LOGO_MAX_DIM) {
    return { ok: false, error: `logo is ${png.width}x${png.height}; max ${USER_LOGO_MAX_DIM}x${USER_LOGO_MAX_DIM}`, status: 400 };
  }
  const dir = baseDir();
  await fsp.mkdir(dir, { recursive: true });
  const tmp = path.join(dir, `.${keyFor(sub)}.${process.pid}.${Date.now()}.tmp`);
  await fsp.writeFile(tmp, buf);
  await fsp.rename(tmp, fileFor(sub)); // atomic replace
  return { ok: true, width: png.width, height: png.height, bytes: buf.length };
}

export async function deleteUserLogo(sub: string): Promise<boolean> {
  if (!sub) return false;
  try { await fsp.unlink(fileFor(sub)); return true; } catch { return false; }
}

/** Public URL for a user's logo by nickname (the sub stays hidden), or null when
 *  they have not uploaded one. ?v=<mtime> busts the cache after a re-upload. */
export function userLogoUrlByNick(nick: string, sub: string): string | null {
  const st = userLogoStat(sub);
  return st ? `/api/u/${encodeURIComponent(nick)}/logo?v=${Math.floor(st.mtimeMs)}` : null;
}
