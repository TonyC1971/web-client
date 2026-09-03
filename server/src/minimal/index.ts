/**
 * Entrypoint for a MINIMAL self-hosted install.
 *
 * The uonexus entrypoint (../index.ts) boots AssetServer, which is the product: cards, market,
 * cosmetics, minigames, achievements, admin. This one boots the reduced server instead, and
 * attaches the two pieces that are genuinely shared and genuinely generic — the UO protocol relay
 * and the world-map position hub.
 *
 * 🚨 IT REGISTERS NOTHING FROM THE ECONOMY, and the omissions are the design rather than an
 * oversight. ../index.ts registers the Discord welcome card and (via cardsdb's own module load) the
 * card eraser. Neither is imported here, so neither exists: logins grant nothing, and account
 * deletion erases exactly the stores this build actually has. MinimalBackendCarriesNoProduct walks
 * the transitive import graph from here to keep that true.
 */
import { startMinimalServer } from './minimalServer.js';
import { UOProxy, isAllowedOrigin } from '../shared/UOProxy.js';
import { attachUoamHub, resolveUoamAuth } from '../shared/uoamHub.js';
import { verifyRequestJwt } from '../shared/auth.js';
import { loadRegistry, registrySize, defaultSlug } from '../shared/serverRegistry.js';
import { liveSlugForSub } from '../shared/proxyStats.js';

const PORT = Number(process.env.PORT || 3000);
const SERVERS_DIR = process.env.SERVERS_DIR || '/app/servers';

// The shard registry, before anything can serve /api/servers. A self-hosted install has exactly one
// entry; an empty directory is a configuration mistake worth saying out loud rather than answering
// an empty list and letting the client fail later with "no shards".
loadRegistry(SERVERS_DIR);
if (registrySize() === 0) {
  console.error(`[minimal] no shard configured in ${SERVERS_DIR}. Copy servers/example.yaml.example`);
  console.error('[minimal] to servers/<your-slug>.yaml and set its host/port, then restart.');
}

const httpServer = startMinimalServer(PORT);

// The UO protocol relay on /ws. Identical to the full build: this is the piece a self-hoster is
// actually here for, and there is no reduced version of it.
new UOProxy(httpServer);

// Shared world-map positions on /uoam, for the rail's World Map panel. The room's shard half comes
// from the LIVE SESSION rather than the query string the client sends, so a client cannot place
// itself in another shard's room by asking.
attachUoamHub(httpServer, {
  path: '/uoam',
  verifyJWT: (req) => resolveUoamAuth(req, {
    isAllowedOrigin,
    verifySession: verifyRequestJwt,
    liveSlugForSub: (sub) => liveSlugForSub(sub),
  }),
});

// 🚨 Fail closed on unrouted upgrades. UOProxy and the uoam hub each early-return for paths they do
// not own, so a WS upgrade to any OTHER path matches NEITHER: the socket is left dangling with no
// destroy and no handshake timeout, an unauthenticated pre-handshake socket drip that only OS
// keepalive eventually clears. nginx forwards Upgrade for /ws and /uoam only, so this is
// unreachable from the edge — but a self-hoster may well expose this port directly, which is
// exactly the case the full build never has to survive. Registered LAST so the two real handlers
// run first.
httpServer.on('upgrade', (req, socket) => {
  let pathname = '';
  try { pathname = new URL(req.url || '/', 'http://localhost').pathname; } catch { /* ignore */ }
  if (pathname !== '/ws' && pathname !== '/uoam') {
    try { socket.destroy(); } catch { /* already gone */ }
  }
});

console.log(`[minimal] ready — shard=${defaultSlug() || '(none)'} registry=${registrySize()}`);

function shutdown(sig: string): void {
  console.log(`[minimal] ${sig} — closing`);
  httpServer.close(() => process.exit(0));
  // Do not hang forever on a socket that will not close: players hold long-lived WS connections by
  // design, and close() waits for every one of them.
  setTimeout(() => process.exit(0), 5_000).unref();
}
process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));
