// Which half of the proxy owns which route.
//
// Operator decision 2026-07-29: the web must not share a process with players
// ("no podemos compartir proxy con jugadores 100 %"). The reason is fault isolation,
// not throughput -- a runaway web handler takes the game relay down at one request per
// hour, and no percentile predicts that. See docs/PROXY_SPLIT.md.
//
// ONE BINARY, ONE IMAGE. The role is chosen by the PROXY_ROLE env var; the code is never
// forked. A 23k-line fork would diverge within weeks.
//
// TWO RULES DECIDE OWNERSHIP
//  1. Derived from LIVE WebSocket session state -> game. (Achievement grants are bound to
//     a live session on purpose: that binding IS the anti-cheat. Moving it weakens it.)
//  2. On the path to A PLAYER GETTING INTO THE GAME -> game, so that a dead or wedged web
//     process cannot stop anyone from connecting. That is the whole point of splitting.
// Everything else is derived from the database and belongs to web.
//
// MATCHING IS ON THE EXPRESS PATTERN, NOT THE URL
// Rules are compared against the pattern as registered (`/api/:game/live`), not against a
// runtime path. That makes ownership exact and reviewable: a route either matches a rule
// literally or it does not. Matching runtime URLs would make `/api/foo/live` ambiguous.
//
// ORDER MATTERS: first match wins, so exceptions come before the general prefix. The one
// that bites is /api/admin/sessions -- an admin route that reads LIVE session state, so it
// belongs to game even though every other /api/admin route belongs to web.

export type Role = 'all' | 'game' | 'web';

export const ROLE: Role = (() => {
  const raw = (process.env.PROXY_ROLE ?? 'all').trim().toLowerCase();
  return raw === 'game' || raw === 'web' ? raw : 'all';
})();

interface Rule {
  /** Matched against the Express pattern, as a prefix. */
  prefix: string;
  /** Which processes REGISTER the route. */
  owner: Exclude<Role, 'all'> | 'both';
  /** Which process nginx SENDS it to. Defaults to `owner`. A route owned by 'both' is
   *  registered everywhere but still has to be routed somewhere, so it must say where. */
  route?: Exclude<Role, 'all'>;
  /** How nginx should express the same thing. Kept beside the rule ON PURPOSE: if the two
   *  ever drift, nginx silently serves a route from the wrong process -- no error, just
   *  wrong state. Generating the nginx config from this table is what prevents that. */
  nginx: { kind: 'prefix' | 'regex'; value: string };
  why: string;
}

