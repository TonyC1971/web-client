// v0.3.14: shard-banned cool-down mode.
//
// When the proxy opens a TCP socket to an upstream shard and the shard
// FINs within `IMMEDIATE_FIN_WINDOW_MS` of the connect AND the proxy hadn't
// yet sent any client bytes, that's an unmistakeable per-IP block at the
// shard side: the docker bridge IP got onto an in-memory deny list (Sphere
// `MaxConnectRequestsPerIP` / ModernUO connect throttle / a custom
// f_onserver_connectreq_ex script — pick your engine). Continuing to
// hammer reconnects only extends the ban duration on the shard side
// (Sphere's NetTTL grows on every blocked attempt).
//
// On detection, the affected slug enters cool-down for COOLDOWN_MS:
//   - Future WS upgrades for that slug are rejected with 1013 + reason
//     "shard temporarily unreachable, retry in 5 minutes".
//   - The picker `/api/servers` payload reports `online: false,
//     reason: 'shard-blocked-proxy'` so the card greys out cleanly.
//   - A single TCP probe runs every PROBE_INTERVAL_MS; first non-immediate
//     -FIN lifts the cool-down.
//
// Pairs with v0.3.15 WebIdentity, where cooperative shards see the real
// per-user IP and never trip per-IP blocks in the first place.
//
// State is in-memory only — survives nothing. A proxy restart clears
// every cool-down. That's deliberate: the operator's manual restart path
// (e.g. after raising MaxPings on the shard) implicitly resets the
// view, which is what they want.

import * as net from 'net';
import { getServer } from './serverRegistry.js';
import { resolvePublicIp } from './netGuard.js';

/** ms threshold that defines an "immediate" FIN — sub-window means the
 *  shard rejected before the proxy could send anything. Real FIN-on-
 *  application-error happens after at least one round trip + parse, so
 *  50 ms is a safe floor. */
const IMMEDIATE_FIN_WINDOW_MS = 50;
/** Cool-down duration after an immediate FIN. 5 min is short enough that
 *  the picker recovers without operator intervention but long enough that
 *  Sphere's NetTTL window typically expires (default ~3-5 min). */
const COOLDOWN_MS = 5 * 60 * 1000;
/** Probe cadence while in cool-down. */
const PROBE_INTERVAL_MS = 60 * 1000;
/** TCP probe connect timeout — give up after this and try again next tick. */
const PROBE_CONNECT_TIMEOUT_MS = 5_000;

interface CooldownEntry {
  /** ms timestamp when the cool-down started. */
  startedAt: number;
  /** ms timestamp when the cool-down auto-expires (lift if probe doesn't
   *  pass first). Re-extended on every immediate-FIN observation. */
  expiresAt: number;
  /** Most recent detection — used in log line + diag. */
  triggerCount: number;
  /** Active probe timer so we don't double-schedule. */
  probeTimer: NodeJS.Timeout | null;
}

const cooldowns = new Map<string, CooldownEntry>();

/** Per-session connect state — track when the TCP open started and whether
 *  any c2s bytes have been forwarded yet. Sessions register themselves on
 *  open; the proxy calls `recordC2sBytes` whenever it forwards a frame. */
interface SessionState {
  slug: string;
  connectAt: number;
  c2sBytes: number;
  /** Did the SHARD close on us? Set only by a peer FIN or an RST. */
  peerClosed: boolean;
}
const sessions = new WeakMap<net.Socket, SessionState>();

/** Register a freshly opened upstream socket so we can decide later whether
 *  its FIN was "immediate". Called from Session.connectTcp(). */
export function registerUpstreamConnect(socket: net.Socket, slug: string): void {
  sessions.set(socket, { slug, connectAt: Date.now(), c2sBytes: 0, peerClosed: false });
}

/**
 * 🚨 THE SHARD CLOSED ON US — not us on the shard. Without this distinction the tracker cannot
 * tell a per-IP block from a player closing their browser tab.
 *
 * `recordImmediateFin` fires from the socket's `close`, which happens whoever hung up. And the
 * proxy hangs up on the upstream the moment the WebSocket goes away (`ws.on('close')` → tcpSocket
 * .end()). So a client that opened a session and closed it again within fifty milliseconds
 * produced: elapsed under the window, c2sBytes zero — the exact signature of a shard-side block.
 * One such session put the WHOLE SLUG into a five-minute cool-down, refusing every OTHER player's
 * WS upgrade and greying the card in the picker, and every repeat re-extended it.
 *
 * That is a denial of service against everyone else, from any session, needing nothing but an
 * abort right after connecting — and the same accident happens to an honest player on a flaky
 * link, which is why it is a correctness bug as well as an exploit.
 *
 * Node already carries the signal: `end` fires when the PEER sends FIN, and a reset surfaces as an
 * ECONNRESET error. Both mean the far side went away. A local `.end()` produces neither.
 */
export function recordPeerClose(socket: net.Socket): void {
  const s = sessions.get(socket);
  if (s) s.peerClosed = true;
}

/** Called per c2s frame so the cool-down detection can distinguish
 *  "shard FINed before we sent anything" (= per-IP block) from
 *  "shard FINed mid-session" (= application close). */
