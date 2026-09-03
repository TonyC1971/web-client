// UOAM-style opt-in position-sharing hub over WebSocket.
//
// STATUS: WIRED — attached in index.ts on path /uoam, sharing the proxy's
// http.Server with the game /ws upgrade (both early-return for paths they don't
// own, so they coexist). Pure WS relay (no TCP, no game-server/engine
// involvement); positions come from the client (rail getPlayer bridge).
// The hub lives in the proxy rather than the shard so it works against any UO server.
//
// Wiring (when placement is decided), in the server entry that owns the
// http.Server (alongside the existing game /ws upgrade handler):
//
//     import { attachUoamHub } from './uoamHub';
//     attachUoamHub(httpServer, {
//       path: '/uoam',
//       // reuse the proxy's JWT check; return null to reject. slug = shard.
//       verifyJWT: (req) => { const c = parseJwtCookie(req); return c ? { slug: c.slug } : null; },
//     });
//
// The existing game-/ws upgrade handler MUST early-return for paths it doesn't
// own (and this one early-returns for non-/uoam), so both can coexist.

import { WebSocketServer, WebSocket } from 'ws';
import crypto from 'node:crypto';
import type { Server, IncomingMessage } from 'node:http';
import type { Duplex } from 'node:stream';
import { clientIpFromReq } from './clientIp.js';

interface Peer {
  id: string;
  ws: WebSocket;
  room: string;
  name: string;
  x: number;
  y: number;
  map: number;
  hue: number;
  lastPos: number; // ms timestamp of last accepted pos (rate limit)
}

const rooms = new Map<string, Map<string, Peer>>();
const MAX_ROOM = 200; // hard cap per room
const MAX_ROOMS = 500; // hard cap on distinct rooms (DoS: join-flood with random pw)
const MAX_PER_IP = 8; // max concurrent /uoam sockets per client IP
const POS_MIN_MS = 180; // min interval between accepted pos updates per peer
const JOIN_MIN_MS = 1000; // min interval between accepted joins per socket (anti join-flood)
const MAX_MSG_BYTES = 4096; // hard cap per WS frame — pos/join JSON is tiny; blunts large-message memory DoS
const ipConns = new Map<string, number>(); // live socket count per IP

function roomKey(slug: string, pw: string): string {
  // shard-scoped + password "domain": only same-shard + same-password peers meet.
  const h = crypto.createHash('sha256').update(pw || '').digest('hex').slice(0, 16);
  return `${slug}:${h}`;
}

function send(ws: WebSocket, obj: unknown): void {
  try { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)); } catch { /* ignore */ }
}

function peerView(p: Peer) {
  return { id: p.id, name: p.name, x: p.x, y: p.y, map: p.map, hue: p.hue };
}

function leave(p: Peer): void {
  const room = rooms.get(p.room);
  if (!room) return;
  room.delete(p.id);
  for (const other of room.values()) send(other.ws, { t: 'leave', id: p.id });
  if (room.size === 0) rooms.delete(p.room);
}

function clampInt(v: unknown, lo: number, hi: number): number {
  const n = Math.trunc(Number(v));
  return Number.isFinite(n) ? Math.max(lo, Math.min(hi, n)) : 0;
}

/**
 * The one player-controlled STRING this hub relays, cleaned where it passes rather than where it
 * lands.
 *
 * 🚨 THE DELIMITERS ARE THE POINT. The rail feeds peers to the in-game world map as one packed
 * string, `name \x1f x \x1f y \x1f map`, joined by `\x1e`. A name carrying those bytes turns one
 * peer into several in somebody else's marker list. Today's client strips them before packing and
 * escapes the name for the panel, so this is not a live hole — it is defence moved to the right
 * place: the hub is ONE server serving EVERY client version, including a stale cached bundle from
 * before those two lines existed, and a player can pick this string.
 *
 * Same argument the points module makes for refusing guests in the primitive rather than at
 * ninety-three call sites: a rule every consumer must remember is a rule a consumer will forget.
 * Nothing legitimate is lost — a character name has no control characters in it.
 */
export function sanitizePeerName(raw: unknown): string {
  const cleaned = String(raw ?? '')
    // eslint-disable-next-line no-control-regex
    .replace(/[\x00-\x1f\x7f]/g, ' ')   // control chars, which includes the \x1e/\x1f separators
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 32);
  return cleaned || 'player';
}