const RULES: Rule[] = [
  // --- exceptions first ---
  // 🚨 /api/admin/sessions WAS THE HEADLINE EXCEPTION HERE and is now on web, along with
  // /api/:game/live, /api/admin/metrics and /api/admin/metrics/purge-guests (2026-07-31).
  // None of the four is a player getting into a game, and the operator's rule for this
  // process is narrow: "el proxy game deberia ser unicamente para servir la version mas
  // basica del portal web, donde ves los servidores a los que hacer login y poder jugar en
  // ellos ... asi si se satura la otra parte web, no afecta al resto de jugadores que solo
  // quieren jugar realmente".
  //
  // They were stranded here by STATE, not by URL shape: proxyStats holds the live sessions in
  // memory and only the WebSocket handler fills it. So the STATE travels instead of the
  // routes — the game process publishes its map to a shared table every 2s and the web
  // process reads it; kicks go back the other way as requests, because closing a socket is an
  // ACTION and the sockets live here. See liveSessionsMirror.ts.
  //
  // purge-guests never needed the map at all: pure database work, swept along by the
  // /api/admin/metrics prefix. Its own small lesson about ruling by URL shape.
  //
  // 🚨 WHAT DID NOT MOVE, and must not: /api/metrics/report. Its anti-cheat gate reads the
  // REAL map, and a two-second-old copy is not an acceptable input to a gate that decides
  // whether playtime and achievements are credited.

  {
    prefix: '/api/servers/self',
    owner: 'web',
    nginx: { kind: 'prefix', value: '/api/servers/self' },
    why: 'self-service shard management -- database writes, not the login path',
  },

  // --- live-session derived ---
  //
  // 🚨 EVERYTHING IN THIS GROUP MUST BE 'game', AND THE REASON IS STATE, NOT URL SHAPE.
  // proxyStats keeps the live-session map in MEMORY, and only UOProxy's WebSocket handler
  // calls trackSession -- which runs in the game process alone. In the web process that map
  // is permanently EMPTY, so a handler there does not error: it reads "nobody is online" and
  // answers confidently with the wrong thing.
  //
  // That is exactly how /api/metrics/report was missed when the split first shipped. Its
  // anti-cheat gate is `if (!hasLiveSession(user.sub)) return credited: 0`, so on the web
  // process EVERY report returned credited: 0 -- play time and achievements silently stopped
  // being credited, with no error anywhere. The route was reachable, registered and 200: the
  // split-consistency gate proves routes land on a process that REGISTERS them, and cannot
  // see that a process lacks the STATE a handler reads.
  //
  // Rule of thumb when adding a route: if the handler touches proxyStats, it is 'game'.
  {
    prefix: '/api/metrics/report',
    owner: 'game',
    nginx: { kind: 'prefix', value: '/api/metrics/report' },
    why: 'anti-cheat gate reads the live-session map, which only the game process holds',
  },
  // 🚨 A LESSON THAT OUTLIVED ITS RULES, kept because the next person will hit it. This table
  // matches by PREFIX against Express patterns, so a rule for `/api/admin/item-` also swallows
  // `/api/admin/item-drops` — a pure-config route that belongs to web. Pair that with an nginx
  // regex narrower than the prefix and the app registers a route on one process while nginx
  // sends it to the other: a 404 in production, and the consistency gate is what caught it.
  // Never write a rule whose nginx pattern is narrower than its own prefix.

  // 🚨 NOT LISTED HERE, DELIBERATELY: /api/gear and /api/admin/{gear,item-grant,item-revoke}.
  // They used to be 'game' because they read the live-session map to learn a player's shard
  // account. They no longer read it — the name is COMPUTED (`d<discordId>` / `g<hex>`, see
  // webAccountFor in AssetServer) and the shard resolves an offline character — so they are
  // unruled and land on WEB with everything else.
  //
  // That is the operator's architecture, not an accident (2026-07-31): the minigames shard is
  // a BACKEND FOR THE WEBSITE, and the dedicated web proxy is what must talk to it — "que no
  // vaya al proxy normal". Putting them on 'game' made the process that relays live gameplay
  // do the website's data fetching.

  // --- the path into the game: must survive the web process dying ---
  {
    prefix: '/uo/',
    owner: 'game',
    nginx: { kind: 'prefix', value: '/uo/' },
    why: 'game asset batch, on the critical path while playing',
  },
  {
    // OWNED BY 'both' ON PURPOSE, and this is not cosmetic. It is the path the container
    // healthcheck probes (docker-compose.yml: wget --spider .../api/config). A process
    // that does not answer it is marked unhealthy, the autoheal sidecar restarts it, and
    // it never recovers -- and nginx has `depends_on: proxy: service_healthy`, so the
    // whole site goes down with it, roughly a minute after the change looked fine.
    // Found 2026-07-29 by RUNNING ownsRoute rather than reading it: under ROLE=game the
    // general /api/ rule below claimed this route and the game process would have 404'd
    // its own healthcheck. Any future health probe must be owned by 'both' for the same
    // reason. Routed to game because it is also on the client's boot path.
    prefix: '/api/config',
    owner: 'both',
    route: 'game',
    nginx: { kind: 'prefix', value: '/api/config' },
    why: 'client boot config + container health probe; every process must answer it',
  },
  {
    prefix: '/api/servers',
    owner: 'game',
    nginx: { kind: 'prefix', value: '/api/servers' },
    why: 'the shard picker; without it nobody can choose a server and connect',
  },
  {
    prefix: '/auth/',
    owner: 'game',
    nginx: { kind: 'prefix', value: '/auth/' },
    why: 'login and token issuance; a dead web process must not block signing in',
  },

  // --- everything else is database-derived: the portal and the economy ---
  {
    prefix: '/u/',
    owner: 'web',
    nginx: { kind: 'prefix', value: '/u/' },
    why: 'public profile short link -- pure database read',
  },
  {
    prefix: '*',
    owner: 'both',
    nginx: { kind: 'prefix', value: '/' },
    why: 'SPA fallback; harmless on either process and wanted on both',
  },
  {
    prefix: '/api/',
    owner: 'web',
    nginx: { kind: 'prefix', value: '/api/' },
    why: 'portal, profiles, cards, market, items, cosmetics, leaderboards, admin',
  },
];

/**
 * True when this process should register the route. Unknown paths default to BOTH, so a
 * route added without a rule keeps working rather than vanishing from production; the boot
 * summary below is what surfaces it.
 */
export function ownsRoute(pattern: string): boolean {
  if (ROLE === 'all') {
    return true;
  }

  const rule = RULES.find((r) => pattern.startsWith(r.prefix));
  return rule ? rule.owner === 'both' || rule.owner === ROLE : true;
}

/** Routes with no rule: they run on both processes. Logged at boot so nobody has to guess. */
export function unruledRoutes(patterns: string[]): string[] {
  return patterns.filter((p) => !RULES.some((r) => p.startsWith(r.prefix)));
}

