// banSweep.ts — re-check LIVE sessions against the ban list, in the process that holds them.
//
// 🚨 THE GAP THIS CLOSES. Banning somebody has two halves: refuse their next connection, and cut
// the one they are on. The upgrade gate does the first (it calls findActiveBan per WebSocket
// upgrade, and the registry re-reads when the file moves, so a ban added by the WEB process is
// seen by the GAME one). Nothing did the second. `closeMatching` exists and handles ipCidr
// correctly, but it is invoked from the POST /api/admin/bans handler — which lives in the web
// process, where there are no sessions. So an IP ban left the offender connected and playing until
// they chose to disconnect.
//
// 🚨 AND THE OBVIOUS FIX WAS THE WRONG ONE. The recorded plan was to publish live-session IPs to
// the shared table so the web process could match them, and it was parked because putting player
// IP addresses in a new shared table is a privacy surface that did not exist before. Inverting it
// costs nothing: the ban RULE is small and not sensitive, and both processes already read it from
// the same file. So the sensitive data never moves — the sweep runs where the IPs already are.
//
// Deliberately a plain function over a session-shaped interface rather than a method on the proxy:
// it makes the matching testable against the real ban registry without standing up a WebSocket
// server, which is what let this ship with a behavioural gate instead of a source scan.

import { findActiveBan, type BanEntry } from './banRegistry.js';

/** The only three things the sweep needs from a live session. */
export interface SweepableSession {
  /** Discord JWT sub captured at upgrade; null for guests. */
  readonly discordSub: string | null;
  /** Per-handle remote address, as the upgrade gate saw it. */
  readonly remoteAddr: string;
  destroy(): void;
}

/**
 * Close every live session covered by an active ban. Returns what was closed, so the caller can
 * log it — a kick with no trace is indistinguishable from a crash, from the player's side and from
 * the operator's.
 *
 * 🚨 THE LIST IS SNAPSHOTTED BY THE CALLER'S ITERABLE, not iterated live: `destroy()` synchronously
 * fires the close handler that removes the session from the proxy's Set, and mutating a Set during
 * a for…of makes V8's iterator skip the next entry — with 100 matching sessions the naive loop
 * closes about half. That exact bug was found here once already (v0.3.23), so the array copy is
 * taken inside rather than trusted to every caller.
 */
export function sweepBannedSessions(
  sessions: Iterable<SweepableSession>,
  onKick?: (session: SweepableSession, ban: BanEntry) => void,
): number {
  let closed = 0;
  for (const session of Array.from(sessions)) {
    // Both keys in one call: findActiveBan checks discordId first, then walks the CIDR list. A
    // guest has no sub, which is exactly why the IP arm has to be here — it is the only handle
    // that reaches them.
    const ban = findActiveBan({ discordId: session.discordSub, ip: session.remoteAddr });
    if (!ban) continue;
    onKick?.(session, ban);
    try { session.destroy(); } catch { /* already gone */ }
    closed++;
  }
  return closed;
}