function onConnection(ws: WebSocket, slug: string, releaseIpSlot: () => void, maxRoom: number): void {
  let peer: Peer | null = null;
  let lastJoin = 0; // anti join-flood: each re-join broadcasts a 'leave' to the room
  // v0.9.132 (audit 2026-06-21): the per-IP slot is now RESERVED synchronously in
  // the upgrade handler BEFORE the async handshake (see attachUoamHub) — not
  // incremented here — so a burst of concurrent upgrades from one IP can't all read
  // the pre-increment count and blow past MAX_PER_IP. cleanup just releases it.
  let released = false;
  const cleanup = (): void => {
    if (released) return;
    released = true;
    if (peer) { leave(peer); peer = null; }
    releaseIpSlot();
  };
  ws.on('message', (raw) => {
    // Hard size cap BEFORE String()/JSON.parse — a peer's pos/join JSON is tiny;
    // reject oversized frames so a 100 MB message can't spike memory.
    try { if ((raw as Buffer).length > MAX_MSG_BYTES) return; } catch { /* ignore */ }
    let d: any;
    try { d = JSON.parse(String(raw)); } catch { return; }
    if (!d || typeof d.t !== 'string') return;

    if (d.t === 'join') {
      // Anti join-flood: each re-join broadcasts a 'leave' to up to MAX_ROOM
      // peers, so an unthrottled join loop amplifies to the whole room.
      const tnow = Date.now();
      if (tnow - lastJoin < JOIN_MIN_MS) return;
      lastJoin = tnow;
      const key = roomKey(slug, typeof d.pw === 'string' ? d.pw : '');
      // 🚨 DECIDE BEFORE MUTATING. `leave(peer)` used to run first, so a join that then hit a cap
      // left the peer removed from its old room — and broadcast a `leave` for it — while this
      // socket still held a `peer` pointing at that room. The socket keeps sending `pos`, which is
      // relayed to the room it was just declared gone from (a marker that resurrects after its own
      // leave), and the ghost occupies no slot in `room.size`, so it broadcasts past MAX_ROOM.
      // Same shape as the reclaim-first rule the market learned: a refusal must leave the state it
      // found, so failing is a no-op instead of a half-move.
      const existing = rooms.get(key);
      if (!existing && rooms.size >= MAX_ROOMS) { send(ws, { t: 'error', error: 'too many rooms' }); return; }
      if (existing && existing.size >= maxRoom) { send(ws, { t: 'error', error: 'room full' }); return; }
      if (peer) leave(peer); // the join is guaranteed now: re-join = move rooms
      // Re-read: if this peer was the last member of `key`, leave() just deleted the room.
      let room = rooms.get(key);
      if (!room) { room = new Map(); rooms.set(key, room); }
      peer = {
        id: crypto.randomUUID(), ws, room: key,
        name: sanitizePeerName(d.name),
        x: 0, y: 0, map: 0, hue: 0, lastPos: 0,
      };
      room.set(peer.id, peer);
      // snapshot of everyone already here
      send(ws, { t: 'peers', peers: [...room.values()].filter((o) => o.id !== peer!.id).map(peerView) });
      return;
    }

    if (d.t === 'pos' && peer) {
      const now = Date.now();
      if (now - peer.lastPos < POS_MIN_MS) return; // rate limit
      peer.lastPos = now;
      peer.x = clampInt(d.x, 0, 7168);
      peer.y = clampInt(d.y, 0, 4096);
      peer.map = clampInt(d.map, 0, 5);
      peer.hue = clampInt(d.hue, 0, 0xffff);
      const room = rooms.get(peer.room);
      if (!room) return;
      const msg = { t: 'pos', ...peerView(peer) };
      for (const other of room.values()) if (other.id !== peer.id) send(other.ws, msg);
      return;
    }

    if (d.t === 'leave' && peer) { leave(peer); peer = null; }
  });
  ws.on('close', cleanup);
  ws.on('error', cleanup);
}

export interface UoamHubOptions {
  path?: string;
  /** Validate the upgrade request; return the shard slug (or null to reject). */
  verifyJWT?: (req: IncomingMessage) => { slug: string } | null;
  /**
   * Cap override, for tests only — production passes nothing and gets MAX_ROOM.
   *
   * The room cap is 200 and MAX_PER_IP caps one host at 8 sockets, so the "room full" branch is
   * unreachable from a single machine: a test cannot fill a room, and a refusal path nothing can
   * reach is a refusal path nothing can check. This seam is the difference between a gate and a
   * comment claiming there is one.
   */
  limits?: { maxRoom?: number };
}

export interface UoamAuthDeps {
  /** CSWSH guard — the same Origin allow-list the game /ws upgrade uses. */
  isAllowedOrigin: (req: IncomingMessage) => boolean;
  /** The signed session, or null. */
  verifySession: (req: IncomingMessage) => { sub?: unknown } | null;
  /** Which shard this player is ACTUALLY connected to right now, or null. */
  liveSlugForSub: (sub: string) => string | null;
}