/** Where nginx sends a rule, as opposed to which processes register it. */
function effectiveRoute(r: Rule): Exclude<Role, 'all'> {
  // A 'both' rule that does not say where to route falls to web, the default half. Every
  // 'both' rule should set `route` explicitly rather than rely on this.
  return r.route ?? (r.owner === 'both' ? 'web' : r.owner);
}

/** The /api/ rules the map is built from, in declaration order (first match wins). */
const API_RULES: Rule[] = RULES.filter((r) => r.prefix !== '*' && r.nginx.value.includes('/api/'));

/**
 * The nginx snippet, generated from the SAME table the app matches on so the two cannot
 * disagree -- a hand-copied prefix that drifts does not error, it serves a route from the
 * wrong process.
 *
 * WHY A MAP AND NOT `location` BLOCKS -- the first version of this emitted one location per
 * rule and would have taken the whole site down:
 *   - `/api/`, `/auth/` and `/u/` ALREADY have location blocks in nginx.conf. A second
 *     block with the same prefix is `duplicate location`, a hard config error, and nginx
 *     then REFUSES TO START. A partial change becomes a total outage.
 *   - A location does not inherit from a SIBLING location, only from `server`. A block
 *     holding just `set $proxy_upstream` would have no proxy_pass, no limit_req and none of
 *     the security headers, so it would quietly serve static files instead of proxying --
 *     404 on the shard picker, i.e. nobody can log in.
 * One map, consulted from the single existing `location /api/`, has neither problem.
 *
 * ALSO NOT OBVIOUS: this stack never puts a literal host in proxy_pass. It uses `resolver`
 * plus `set $proxy_upstream <name>:3000` so nginx re-resolves per request. A literal
 * upstream resolves ONCE at startup, so a recreated container 502s every route until
 * someone reloads by hand (the v0.3.34 incident) -- and nginx refuses to start at all while
 * that container is down. So we only ever change the VARIABLE.
 */
export function nginxApiMap(gameHost = 'proxy:3000', webHost = 'proxy-web:3000'): string {
  const hostOf = (r: Rule) => (effectiveRoute(r) === 'game' ? gameHost : webHost);
  const pats = API_RULES.map(mapPattern);
  const width = Math.max(7, ...pats.map((p) => p.length)) + 4;

  const lines = [
    '# GENERATED from server/src/proxyRole.ts -- do not hand-edit.',
    '# Regenerate: node tools/print-nginx-map.mjs   Verify: node tools/check-split-consistency.mjs',
    '# Consulted ONLY by `location /api/` below, via `set $proxy_upstream $api_upstream;`.',
    '# First match wins among the regexes, so the more specific pattern is listed first.',
    'map $uri $api_upstream {',
    `    ${'default'.padEnd(width)}"${webHost}";`,
  ];
  API_RULES.forEach((r, i) => {
    lines.push(`    ${pats[i].padEnd(width)}"${hostOf(r)}";   # ${r.why}`);
  });
  lines.push('}');

  return lines.join('\n');
}

/** The pattern for a rule as it appears in the map. */
function mapPattern(r: Rule): string {
  return r.nginx.kind === 'regex' ? `~${r.nginx.value}` : `~^${r.nginx.value}`;
}

/**
 * Which process nginx picks for a CONCRETE URL, simulating the generated map.
 *
 * This exists because the app and nginx match in two different domains: `ownsRoute` matches
 * an Express PATTERN (`/api/:game/live`) while nginx matches a URL (`/api/runmatch/live`).
 * Drift between them produces no error at all -- a route is simply served by the process
 * with the wrong state. tools/check-split-consistency.mjs walks every registered route and
 * compares the two answers, which is the only cheap way to catch that.
 */
export function nginxRoleForUri(uri: string): Exclude<Role, 'all'> {
  const matches = (r: Rule) => {
    const source =
      r.nginx.kind === 'regex'
        ? r.nginx.value
        : '^' + r.nginx.value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(source).test(uri);
  };

  if (uri.startsWith('/api/')) {
    const hit = API_RULES.find(matches);
    // No entry matched: the map's own `default`, which is the web half.
    return hit ? effectiveRoute(hit) : 'web';
  }

  // Outside /api/ the map is not consulted at all -- each location block carries a literal
  // upstream. Only /u/ is web; /auth/, /ws, /uoam and the /gamefiles/batch rewrite are game.
  const hit = RULES.filter((r) => r.prefix !== '*' && !r.nginx.value.includes('/api/')).find(matches);
  return hit ? effectiveRoute(hit) : 'game';
}

/**
 * Splitting ROUTES does not split BACKGROUND WORK, and that is easy to miss: a process
 * nginx never routes to is still not passive. AssetServer registers scheduled jobs that
 * WRITE -- presence sampling, GDPR account reaping, audit maintenance, expired-directory
 * cleanup. Running them in two processes does not fail; it silently produces wrong data
 * (two samples per interval, two reapers deleting at once), which is worse.
 *
 * The game process owns them: it is the one that always exists, in every deployment,
 * including the un-split ROLE=all default.
 *
 * NOT everything on a timer is a "job". Per-process housekeeping -- expiring in-memory
 * rate-limit buckets, for instance -- must keep running in BOTH processes, because each
 * has its own buckets. Gating the wrong timer is as harmful as gating none.
 */
