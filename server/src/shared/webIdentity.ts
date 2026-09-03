// v0.3.15: WebIdentity 0xA4 frame builder.
//
// Mirrors the upstream public spec at
//   https://github.com/ClassicUO/packets/blob/main/WebIdentity.ksy
// and the RunUO-like reference impl at
//   https://github.com/ClassicUO/packets/tree/main/implementations/RunUO-like
// vendored under docs/vendor/classicuo-packets/.
//
// Layout (149 bytes total):
//   [0]      0xA4                     packet ID
//   [1..6]   "CUOWEB" (6 ASCII bytes) client_type, fixed-width
//   [7]      0x01                     version
//   [8..11]  uint32 BE                timestamp (unix seconds)
//   [12+]    seven null-terminated UTF-8 strings:
//              secret, userId, connectingIp, externalAuthProvider,
//              externalAuthUsername, externalAuthId, role
//   [tail]   zero padding to 149 bytes total
//
// Strings are written as `<bytes>0x00`. Total length is fixed at 149
// because the underlying SystemInfo packet that 0xA4 reuses is 149
// bytes — emulators register it that way and reject other lengths.

import * as net from 'net';

export const WEB_IDENTITY_PACKET_ID = 0xa4;
export const WEB_IDENTITY_CLIENT_TYPE = 'CUOWEB';
export const WEB_IDENTITY_VERSION = 0x01;
export const WEB_IDENTITY_TOTAL_BYTES = 149;
const WEB_IDENTITY_BODY_BYTES = WEB_IDENTITY_TOTAL_BYTES - 1; // 148

const FIXED_HEADER_BYTES =
  6 +  // client_type
  1 +  // version
  4;   // timestamp

export interface WebIdentityFields {
  /** ASCII shared secret, must match shard's `ClassicUO.WebIdentitySecret`
   *  (ServUO/ModernUO/RunUO) or `WebIdentitySecret` (Sphere sphere.ini). */
  secret: string;
  /** Stable per-user identifier. Discord-authed → snowflake. Guests →
   *  per-session token. */
  userId: string;
  /** Real client IP as a textual address ("203.0.113.5" or "2001:db8::1"). */
  connectingIp: string;
  /** Auth provider name. "Discord" for OAuth users, "" for guests. */
  externalAuthProvider?: string;
  /** Auth display username, e.g. "blank#9244" for Discord. "" for guests. */
  externalAuthUsername?: string;
  /** Auth account id, e.g. Discord ID "100000000000000002". "" for guests. */
  externalAuthId?: string;
  /** "user" | "admin" | "shard-owner" | "mini-player" | "mini-guest". Default
   *  "user". "mini-player" (Discord) and "mini-guest" (guest) flag a mini
   *  auto-login connection (eternal's Custom/ClassicUO/MiniAutoLogin.cs acts ONLY
   *  on those two roles). */
  role?: 'user' | 'admin' | 'shard-owner' | 'mini-player' | 'mini-guest';
  /** Override clock for tests. Default = current Unix seconds. */
  timestampSec?: number;
}

/**
 * Build the 149-byte 0xA4 WebIdentity preamble. Pure function — no I/O.
 *
 * Throws if the fields don't fit (the 7 strz fields plus terminators must
 * total ≤ 137 bytes, which is the body minus the 11-byte fixed header).
 * In practice fields are short (Discord IDs ~18 chars, IPs ~15 chars,
 * roles ≤ 11 chars) so this only fires on absurd input.
 */
/** Truncate a string so its UTF-8 encoding is at most `maxBytes`, without splitting a
 *  multi-byte codepoint. Returns '' for maxBytes <= 0. Used to fit the display username
 *  into whatever the fixed 149-byte WebIdentity frame has left after the other fields. */
function truncateUtf8(s: string, maxBytes: number): string {
  if (maxBytes <= 0) return '';
  const b = Buffer.from(s, 'utf8');
  if (b.length <= maxBytes) return s;
  let end = maxBytes;
  // Back off any UTF-8 continuation bytes (0b10xxxxxx) so we cut on a codepoint boundary.
  while (end > 0 && (b[end] & 0xc0) === 0x80) end--;
  return b.toString('utf8', 0, end);
}

