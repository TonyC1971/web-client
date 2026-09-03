// clientIp.ts — canonical client-IP resolver for rate limiting / per-IP caps.
//
// Only trust X-Forwarded-For / X-Real-IP when a reverse-proxy hop count is
// configured (TRUST_PROXY_HOPS > 0, default 0 = fail-closed); take the trusted
// hop (parts.length - hops, i.e. the LAST entry nginx appended, NOT the first
// attacker-supplied one); fall back to the socket address. Mirrors the inline
// helpers in AssetServer (clientIp) and UOProxy (clientIpFromReq); uoamHub uses
// THIS so its per-IP DoS cap can't be bypassed by a spoofed X-Forwarded-For
// first-entry (security audit 2026-06-21).

import type { IncomingMessage } from 'node:http';
import { TRUST_PROXY_HOPS } from './config.js';

export function clientIpFromReq(req: IncomingMessage): string {
  if (TRUST_PROXY_HOPS > 0) {
    const xff = req.headers['x-forwarded-for'];
    if (typeof xff === 'string' && xff.length > 0) {
      const parts = xff.split(',').map((s) => s.trim()).filter(Boolean);
      const idx = Math.max(0, parts.length - TRUST_PROXY_HOPS);
      if (parts[idx]) { return parts[idx]; }
    }
    const xri = req.headers['x-real-ip'];
    if (typeof xri === 'string' && xri.length > 0) { return xri; }
  }
  return req.socket.remoteAddress ?? 'unknown';
}
