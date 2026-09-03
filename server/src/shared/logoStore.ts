// logoStore.ts — admin upload + serving of per-shard picker logos.
//
// Operator req 2026-06-09: admins want to upload a transparent PNG logo for a
// shard (not just point the YAML `logo` field at a URL). Logos are stored under
// DATA_PATH/logos/<slug>.png — DATA_PATH is the proxy's writable volume (it
// already holds votes.json / app-config.json), so this needs NO change to the
// (read-only) gamefiles mount. The picker fetches the logo via the public
// GET /api/servers/:slug/logo route; /api/servers points `logo` at that route
// (with a ?v=<mtime> cache-bust) whenever an uploaded PNG exists, overriding the
// YAML logo. Deleting the upload falls back to the YAML logo.
//
// Security: upload/delete are admin-gated (resolveAdmin → canEditServer) +
// same-origin (requireSafeOrigin) + audited. The slug is regex-validated by the
// caller so `<slug>.png` can't traverse. The body is validated as a real PNG
// (signature + IHDR) and bounded in bytes + pixel dimensions before it touches
// disk. validatePng is pure and unit-tested.

import express, { type Application, type Request, type Response, type RequestHandler } from 'express';
import * as fs from 'node:fs';
import * as fsp from 'node:fs/promises';
import * as path from 'node:path';
import { SLUG_REGEX } from './serverRegistry.js';

export const LOGO_MAX_BYTES = 1024 * 1024;   // 1 MiB — generous for a picker logo
export const LOGO_MAX_DIM   = 1024;           // px — logos render tiny; cap the source
// Banners are the big hero PNG shown in the floating shard-detail panel — bigger
// than a logo, so a looser cap (operator 2026-06-12).
export const BANNER_MAX_BYTES = 4 * 1024 * 1024; // 4 MiB
export const BANNER_MAX_DIM   = 2560;             // px

const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export interface PngInfo { ok: true; width: number; height: number; colorType: number; }
export interface PngBad  { ok: false; error: string; }

/**
 * Validate a buffer as a PNG and pull width/height/colorType from IHDR. Pure.
 * Accepts ANY valid PNG (opaque or with alpha) — the operator wants to upload
 * transparent logos, but we don't *require* transparency. Returns {ok:false}
 * with a reason for anything that isn't a well-formed PNG header.
 */
export function validatePng(buf: Buffer): PngInfo | PngBad {
  if (!Buffer.isBuffer(buf) || buf.length < 33) return { ok: false, error: 'not a PNG (too small)' };
  if (!buf.subarray(0, 8).equals(PNG_SIG)) return { ok: false, error: 'not a PNG (bad signature)' };
  // First chunk must be IHDR with a 13-byte body.
  if (buf.readUInt32BE(8) !== 13 || buf.toString('latin1', 12, 16) !== 'IHDR') {
    return { ok: false, error: 'not a PNG (missing IHDR)' };
  }
  const width = buf.readUInt32BE(16);
  const height = buf.readUInt32BE(20);
  const colorType = buf[25];
  if (width < 1 || height < 1) return { ok: false, error: 'PNG has zero dimensions' };
  return { ok: true, width, height, colorType };
}

// ── storage (dir-parameterised so tests can use a temp dir) ──────────────────

function imageFile(dir: string, slug: string): string {
  return path.join(dir, `${slug}.png`);
}

/** fs.Stats for an uploaded image (logo OR banner), or null if none. */
export function logoStat(dir: string, slug: string): fs.Stats | null {
  try { return fs.statSync(imageFile(dir, slug)); } catch { return null; }
}
/** The served URL for an uploaded image of `kind`, or null. ?v=<mtime> busts cache. */
function uploadedImageUrl(dir: string, slug: string, kind: 'logo' | 'banner'): string | null {
  const st = logoStat(dir, slug);
  return st ? `/api/servers/${slug}/${kind}?v=${Math.floor(st.mtimeMs)}` : null;
}
export function uploadedLogoUrl(dir: string, slug: string): string | null { return uploadedImageUrl(dir, slug, 'logo'); }
export function uploadedBannerUrl(dir: string, slug: string): string | null { return uploadedImageUrl(dir, slug, 'banner'); }

async function writeImage(dir: string, slug: string, buf: Buffer): Promise<void> {
  await fsp.mkdir(dir, { recursive: true });
  const tmp = path.join(dir, `.${slug}.${process.pid}.${Date.now()}.tmp`);
  await fsp.writeFile(tmp, buf);
  await fsp.rename(tmp, imageFile(dir, slug)); // atomic replace
}

export interface LogoRouteDeps {
  /** Directory logos live in (e.g. DATA_PATH/logos). */
  logosDir: string;
  /** → Discord admin id for a valid admin session, else null. */
  resolveAdmin: (req: Request) => string | null;
  /** → true if this admin may edit this shard slug. */
  canEditServer: (sub: string, slug: string) => boolean;
  /** Same-origin gate for state-changing requests (sends 403 + returns false). */
  requireSafeOrigin: (req: Request, res: Response) => boolean;
  /** → true if the shard slug exists (so we 404 unknown shards). */
  serverExists: (slug: string) => boolean;
  /** Append-only admin audit trail. */
  auditLog: (req: Request, sub: string, action: string, payload: unknown) => Promise<void>;
  /** Moderate limiter for the admin upload/delete. */
  adminLimiter: RequestHandler;
  /** Limiter for the public GET. */
  publicLimiter: RequestHandler;
}

