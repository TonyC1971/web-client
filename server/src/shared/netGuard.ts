// netGuard.ts — SSRF defence for the self-service shard flow (audit 2026-06-11).
//
// `assertPublicHost` (serverRegistry.ts) is a STRING gate: it rejects literal
// private IPs + localhost/.local names. That is necessary but NOT sufficient for
// an UNTRUSTED self-service owner, because it never resolves DNS:
//   • DNS→private: a hostname like `rebind.evil.com` that resolves to
//     192.168.1.10 / 127.0.0.1 / 169.254.169.254 passes the string gate, then the
//     probe fetch (GET) and the UOProxy TCP bridge connect into our LAN/metadata.
//   • Redirect-follow: the owner's server can 302 the probe to an internal IP.
//
// This module adds the resolve-time layer:
//   • isPrivateOrReservedIp(ip)   — classify a RESOLVED address.
//   • resolvePublicIp(host)       — resolve + assert EVERY A/AAAA is public, and
//                                   return one public IP to PIN the connection to
//                                   (defeats DNS-rebind for the plain-TCP bridge).
//   • assertResolvesPublic(host)  — throw if the host resolves to anything private
//                                   (used at registration + before the probe).
//
// The bridge connects to the PINNED IP (no TLS cert, safe to pin → rebind fully
// closed). The https probe still connects by hostname (cert validation needs the
// SNI name), so there it only rejects DNS→private + disallows redirects; the
// residual sub-second rebind TOCTOU on a one-shot GET is acceptable (no relay).

import { isIP } from 'node:net';
import { lookup } from 'node:dns/promises';

/** True if `ip` (a literal IPv4/IPv6) is private, loopback, link-local, CGNAT,
 *  ULA, multicast, or otherwise non-public. Mirrors serverRegistry's
 *  assertPublicHost literal rules and extends them for resolved addresses. */
export function isPrivateOrReservedIp(ip: string): boolean {
  const fam = isIP(ip);
  if (fam === 4) return isPrivateIpv4(ip);
  if (fam === 6) {
    // EXPAND to 8 hextets first (audit 2026-06-21 HIGH-SSRF fix). The old code
    // string-matched only the dotted-decimal embedded form (::ffff:127.0.0.1), so
    // the HEX form (::ffff:7f00:1) and the fully-expanded form (0:0:0:0:0:ffff:
    // 7f00:1) of an IPv4-mapped address slipped through as "public" → an internal
    // IP (NAS / cloud-metadata) could be reached. Bit-classifying the expanded
    // hextets closes every spelling.
    const hx = expandIpv6(ip);
    if (!hx) return true; // unparseable IPv6 literal → treat as unsafe
    const first5Zero = hx[0] === 0 && hx[1] === 0 && hx[2] === 0 && hx[3] === 0 && hx[4] === 0;
    // IPv4-mapped ::ffff:a.b.c.d (::ffff:0:0/96) — the common dual-stack form.
    if (first5Zero && hx[5] === 0xffff) return isPrivateIpv4(embeddedV4(hx));
    if (first5Zero && hx[5] === 0) {
      if (hx[6] === 0 && hx[7] === 0) return true;   // :: unspecified
      if (hx[6] === 0 && hx[7] === 1) return true;   // ::1 loopback
      return isPrivateIpv4(embeddedV4(hx));          // ::a.b.c.d IPv4-compatible (deprecated)
    }
    // NAT64 well-known prefix 64:ff9b::/96 → classify the embedded v4 (reject internal targets).
    if (hx[0] === 0x64 && hx[1] === 0xff9b && hx[2] === 0 && hx[3] === 0 && hx[4] === 0 && hx[5] === 0) return isPrivateIpv4(embeddedV4(hx));
    if ((hx[0] & 0xffc0) === 0xfe80) return true;    // fe80::/10 link-local
    if ((hx[0] & 0xfe00) === 0xfc00) return true;    // fc00::/7 ULA
    if ((hx[0] & 0xff00) === 0xff00) return true;    // ff00::/8 multicast
    if (hx[0] === 0x2001 && hx[1] === 0x0db8) return true; // 2001:db8::/32 documentation
    return false;
  }
  return true; // not a valid IP literal → treat as unsafe
}

/** Dotted-decimal of the last 32 bits (embedded IPv4) of an expanded IPv6. */
function embeddedV4(hx: number[]): string {
  return `${hx[6] >> 8}.${hx[6] & 0xff}.${hx[7] >> 8}.${hx[7] & 0xff}`;
}

/** Expand any IPv6 literal (compressed `::`, embedded-IPv4 tail, or full) to its 8
 *  16-bit hextets, or null if invalid. Handles `::ffff:1.2.3.4` by folding the
 *  dotted tail into two hextets first, then `::` zero-fill. Strips a zone id. */