export function recordC2sBytes(socket: net.Socket, n: number): void {
  const s = sessions.get(socket);
  if (s) s.c2sBytes += n;
}

/**
 * Inspect a closing upstream socket. If the close happened within the
 * immediate-FIN window AND no c2s bytes flowed, mark the slug as
 * cooling-down.
 */
export function recordImmediateFin(socket: net.Socket): void {
  const s = sessions.get(socket);
  if (!s) return;
  sessions.delete(socket);
  // 🚨 ONLY WHEN THE SHARD HUNG UP. See recordPeerClose: this runs from the socket's `close`,
  // which fires whoever closed it — including us, the moment the player's WebSocket goes away.
  // Without this line one client opening and immediately abandoning a session greyed the shard
  // out for every other player for five minutes, repeatably.
  if (!s.peerClosed) return;
  const elapsed = Date.now() - s.connectAt;
  if (elapsed > IMMEDIATE_FIN_WINDOW_MS) return;
  if (s.c2sBytes > 0) return;
  enterCooldown(s.slug);
}

function enterCooldown(slug: string): void {
  const now = Date.now();
  const existing = cooldowns.get(slug);
  if (existing) {
    existing.triggerCount++;
    existing.expiresAt = now + COOLDOWN_MS;
    return;
  }
  const entry: CooldownEntry = {
    startedAt: now,
    expiresAt: now + COOLDOWN_MS,
    triggerCount: 1,
    probeTimer: null,
  };
  cooldowns.set(slug, entry);
  console.warn(
    `[ShardCooldown] [${slug}] entering cool-down — upstream FIN <${IMMEDIATE_FIN_WINDOW_MS}ms ` +
    `with c2sBytes=0 (per-IP block on shard side likely). New WS upgrades for this ` +
    `slug will be refused for ${COOLDOWN_MS / 1000}s; probing every ${PROBE_INTERVAL_MS / 1000}s.`
  );
  scheduleProbe(slug);
}

function scheduleProbe(slug: string): void {
  const entry = cooldowns.get(slug);
  if (!entry) return;
  if (entry.probeTimer) return;
  entry.probeTimer = setTimeout(() => {
    entry.probeTimer = null;
    runProbe(slug);
  }, PROBE_INTERVAL_MS);
  entry.probeTimer.unref?.();
}

function runProbe(slug: string): void {
  const entry = cooldowns.get(slug);
  if (!entry) return;
  if (Date.now() >= entry.expiresAt) {
    // Hard timeout — lift even without a successful probe so a permanently
    // blocked shard doesn't pin a slug grey forever.
    console.log(`[ShardCooldown] [${slug}] hard expiry — lifting cool-down`);
    cooldowns.delete(slug);
    return;
  }
  const target = getServer(slug);
  if (!target) {
    cooldowns.delete(slug);
    return;
  }
  const sock = new net.Socket();
  let resolved = false;
  const finish = (immediateFin: boolean): void => {
    if (resolved) return;
    resolved = true;
    try { sock.destroy(); } catch { /* ignore */ }
    if (!immediateFin) {
      console.log(`[ShardCooldown] [${slug}] probe ok — lifting cool-down (was held ${Math.round((Date.now() - entry.startedAt) / 1000)}s)`);
      cooldowns.delete(slug);
    } else {
      scheduleProbe(slug);
    }
  };
  const t0 = Date.now();
  sock.setTimeout(PROBE_CONNECT_TIMEOUT_MS);
  sock.once('connect', () => {
    // Probe-mode: check whether the socket FINs within the window without
    // us sending bytes. If it stays alive past the window, the shard's
    // per-IP block has lifted. Either way, close the socket cleanly so we
    // don't trip whatever per-IP counter is in play.
    setTimeout(() => finish(false), IMMEDIATE_FIN_WINDOW_MS + 5);
  });
  sock.once('end', () => {
    finish(Date.now() - t0 <= IMMEDIATE_FIN_WINDOW_MS);
  });
  sock.once('error', () => finish(true));
  sock.once('timeout', () => finish(true));
  // SSRF / DNS-rebind (audit 2026-06-21): for an UNTRUSTED owner-controlled host
  // (self-service, or a non-self-service shard whose host an owner-tier editor set
  // → untrustedHost), re-resolve + PIN a verified-public IP at probe time (the
  // bridge pins, this path did not). If it now resolves private/reserved (a
  // rebind), never connect — treat as still-blocked. Operator shards never touched
  // by an owner reach internal hosts intentionally.
  void (async () => {
    let connectHost = target.host;
    if (target.selfService || target.untrustedHost) {
      try { connectHost = await resolvePublicIp(target.host); }
      catch { finish(true); return; }
    }
    if (!resolved) { sock.connect(target.port, connectHost); }
  })();
}

/** Returns the cool-down entry for a slug, or null. */
export function isShardCoolingDown(slug: string): CooldownEntry | null {
  const e = cooldowns.get(slug);
  if (!e) return null;
  if (Date.now() >= e.expiresAt && !e.probeTimer) {
    cooldowns.delete(slug);
    return null;
  }
  return e;
}