/** 🚨 IMPORTED, not copied — serverRegistry owns what a shard slug is. See overrides.ts. */
const SLUG_RE = SLUG_REGEX;

// Generic per-shard image (logo | banner) routes. Logos and banners share the
// exact same upload/serve/delete flow + gates; they differ only in the route
// segment, the storage dir, and the size/pixel caps.
function attachImageRoutes(app: Application, kind: 'logo' | 'banner', dir: string, maxBytes: number, maxDim: number, deps: LogoRouteDeps): void {
  const { resolveAdmin, canEditServer, requireSafeOrigin, serverExists, auditLog, adminLimiter, publicLimiter } = deps;

  // GET /api/servers/:slug/<kind> — PUBLIC (the picker is the pre-auth landing).
  app.get(`/api/servers/:slug/${kind}`, publicLimiter, async (req, res) => {
    const slug = String(req.params.slug ?? '').trim().toLowerCase();
    if (!SLUG_RE.test(slug)) { res.status(400).end(); return; }
    const st = logoStat(dir, slug);
    if (!st || !st.isFile()) { res.status(404).end(); return; }
    const etag = `"${st.size}-${Math.floor(st.mtimeMs)}"`;
    if (req.headers['if-none-match'] === etag) { res.status(304).end(); return; }
    res.setHeader('Content-Type', 'image/png');
    res.setHeader('ETag', etag);
    res.setHeader('Cache-Control', 'public, max-age=60, must-revalidate');
    fs.createReadStream(imageFile(dir, slug)).on('error', () => { if (!res.headersSent) res.status(500).end(); }).pipe(res);
  });

  // POST /api/admin/servers/:slug/<kind> — upload a PNG (raw image/png body).
  app.post(`/api/admin/servers/:slug/${kind}`,
    express.raw({ type: ['image/png', 'application/octet-stream'], limit: maxBytes }),
    adminLimiter,
    async (req, res) => {
      const slug = String(req.params.slug ?? '').trim().toLowerCase();
      if (!requireSafeOrigin(req, res)) return;
      const sub = resolveAdmin(req);
      if (!sub) { res.status(401).json({ error: 'unauthorized' }); return; }
      if (!SLUG_RE.test(slug) || !serverExists(slug)) { res.status(404).json({ error: `server '${slug}' not found` }); return; }
      if (!canEditServer(sub, slug)) { res.status(403).json({ error: `you don't have permission to manage '${slug}'` }); return; }
      const buf = Buffer.isBuffer(req.body) ? req.body : null;
      if (!buf || buf.length === 0) { res.status(400).json({ error: 'empty body — send the PNG as image/png' }); return; }
      if (buf.length > maxBytes) { res.status(413).json({ error: `${kind} exceeds ${maxBytes} bytes` }); return; }
      const png = validatePng(buf);
      if (!png.ok) { res.status(400).json({ error: png.error }); return; }
      if (png.width > maxDim || png.height > maxDim) {
        res.status(400).json({ error: `${kind} is ${png.width}x${png.height}; max ${maxDim}x${maxDim}` });
        return;
      }
      try {
        await writeImage(dir, slug, buf);
        await auditLog(req, sub, `server.${kind}.upload`, { slug, bytes: buf.length, width: png.width, height: png.height });
        res.status(201).json({ ok: true, slug, url: uploadedImageUrl(dir, slug, kind), width: png.width, height: png.height, bytes: buf.length });
      } catch (e) {
        res.status(500).json({ error: `${kind} write failed: ${(e as Error).message}` });
      }
    });

  // DELETE /api/admin/servers/:slug/<kind> — remove the uploaded PNG.
  app.delete(`/api/admin/servers/:slug/${kind}`, adminLimiter, async (req, res) => {
    const slug = String(req.params.slug ?? '').trim().toLowerCase();
    if (!requireSafeOrigin(req, res)) return;
    const sub = resolveAdmin(req);
    if (!sub) { res.status(401).json({ error: 'unauthorized' }); return; }
    if (!SLUG_RE.test(slug) || !serverExists(slug)) { res.status(404).json({ error: `server '${slug}' not found` }); return; }
    if (!canEditServer(sub, slug)) { res.status(403).json({ error: `you don't have permission to manage '${slug}'` }); return; }
    if (!logoStat(dir, slug)) { res.status(404).json({ error: `no uploaded ${kind} for this shard` }); return; }
    try {
      await fsp.unlink(imageFile(dir, slug));
      await auditLog(req, sub, `server.${kind}.delete`, { slug });
      res.json({ ok: true, slug });
    } catch (e) {
      res.status(500).json({ error: `${kind} delete failed: ${(e as Error).message}` });
    }
  });
}

export function attachLogoRoutes(app: Application, deps: LogoRouteDeps): void {
  attachImageRoutes(app, 'logo', deps.logosDir, LOGO_MAX_BYTES, LOGO_MAX_DIM, deps);
}

/** Same as logos but for the bigger hero banner. `deps.logosDir` must be the
 *  BANNER directory here (the caller passes DATA_PATH/banners). */
export function attachBannerRoutes(app: Application, deps: LogoRouteDeps): void {
  attachImageRoutes(app, 'banner', deps.logosDir, BANNER_MAX_BYTES, BANNER_MAX_DIM, deps);
}