/**
 * Who may join, and — the part that was wrong — WHICH ROOM.
 *
 * 🚨 THE SHARD HALF OF THE ROOM KEY WAS SELF-ASSERTED. roomKey is `slug:sha256(pw)` and the file
 * says what that is for: "only same-shard + same-password peers meet". The password half is a real
 * secret. The shard half was read straight off the caller's own query string
 * (`?slug=`), so any signed-in user could type another shard's slug and join its rooms — and with
 * the default empty password that is the room every map user on that shard shares.
 *
 * Position sharing is opt-in, so those players chose to broadcast. They chose to broadcast to
 * their SHARD, which is the scope the code promises and did not enforce.
 *
 * The authoritative answer is one the proxy already has: /uoam is served by the GAME process, which
 * is the one holding the live-session map, so it can simply be asked which shard this player is on.
 * No live session means no shard room to be in — and that is not a hardship, because the client's
 * Connect button is already gated on being in-world ("World Map is available in-world").
 *
 * Extracted from the index.ts wiring so the decision can be tested; a predicate defined inline in a
 * server bootstrap is a predicate nothing can exercise.
 */
export function resolveUoamAuth(req: IncomingMessage, deps: UoamAuthDeps): { slug: string } | null {
  if (!deps.isAllowedOrigin(req)) return null;
  const payload = deps.verifySession(req);
  if (!payload) return null;                       // anonymous upgrades are refused
  const sub = typeof payload.sub === 'string' ? payload.sub : '';
  if (!sub) return null;
  const slug = deps.liveSlugForSub(sub);
  if (!slug) return null;                          // not in-world: no shard room to join
  return { slug };
}

export function attachUoamHub(server: Server, opts: UoamHubOptions = {}): void {
  const path = opts.path || '/uoam';
  const maxRoom = Math.max(1, Math.min(MAX_ROOM, opts.limits?.maxRoom ?? MAX_ROOM));
  // maxPayload: reject oversized frames at the ws protocol level (before the
  // full payload is buffered) — pos/join JSON is tiny. The game /ws sets the
  // same kind of cap; the UOAM hub previously didn't.
  const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_MSG_BYTES });
  server.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    let url: URL;
    try { url = new URL(req.url || '/', 'http://localhost'); } catch { return; }
    if (url.pathname !== path) return; // not ours — let the game /ws handler take it
    let slug = url.searchParams.get('slug') || 'default';
    if (opts.verifyJWT) {
      const auth = opts.verifyJWT(req);
      if (!auth) { try { socket.destroy(); } catch { /* ignore */ } return; }
      slug = auth.slug || slug;
    }
    // Real client IP via the canonical resolver (audit 2026-06-21): only trusts
    // X-Forwarded-For when TRUST_PROXY_HOPS>0 and takes the LAST (nginx-appended)
    // hop, failing closed to the socket address — so a logged-in user can't spoof
    // the XFF first-entry to make every connection look like a fresh IP and defeat
    // the per-IP cap. The cap is this hub's main connection-flood DoS control.
    const ip = clientIpFromReq(req);
    // v0.9.132 (audit 2026-06-21): RESERVE the per-IP slot synchronously, BEFORE the
    // async handleUpgrade handshake. Previously the count was only bumped inside
    // onConnection (post-handshake), so N concurrent upgrades from one IP all
    // observed the same pre-increment count and every one passed the MAX_PER_IP gate
    // → unbounded sockets per IP (the cap is this hub's main flood control). Mirrors
    // UOProxy's verifyClient slot reservation. release() is idempotent and fires
    // from onConnection's cleanup on a live socket OR from the socket-close backstop
    // if the handshake aborts before onConnection ever runs.
    const cur = ipConns.get(ip) || 0;
    if (cur >= MAX_PER_IP) { try { socket.destroy(); } catch { /* ignore */ } return; }
    ipConns.set(ip, cur + 1);
    let releasedSlot = false;
    const releaseIpSlot = (): void => {
      if (releasedSlot) return;
      releasedSlot = true;
      const n = (ipConns.get(ip) || 1) - 1;
      if (n <= 0) ipConns.delete(ip); else ipConns.set(ip, n);
    };
    socket.once('close', releaseIpSlot); // backstop: handshake aborts before onConnection
    try {
      wss.handleUpgrade(req, socket as any, head, (ws) => onConnection(ws as unknown as WebSocket, slug, releaseIpSlot, maxRoom));
    } catch { releaseIpSlot(); try { socket.destroy(); } catch { /* ignore */ } }
  });
}