export function buildWebIdentityFrame(fields: WebIdentityFields): Buffer {
  const buf = Buffer.alloc(WEB_IDENTITY_TOTAL_BYTES); // zero-filled padding
  buf[0] = WEB_IDENTITY_PACKET_ID;

  // client_type — fixed 6 bytes ASCII. "CUOWEB" is exactly 6 chars; no
  // trailing null needed (the field is fixed-width per the .ksy spec).
  buf.write(WEB_IDENTITY_CLIENT_TYPE, 1, 6, 'ascii');

  buf[7] = WEB_IDENTITY_VERSION;

  const ts = fields.timestampSec ?? Math.floor(Date.now() / 1000);
  buf.writeUInt32BE(ts >>> 0, 8);

  // strz fields. Write each string + a zero terminator into the body
  // sequentially. The Buffer is already zero-filled so the trailing
  // padding (after the last string) is implicitly correct; we only need
  // to track our write offset.
  let offset = 1 + FIXED_HEADER_BYTES; // = 12

  // FIT THE DISPLAY USERNAME (2026-07-02): the frame is FIXED at 149 bytes, so the 7 strz
  // fields + their terminators must fit in STRZ_BUDGET bytes of string content. A mini-player
  // (Discord) frame populates provider/id/username + the longer "mini-player" role that guests
  // leave empty, and a long Discord display name would push it over — the builder used to THROW
  // and the proxy then sent NO 0xA4 preamble at all, so MiniAutoLogin never ran and the account
  // was stuck at an empty character list (operator repro: d<snowflake> looping at char-list,
  // "combined strz fields exceed body capacity … 1 more"). externalAuthUsername is a DISPLAY-only
  // field (MiniAutoLogin sanitizes + caps the char name to 16 chars regardless), so truncate it
  // to whatever budget the other fields leave, rather than dropping the whole identity frame.
  const STRZ_BUDGET = WEB_IDENTITY_BODY_BYTES - FIXED_HEADER_BYTES - 7; // 130 bytes for string content (7 terminators reserved)
  const secretS = fields.secret ?? '';
  const userIdS = fields.userId ?? '';
  const ipS = fields.connectingIp ?? '';
  const providerS = fields.externalAuthProvider ?? '';
  const idS = fields.externalAuthId ?? '';
  const roleS = fields.role ?? 'user';
  const otherBytes =
    Buffer.byteLength(secretS, 'utf8') + Buffer.byteLength(userIdS, 'utf8') +
    Buffer.byteLength(ipS, 'utf8') + Buffer.byteLength(providerS, 'utf8') +
    Buffer.byteLength(idS, 'utf8') + Buffer.byteLength(roleS, 'utf8');
  const usernameS = truncateUtf8(fields.externalAuthUsername ?? '', STRZ_BUDGET - otherBytes);

  const fieldsInOrder = [
    secretS,
    userIdS,
    ipS,
    providerS,
    usernameS,
    idS,
    roleS,
  ];

  for (const s of fieldsInOrder) {
    const written = buf.write(s, offset, 'utf8');
    offset += written;
    // Null terminator. If we ran out of body space, this byte landed at
    // (or past) the end of the 149-byte buffer — signal the caller.
    if (offset >= WEB_IDENTITY_TOTAL_BYTES) {
      throw new Error(
        `WebIdentity: combined strz fields exceed body capacity ` +
        `(${WEB_IDENTITY_BODY_BYTES} bytes available; need at least ` +
        `${offset - WEB_IDENTITY_TOTAL_BYTES + 1} more for the last terminator)`,
      );
    }
    buf[offset] = 0x00;
    offset += 1;
  }

  return buf;
}

/**
 * Normalise an IP address read from `req.socket.remoteAddress` or
 * an XFF header into the textual form expected by the WebIdentity
 * `connecting_ip` field. Strips the `::ffff:` IPv4-mapped IPv6 prefix
 * Node uses on dual-stack sockets so the shard sees a clean v4 string
 * for v4 clients. Pass-through for v6 and already-clean v4.
 */
export function normaliseClientIp(raw: string | undefined | null): string {
  if (!raw) return '';
  const lc = raw.trim().toLowerCase();
  if (lc.startsWith('::ffff:') && net.isIPv4(raw.slice('::ffff:'.length))) {
    return raw.slice('::ffff:'.length);
  }
  return raw;
}