export const OWNS_JOBS: boolean = ROLE !== 'web';

/**
 * The mirror of OWNS_JOBS, for the jobs that must run where the SHARD WORK belongs.
 *
 * Same single-owner rule, opposite process. A job goes here when it calls the minigames
 * shard, because that traffic is the web proxy's alone (operator 2026-07-31: "todo lo que
 * sea para el servidor de minijuegos como items, cartas y cosas así y llamadas a la api del
 * server de minigames tiene que ser en el proxy web ... así si se satura la otra parte web,
 * no afecta al resto de jugadores que solo quieren jugar realmente").
 *
 * `ROLE !== 'game'` rather than `=== 'web'` so the un-split ROLE=all default still runs it —
 * exactly the reasoning OWNS_JOBS uses, kept symmetric so neither can be read as accidental.
 * In a split deployment the two are disjoint, which is the point: every job has exactly one
 * owner, and which one is decided by what the job TOUCHES, not by where it was easy to put.
 */
export const OWNS_WEB_JOBS: boolean = ROLE !== 'game';

/**
 * setInterval for scheduled jobs: a no-op on a process that does not own them, and a job that
 * throws does not take the process with it.
 *
 * 🚨 A SYNC THROW IN A TIMER IS THE BIGGEST BLAST RADIUS A BACKGROUND BUG HAS HERE. index.ts
 * handles `uncaughtException` with `process.exit(1)` — deliberately, and correctly for request
 * handlers, where a sync throw means a broken invariant. A periodic job is not that: "the presence
 * sampler hit a bad row" or a `SQLITE_BUSY` from the other process should cost one skipped run, not
 * every live WebSocket session on the shard. And SQLITE_BUSY is not hypothetical here — two
 * processes share these databases, and node:sqlite throws it synchronously.
 *
 * Every current caller happens to wrap its own body, so nothing is broken today. That is exactly
 * the argument `jobAtBoot` below already makes about ownership: a rule each new job has to remember
 * is a rule the third one forgets. The two helpers are meant to read as a pair, and one of them
 * catching while the other does not was the asymmetry.
 *
 * The error is logged, not swallowed: a job that fails every hour must be visible, and the caller's
 * own catch (where it has one) still runs first and keeps its own message.
 */
export function everyJob(fn: () => void, ms: number): { unref(): void } {
  if (!OWNS_JOBS) {
    return { unref() { /* nothing was scheduled */ } };
  }
  const guarded = (): void => {
    try {
      fn();
    } catch (e) {
      console.error(`[job] scheduled job threw (continuing): ${(e as Error)?.stack ?? e}`);
    }
  };
  return setInterval(guarded, ms) as unknown as { unref(): void };
}

/**
 * The BOOT run of a scheduled job — same ownership rule as `everyJob`, which is the whole point.
 *
 * 🚨 THE RECURRING HALF WAS GATED AND THE BOOT HALF WAS NOT. `runExpiredCleanup` and
 * `reapInactiveOwners` were scheduled through `everyJob` (owner only) but ALSO called bare at
 * startup, so both processes ran them — concurrently, because a deploy recreates `proxy` and
 * `proxy-web` together and they start within milliseconds of each other. Every deploy was a
 * simultaneous double run of the two most destructive jobs in the codebase.
 *
 * What that costs is the deletion queue: its own header states it is safe ONLY because every
 * mutation runs under one in-process mutex, and its load-modify-persist cycle is not a
 * cross-process transaction. Two processes removing different entries from a freshly-loaded copy
 * each write back a whole array, so the loser's removal is undone — an entry that should be gone
 * comes back, or a shard queued for deletion loses its place.
 *
 * A helper rather than an `if` at each call site: the pairing is the rule, and an `if` is
 * something the third job has to remember. `everyJob` and this are the same decision, so they
 * read as a pair and a new job that only reaches for one of them is visibly odd.
 */
export function jobAtBoot(fn: () => void | Promise<void>, label: string): void {
  if (!OWNS_JOBS) return;
  try {
    const r = fn();
    if (r && typeof (r as Promise<void>).catch === 'function') {
      (r as Promise<void>).catch((e) => console.error(`[${label}] boot error:`, e));
    }
  } catch (e) {
    console.error(`[${label}] boot error:`, e);
  }
}

export function roleSummary(registered: number, skipped: number): string {
  return `[role] PROXY_ROLE=${ROLE} routes registered=${registered} skipped=${skipped} scheduledJobs=${OWNS_JOBS}`;
}