function expandIpv6(ip: string): number[] | null {
  let s = ip.toLowerCase();
  const pct = s.indexOf('%');
  if (pct >= 0) { s = s.slice(0, pct); }
  // Fold a trailing dotted-quad (embedded IPv4) into two hex groups.
  const m = s.match(/^(.*:)((?:\d{1,3}\.){3}\d{1,3})$/);
  if (m) {
    const v4 = m[2].split('.').map(Number);
    if (v4.length !== 4 || v4.some((n) => !Number.isInteger(n) || n < 0 || n > 255)) { return null; }
    s = m[1] + ((v4[0] << 8) | v4[1]).toString(16) + ':' + ((v4[2] << 8) | v4[3]).toString(16);
  }
  let parts: string[];
  if (s.indexOf('::') >= 0) {
    if (s.indexOf('::') !== s.lastIndexOf('::')) { return null; } // more than one '::'
    const [head, tail] = s.split('::');
    const headParts = head ? head.split(':') : [];
    const tailParts = tail ? tail.split(':') : [];
    const fill = 8 - headParts.length - tailParts.length;
    if (fill < 0) { return null; }
    parts = [...headParts, ...Array(fill).fill('0'), ...tailParts];
  } else {
    parts = s.split(':');
  }
  if (parts.length !== 8) { return null; }
  const out = parts.map((p) => (p === '' ? 0 : parseInt(p, 16)));
  if (out.some((n) => !Number.isInteger(n) || n < 0 || n > 0xffff)) { return null; }
  return out;
}

function isPrivateIpv4(ip: string): boolean {
  const o = ip.split('.').map(Number);
  if (o.length !== 4 || o.some((n) => !Number.isInteger(n) || n < 0 || n > 255)) return true;
  return (
    o[0] === 0 ||                                  // 0.0.0.0/8 "this network"
    o[0] === 10 ||                                 // 10/8 private
    o[0] === 127 ||                                // 127/8 loopback
    (o[0] === 169 && o[1] === 254) ||              // 169.254/16 link-local (+ metadata)
    (o[0] === 172 && o[1] >= 16 && o[1] <= 31) ||  // 172.16/12 private
    (o[0] === 192 && o[1] === 168) ||              // 192.168/16 private
    (o[0] === 100 && o[1] >= 64 && o[1] <= 127) || // 100.64/10 CGNAT
    (o[0] === 192 && o[1] === 0 && o[2] === 0) ||  // 192.0.0/24 IETF protocol
    (o[0] === 192 && o[1] === 0 && o[2] === 2) ||  // 192.0.2/24 TEST-NET-1
    (o[0] === 198 && (o[1] === 18 || o[1] === 19)) || // 198.18/15 benchmark
    (o[0] === 198 && o[1] === 51 && o[2] === 100) || // 198.51.100/24 TEST-NET-2
    (o[0] === 203 && o[1] === 0 && o[2] === 113) ||  // 203.0.113/24 TEST-NET-3
    o[0] >= 224                                    // 224/4 multicast + 240/4 reserved + 255.255.255.255
  );
}

/** Resolve `host` and return a PUBLIC IP to connect to. Throws if the host (or
 *  any of its A/AAAA records) is private/reserved, or doesn't resolve. A literal
 *  public IP is returned as-is. Use the returned IP to PIN a plain-TCP connect so
 *  a later DNS flip can't redirect it into the LAN. */
export async function resolvePublicIp(host: string): Promise<string> {
  const h = host.trim().replace(/^\[|\]$/g, '');
  if (!h) throw new Error('empty host');
  if (isIP(h)) {
    if (isPrivateOrReservedIp(h)) throw new Error(`refusing to use non-public address ${h}`);
    return h;
  }
  let addrs: Array<{ address: string; family: number }>;
  try {
    addrs = await lookup(h, { all: true });
  } catch (e) {
    throw new Error(`could not resolve host '${h}': ${(e as Error).message}`);
  }
  if (!addrs.length) throw new Error(`host '${h}' did not resolve to any address`);
  for (const a of addrs) {
    if (isPrivateOrReservedIp(a.address)) {
      throw new Error(`host '${h}' resolves to a private/reserved address (${a.address}) — not allowed for self-service shards`);
    }
  }
  return addrs[0].address; // every record verified public; pin the first
}

/** Throw if `host` resolves to anything private/reserved (registration + probe
 *  gate). Does not pin — callers that connect by hostname (https/cert) use this. */
export async function assertResolvesPublic(host: string): Promise<void> {
  await resolvePublicIp(host);
}
