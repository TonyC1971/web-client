/**
 * The proxy for a MINIMAL self-hosted install: only the routes that client actually calls.
 *
 * 🚨 IT DOES NOT IMPORT AssetServer.ts, AND THAT IS THE ENTIRE POINT.
 * AssetServer is the uonexus product surface — 6288 lines, 66 local imports, twelve of them the
 * economy (cards, market, cosmetics, minigames, achievements, points, seasons). Reusing it to save
 * writing ten thin handlers would drag all of that into a build whose whole purpose is not to carry
 * it. What IS reused is the clean lower layer: auth (JWT + Discord identity), the shard registry,
 * profile storage, the script policy, and the WS relay itself.
 *
 * WHAT A SELF-HOSTER GETS, and nothing more:
 *   /ws                     the UO protocol relay              (UOProxy, reused verbatim)
 *   /uoam                   shared world-map positions         (uoamHub, reused verbatim)
 *   /api/config             client boot configuration
 *   /api/servers            the ONE configured shard
 *   /api/me                 who is signed in
 *   /auth/discord/*         optional sign-in
 *   /api/settings           cloud-synced client settings
 *   /api/profile            the five CUO profile files
 *   /api/script-policy/:slug which scripting verbs are allowed
 *   /api/admin/gate         the auth_request target nginx uses to hide /admin
 *   /api/servers/:slug/hashes the SHA-256 map the client's cache audit checks against
 *   /api/client-epoch       cache-busting epoch for the loader
 *   /api/boot-failure       client-side boot diagnostics sink
 *
 * ⚠️ GAME FILES ARE NOT SERVED HERE. nginx serves them straight off disk with brotli_static. They
 * are large and static; routing them through Node buys nothing and would have meant porting the
 * whole asset pipeline — the single biggest reason this file could stay small. (They are served BY
 * NAME, not content-addressed: that is the hosted deployment's pool, which this build does not have.
 * The same distinction is why /manifest stays absent while /hashes does not.)
 */
import express, { type Express, type Request, type Response, type NextFunction } from 'express';
import * as http from 'node:http';
import {
  currentUser, requireSafeOrigin,
  recordIdentity, getIdentityName, getIdentityAvatar,
  handleDiscordLogin, handleDiscordCallback, handleGuestLogin, handleLogout,
  handleApiGetSettings, handleApiPutSettings,
  handleApiGetProfile, handleApiPutProfile, handleApiDeleteProfile,
  adminEraseUser, listAccounts, listProfiles, deleteProfilesForUser,
} from '../shared/auth.js';
import { listServers, getServer, defaultSlug } from '../shared/serverRegistry.js';
import {
  getEffectiveVerbs, getGlobalBlock, setGlobalBlock, getShardBlock, setShardBlock,
  SCRIPT_VERBS,
} from '../shared/scriptPolicy.js';
import { listBans, addBan, removeBan } from '../shared/banRegistry.js';
import { listSessions, kickSession } from '../shared/proxyStats.js';
import { PUBLIC_ORIGINS, DISCORD_CLIENT_ID, DATA_PATH } from '../shared/config.js';
import { writeFileAtomic } from '../shared/atomicJson.js';
import * as fs from 'node:fs';
import * as nodePath from 'node:path';

/**
 * Admin subjects for THIS install, from the environment. Empty ⇒ nobody is an admin.
 *
 * 🚨 ONE FUNCTION, BECAUSE TWO COPIES OF A RULE ARE A GAP WITH A COMMENT OVER IT. This rule decides
 * both whether nginx serves admin.html (`/api/admin/gate`) and whether each API call is honoured,
 * and it used to be written out twice with a note asking the next person to keep them in step.
 * Nothing checked that they did. This project has already paid for exactly that shape — a rule
 * living in SQL and in JavaScript, where a test covering one half passed while the other was gone —
 * and the fix there was the same: make both callers read the single function.
 *
 * The asymmetry that matters is preserved: the gate answers with a bare status because nginx reads
 * only the code, while the API answers JSON.
 */
async function isAdminRequest(req: Request): Promise<boolean> {
  const user = await currentUser(req);
  if (!user) return false;
  const admins = adminSubs();
  return admins.size > 0 && admins.has(user.sub);
}

function adminSubs(): Set<string> {
  return new Set(
    String(process.env.ADMIN_SUBS || '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean),
  );
}

const json = (res: Response, code: number, body: unknown) => {
  // Everything here is per-caller or configuration; none of it may be held in a shared cache.
  // /api/me in particular would hand the next visitor somebody else's identity.
  res.setHeader('Cache-Control', 'no-store');
  res.status(code).json(body);
};

export function buildMinimalApp(): Express {
  const app = express();
  app.disable('x-powered-by');
  app.use(express.json({ limit: '256kb' }));

  // 🚨 requireSafeOrigin RETURNS A BOOLEAN — it is a guard, not Express middleware, and mounting it
  // as one hung every route it was supposed to protect. Express waits for next(); a boolean guard
  // never calls it. The failure shape is the cruellest possible: a request with a BAD Origin is
  // rejected correctly (the guard writes 403 and ends the response), and a request with a GOOD
  // Origin hangs forever. So the security path looked healthy while sign-in, settings sync, profile
  // sync, account deletion and boot-failure reports all silently never completed — seven routes.
  //
  // The handlers from auth.ts each call the guard themselves, so this adapter is belt-and-braces
  // there. It stays on every route anyway: the route table should say what protects a route, and a
  // handler swapped in later that does NOT self-guard must not quietly lose the check.
  const safeOrigin = (req: Request, res: Response, next: NextFunction): void => {
    if (requireSafeOrigin(req, res)) next();
  };


  // ── which client this install serves at / ──────────────────────────────────
  // A minimal install can carry BOTH forks: ClassicUO at / and TazUO at /tuo/. Which one a plain
  // visit to / lands on is an operator decision, so it lives here rather than in a rebuild.
  //
  // 🚨 IT IS A REDIRECT, NOT A SWAP. The two bundles differ in the compiled WASM assembly, which the
  // page cannot exchange after loading — so / sends the visitor to /tuo/ when TazUO is selected.
  // BOTH paths therefore stay reachable by explicit URL whatever the setting says; the setting only
  // decides the default landing. Anything else would make one of the two installed clients
  // unreachable, which is a strange thing for a panel offering a choice to do.
  const settingsFile = nodePath.join(DATA_PATH, 'admin-settings.json');

  /**
   * Operator settings, in ONE file.
   *
   * It started as default-client.json holding a single key. A second setting arriving a few hours
   * later would have meant a second file, a second reader and a second chance for the two to
   * disagree about what "unset" means — so it generalised instead. Every value here fails to the
   * behaviour an unconfigured install already had.
   */
  type AdminSettings = { defaultClient: 'cuo' | 'tuo'; disableDev: boolean; allowClientSwitch: boolean };
  const DEFAULTS: AdminSettings = { defaultClient: 'cuo', disableDev: false, allowClientSwitch: false };

  /**
   * Is a client bundle really installed?
   *
   * 🚨 index.html AND _framework, not merely a directory. An empty mount is exactly how this
   * stack has failed before — the nginx /tuo/ block pointed at a path nothing provided — and
   * "the folder exists" would have called that installed and served 404s to everyone.
   *
   * CLIENT_ROOT defaults to the compose mount. An install whose compose predates this feature
   * has no such directory, and the answer is then "not installed": refusing to enable a client
   * this process cannot verify is the safe side of an unknown, and the message says what to do.
   */
  const CLIENT_ROOT = process.env.CLIENT_ROOT || '/srv/client';
  const bundleInstalled = (which: 'cuo' | 'tuo'): boolean => {
    try {
      const dir = nodePath.join(CLIENT_ROOT, which);
      return fs.existsSync(nodePath.join(dir, 'index.html')) && fs.existsSync(nodePath.join(dir, '_framework'));
    } catch { return false; }
  };
  const readSettings = (): AdminSettings => {
    try {
      const raw = JSON.parse(fs.readFileSync(settingsFile, 'utf8'));
      return {
        defaultClient: raw?.defaultClient === 'tuo' ? 'tuo' : 'cuo',
        disableDev: raw?.disableDev === true,
        // Default FALSE: an install that never touched this keeps serving exactly one client, which
        // is what it does today. Offering a switch is a decision, not a default.
        allowClientSwitch: raw?.allowClientSwitch === true,
      };
    } catch { return { ...DEFAULTS }; }   // unset, unreadable or corrupt ⇒ the unconfigured defaults
  };

  app.get('/api/admin/settings', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    json(res, 200, {
      ...readSettings(),
      // The panel used to HEAD /tuo/ itself. Two sources of truth about the same fact is how a
      // panel ends up offering exactly what the backend will refuse.
      installed: { cuo: bundleInstalled('cuo'), tuo: bundleInstalled('tuo') },
    });
  });

  // ── disk usage ─────────────────────────────────────────────────────────────
  // What a self-hoster actually runs out of. The gamefiles tree is the bulk of it and the one thing
  // they chose the disk for, so it is reported alongside the small data dir rather than lumped in.
  //
  // ⚠️ The walk is SYNCHRONOUS and this is a single-process server, so it blocks the event loop for
  // its duration. Measured on a real install: ~130ms for 559 files / 2.7 GiB. Admin-only and behind
  // a button, so that is a fair price for an exact answer; a cap on files walked would buy speed by
  // reporting a number that is simply wrong, which is the worse trade for a disk-usage readout.
  app.get('/api/admin/disk', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const walk = (dir: string): { files: number; bytes: number; br: number } => {
      let files = 0, bytes = 0, br = 0;
      let ents: fs.Dirent[];
      try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch { return { files, bytes, br }; }
      for (const e of ents) {
        const p = nodePath.join(dir, e.name);
        if (e.isDirectory()) {
          // @eaDir is a thumbnail cache some NAS firmware sprinkles through shared folders.
          // Counting it would report disk this install did not use.
          if (e.name === '@eaDir') continue;
          const sub = walk(p);
          files += sub.files; bytes += sub.bytes; br += sub.br;
        } else if (e.isFile()) {
          files++;
          if (e.name.endsWith('.br')) br++;
          try { bytes += fs.statSync(p).size; } catch { /* vanished mid-walk */ }
        }
      }
      return { files, bytes, br };
    };
    const gfRoot = process.env.ASSETS_PATH || '/gamefiles';
    json(res, 200, { data: walk(DATA_PATH), gamefiles: { path: gfRoot, ...walk(gfRoot) } });
  });

  // ── scripting policy ───────────────────────────────────────────────────────
  // Which macro verbs this shard allows. Editable, because the registry has real setters and the
  // decision is the operator's -- a read-only view would just be a list they cannot act on.
  app.get('/api/admin/script-policy', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    // defaultSlug() is null on an install whose YAML was rejected — the same state the boot log
    // reports as `shard=default`. Answer honestly rather than crashing the panel: there is no shard
    // to hold a policy for yet.
    const slug = defaultSlug();
    json(res, 200, {
      // The catalog travels with the policy, because a blocklist of bare verb names is not
      // something an operator can act on: they would have to already know every name to type one.
      // Sending key+label+group lets the panel show what each verb LETS A SCRIPT DO.
      catalog: SCRIPT_VERBS,
      slug,
      effective: slug ? getEffectiveVerbs(slug) : [],
      globalBlock: getGlobalBlock(),
      shardBlock: slug ? getShardBlock(slug) : [],
    });
  });

  app.put('/api/admin/script-policy', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const body = (req.body || {}) as { globalBlock?: unknown; shardBlock?: unknown };
    const slug = defaultSlug();
    // 'key' in body, never a rebuilt object: the panel sends one list at a time, and rebuilding
    // from the body would silently reset whichever list the caller did not include.
    if ('globalBlock' in body) setGlobalBlock(body.globalBlock);
    if (slug && 'shardBlock' in body) setShardBlock(slug, body.shardBlock);
    // Same shape as the GET, catalog included: the panel re-renders the grid from THIS response,
    // and a save that answered without the catalog would blank the grid it just saved.
    json(res, 200, {
      catalog: SCRIPT_VERBS,
      slug,
      effective: slug ? getEffectiveVerbs(slug) : [],
      globalBlock: getGlobalBlock(),
      shardBlock: slug ? getShardBlock(slug) : [],
    });
  });

  // ── bans ───────────────────────────────────────────────────────────────────
  // 🚨 THESE ARE ENFORCED, which is the only reason the panel offers them. UOProxy calls
  // findActiveBan at the WebSocket upgrade, by Discord id and by IP, so a ban here actually keeps
  // somebody out rather than decorating a list.
  app.get('/api/admin/bans', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    json(res, 200, { bans: listBans() });
  });

  app.post('/api/admin/bans', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const b = (req.body || {}) as { discordId?: string; ipCidr?: string; reason?: string; days?: number };
    // 🚨 CLAMP THE DURATION. addBan checks that expiresAt is in the future and nothing more, so
    // days=99999999999 was accepted and stored as 8640001787727154000 -- past JavaScript's maximum
    // Date, which renders as "Invalid Date". That is a permanent ban wearing a temporary label, and
    // an operator reading the list would be told something untrue about when it ends. Ten years is
    // well past any real moderation decision; beyond it, leave days empty and say permanent.
    const MAX_DAYS = 3650;
    const rawDays = Number(b.days);
    const days = Number.isFinite(rawDays) && rawDays > 0 ? Math.min(rawDays, MAX_DAYS) : 0;
    try {
      const entry = addBan({
        ...(b.discordId ? { discordId: String(b.discordId).trim() } : {}),
        ...(b.ipCidr ? { ipCidr: String(b.ipCidr).trim() } : {}),
        reason: String(b.reason || '').trim(),
        ...(days > 0 ? { expiresAt: Date.now() + days * 86400000 } : {}),
      });
      json(res, 200, { ok: true, ban: entry });
    } catch (e) {
      // addBan throws with a message that says exactly what is wrong (both fields, bad snowflake,
      // bad CIDR). Passing it through beats a generic 400 the operator has to guess at.
      json(res, 400, { error: (e as Error).message || 'invalid ban' });
    }
  });

  app.delete('/api/admin/bans/:id', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    json(res, 200, { ok: removeBan(String(req.params.id || '')) });
  });

  // ── live sessions ──────────────────────────────────────────────────────────
  // Straight from proxyStats, which UOProxy already maintains — no new bookkeeping, and no reaching
  // into the relay's private Session objects for a panel nicety.
  app.get('/api/admin/sessions', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    res.setHeader('Cache-Control', 'no-store');
    json(res, 200, {
      sessions: listSessions().map((s) => ({
        id: s.id, slug: s.slug, isGuest: s.isGuest, since: s.since,
        // The sub is a BEARER CREDENTIAL for a guest: whoever learns a guest-<hex> can replay it
        // and inherit that character. This page is admin-only, but it is still a browser page and
        // still gets screenshotted, so it carries a display name and never the raw sub.
        name: s.isGuest ? 'Guest' : (getIdentityName(s.sub) || 'Discord user'),
      })),
    });
  });

  app.post('/api/admin/sessions/kick', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const id = Number((req.body || {}).id);
    if (!Number.isFinite(id)) { json(res, 400, { error: 'id required' }); return; }
    json(res, 200, { ok: kickSession(id) });
  });

  // ── configuration backup ───────────────────────────────────────────────────
  // Everything the panel can change, in one file. Deliberately NOT the shard yaml or the .env: those
  // are edited on disk by the person who owns the box, and rolling them into a browser download
  // would put host, port and secrets somewhere they do not need to be.
  app.get('/api/admin/backup', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const slug = defaultSlug();
    res.setHeader('Cache-Control', 'no-store');
    json(res, 200, {
      version: 1,
      exportedAt: new Date().toISOString(),
      settings: readSettings(),
      scriptPolicy: { globalBlock: getGlobalBlock(), shardBlock: slug ? getShardBlock(slug) : [] },
      bans: listBans(),
    });
  });

  // ── stored data ────────────────────────────────────────────────────────────
  // Everything this install keeps about a person: a settings file per account and, once the client
  // syncs, per-client profile blobs. A self-hoster is the data controller here, so the panel has to
  // let them SEE and ERASE it — GDPR is not only the player's own delete button.
  // ⚠️ THIS ONE CARRIES THE RAW SUB AND /api/admin/sessions DELIBERATELY DOES NOT. The asymmetry is
  // intentional, so leave it: a guest sub is a bearer credential — whoever learns a guest-<hex> can
  // replay it and inherit that character — and this page is still a browser page that gets
  // screenshotted. Sessions are addressed by a numeric id, so they need no sub at all and do not
  // get one. Erase and drop-profiles have nothing else to name an account by, so the sub travels
  // here as a HANDLE; the panel uses it only in the click handler and never paints it. Do not
  // "align" the two by adding a sub to sessions, and do not remove it here without giving those two
  // routes another way to say which account they mean.
  app.get('/api/admin/data', async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const accounts = listAccounts();
    const { profiles, total } = listProfiles({ limit: 500 });
    const byId = new Map<string, { blobs: number; bytes: number }>();
    for (const p of profiles) {
      const e = byId.get(p.id) || { blobs: 0, bytes: 0 };
      e.blobs++; e.bytes += p.bytes;
      byId.set(p.id, e);
    }
    json(res, 200, {
      accounts: accounts.map((a) => ({
        ...a,
        profileBlobs: byId.get(a.sub)?.blobs || 0,
        profileBytes: byId.get(a.sub)?.bytes || 0,
      })),
      profileTotal: total,
      guests: accounts.filter((a) => a.isGuest).length,
    });
  });

  // Erase ONE account: settings, identity, profile blobs. Same helper the player's own delete
  // button uses, so the two paths cannot drift into erasing different things.
  app.post('/api/admin/data/erase', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const sub = String((req.body || {}).sub || '').trim();
    if (!sub) { json(res, 400, { error: 'sub required' }); return; }
    const r = adminEraseUser(sub);
    json(res, 200, { ok: true, sub, ...r });
  });

  // 🚨 PURGING GUESTS IS DESTRUCTIVE IN A WAY THAT IS NOT OBVIOUS, and this install must not do it
  // on a timer. A guest's id lives in the BROWSER's localStorage, not in the cookie, so somebody
  // can come back months later and still hold the same sub — deleting by age would destroy data
  // that was still recoverable by its owner, and the whole lot is tens of KB. So it stays a
  // deliberate act by the operator, never a scheduled sweep.
  app.post('/api/admin/data/purge-guests', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    let erased = 0, blobs = 0;
    for (const a of listAccounts()) {
      if (!a.isGuest) continue;
      const r = adminEraseUser(a.sub);
      erased++; blobs += r.profileBlobs;
    }
    json(res, 200, { ok: true, erased, profileBlobs: blobs });
  });

  // Profile blobs for one account, without touching its settings or identity — the narrower action
  // when a synced profile is corrupt but the account itself is fine.
  app.post('/api/admin/data/drop-profiles', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const sub = String((req.body || {}).sub || '').trim();
    if (!sub) { json(res, 400, { error: 'sub required' }); return; }
    let removed = 0;
    try { removed = deleteProfilesForUser(sub); } catch { /* best-effort */ }
    json(res, 200, { ok: true, sub, removed });
  });

  app.put('/api/admin/settings', safeOrigin, async (req, res) => {
    if (!(await isAdminRequest(req))) { json(res, 403, { error: 'forbidden' }); return; }
    const body = req.body || {};
    const next = readSettings();

    // Allow-list each key, and treat ABSENT as "not sent" rather than as false. A PUT that carried
    // only one toggle would otherwise silently reset the other — the same partial-write bug that
    // once wiped this project's stored scripts.
    if ('allowClientSwitch' in body) {
      if (typeof body.allowClientSwitch !== 'boolean') {
        json(res, 400, { error: 'allowClientSwitch must be a boolean' }); return;
      }
      if (body.allowClientSwitch && !(bundleInstalled('cuo') && bundleInstalled('tuo'))) {
        // A switch needs somewhere to switch TO. Offering one with a single destination is a
        // control that cannot do anything, which reads as a broken client rather than a
        // missing bundle.
        json(res, 400, { error: 'both bundles must be installed to offer players a choice — '
          + 'build the missing one (server/scripts/build-minimal-bundle.mjs)' }); return;
      }
      next.allowClientSwitch = body.allowClientSwitch;
    }
    if ('defaultClient' in body) {
      if (body.defaultClient !== 'cuo' && body.defaultClient !== 'tuo') {
        json(res, 400, { error: "defaultClient must be 'cuo' or 'tuo'" }); return;
      }
      if (!bundleInstalled(body.defaultClient)) {
        // The whole failure mode of this setting is a site that redirects everyone to a 404,
        // so it is refused rather than accepted and later discovered.
        json(res, 400, { error: `the ${body.defaultClient} bundle is not installed — build it `
          + 'before making it the default (server/scripts/build-minimal-bundle.mjs)' }); return;
      }
      // Deliberately NOT checking whether a TazUO bundle exists: nginx owns the document roots and
      // this process cannot see them. The loader verifies the target answers before following the
      // setting, so a stale 'tuo' degrades to ClassicUO instead of a 404.
      next.defaultClient = body.defaultClient;
    }
    if ('disableDev' in body) {
      if (typeof body.disableDev !== 'boolean') { json(res, 400, { error: 'disableDev must be a boolean' }); return; }
      next.disableDev = body.disableDev;
    }

    try {
      fs.mkdirSync(DATA_PATH, { recursive: true });
      writeFileAtomic(settingsFile, JSON.stringify(next, null, 2));
    } catch { json(res, 500, { error: 'could not persist the setting' }); return; }
    json(res, 200, { ok: true, ...next });
  });

  // ── boot configuration ─────────────────────────────────────────────────────
  app.get('/api/config', (_req, res) => {
    json(res, 200, {
      serverName: process.env.SERVER_NAME || 'UO Shard',
      // Reported so the client can hide the sign-in button instead of offering a button that 503s.
      discordEnabled: Boolean(DISCORD_CLIENT_ID),
      minimal: true,
      // Read by minimal-boot.js at / to decide whether to hand the visitor to /tuo/.
      defaultClient: readSettings().defaultClient,
      // The console silencer. The loader checks `=== true` strictly, so an install that never
      // touched this keeps its developer console exactly as it was.
      disableDev: readSettings().disableDev,
      // Whether the landing offers the player a choice of fork. Public on purpose: the client must
      // know without an admin session, and it reveals nothing an anonymous visitor cannot already
      // see by asking /tuo/ whether it exists.
      allowClientSwitch: readSettings().allowClientSwitch,
      // 🚨 Community-invite link for the landing page's Discord icon. The markup ships three of
      // them as `href="#"` placeholders (the publisher scrubs this install's real invite, correctly
      // -- a published repo must not carry one community's link). Nothing ever rewrote them, so
      // clicking the icon just re-anchored the page: it looked like the link went to the site
      // itself, which is what the operator reported. Served from the environment so a self-hoster
      // points it at THEIR community; left empty, the client removes the icon rather than keeping
      // a link that goes nowhere.
      discordInvite: String(process.env.DISCORD_INVITE_URL || '').trim(),
    });
  });

  // ── the one shard ──────────────────────────────────────────────────────────
  // Shaped exactly like the full registry response even though it holds a single entry: the client
  // reads these field names, and a different shape here would fall back to defaults silently.
  app.get('/api/servers', (_req, res) => {
    json(res, 200, { servers: listServers() });
  });

  // ── the game-file hash map ─────────────────────────────────────────────────
  // The client's background cache audit re-hashes its browser-cached game files against this and
  // silently re-downloads any that no longer match. Without it the audit skips entirely, and a file
  // corrupted in the browser's storage stays corrupted until the player clears it — artwork wrong
  // for one specific thing and right everywhere else.
  //
  // ℹ️ ITS SIBLING /manifest IS DELIBERATELY ABSENT, and that is not the same gap. An empty manifest
  // puts the loader in by-name mode, which is exactly how this build serves files — off disk, by
  // name. Adding one would describe a content-addressed pool this install does not have.
  //
  // The map is produced by minimal-asset-worker.mjs, incrementally. This process only reads it: the
  // proxy must never spend a request hashing gigabytes, and the worker already walks the tree.
  const hashFile = () => nodePath.join(process.env.ASSETS_PATH || '/gamefiles', '.gamefile-hashes.json');
  let hashCache: { mtime: number; body: string } | null = null;

  app.get('/api/servers/:slug/hashes', (req, res) => {
    // Answering {} rather than 404 when the worker has not run yet: the client treats an empty map
    // as "no verification available" and boots normally, which is the honest degradation. A 404 is
    // the same outcome for it but reads like a missing route to whoever is looking.
    let st;
    try { st = fs.statSync(hashFile()); } catch { json(res, 200, {}); return; }
    if (!hashCache || hashCache.mtime !== st.mtimeMs) {
      try {
        const raw = JSON.parse(fs.readFileSync(hashFile(), 'utf8')) as Record<string, { h: string }>;
        // The stored form carries size and mtime so the worker can skip unchanged files. The client
        // wants only { name: sha256 }, so the projection happens here rather than in a second file
        // on disk that could drift from the first.
        const out: Record<string, string> = {};
        for (const [k, v] of Object.entries(raw)) if (v && typeof v.h === 'string') out[k] = v.h;
        hashCache = { mtime: st.mtimeMs, body: JSON.stringify(out) };
      } catch {
        // A half-written or corrupt map must not take the boot down with it.
        json(res, 200, {});
        return;
      }
    }
    res.setHeader('Cache-Control', 'no-store');
    res.type('application/json').send(hashCache.body);
  });

  // ── identity ───────────────────────────────────────────────────────────────
  app.get('/api/me', async (req, res) => {
    const user = await currentUser(req);
    if (!user) { json(res, 401, { error: 'not signed in' }); return; }
    recordIdentity(user.sub, user.name, user.avatar);
    // Deliberately NOT the uonexus /api/me shape: no isAdmin scopes, no ownedServers, no
    // canRegister, no pendingDeletion. Those are concepts of a multi-shard hosted service; here
    // they would be dead fields that invite someone to build on them.
    json(res, 200, {
      id: user.sub,
      name: getIdentityName(user.sub) || user.name || null,
      avatar: getIdentityAvatar(user.sub) || user.avatar || null,
    });
  });

  // ── the admin gate nginx asks before serving /admin ────────────────────────
  // 🚨 THIS IS THE WHOLE ADMIN AUTHORISATION. nginx serves admin.html only when this answers 2xx,
  // so every path out of here that is not an explicit 200 must be a refusal. It fails closed by
  // construction: the default answer is 403 and only an explicit match returns 200.
  app.get('/api/admin/gate', async (req, res) => {
    // The SAME predicate the API calls, not a second copy of the rule — see isAdminRequest. A bare
    // status, not JSON: nginx reads the code and discards the body.
    res.status((await isAdminRequest(req)) ? 200 : 403).end();
  });

  // ── sign-in (optional) ─────────────────────────────────────────────────────
  // Mounted, not reimplemented. These handlers ARE the identity layer, they already carry the
  // origin checks, the OAuth state validation and the cookie handling, and a second copy here would
  // be a second thing to keep correct. Nothing about them is uonexus-specific — that was the point
  // of cutting auth.ts free of the economy.
  //
  // With no DISCORD_CLIENT_ID configured these answer 503 and the client stays fully playable as a
  // guest; the routes are mounted either way so the failure is an honest 503 rather than a 404 that
  // reads as "this build has no sign-in".
  app.get('/auth/discord', handleDiscordLogin);
  app.get('/auth/discord/callback', handleDiscordCallback);
  app.post('/auth/guest', safeOrigin, handleGuestLogin);
  app.post('/auth/logout', safeOrigin, handleLogout);

  // ── cloud-synced client state ──────────────────────────────────────────────
  // settings = the rail's own stores; profile = the five CUO files (macros, gumps, infobar,
  // profile.json, skillsgroups). Same handlers as uonexus, including the read-merge-write inside a
  // file lock that a partial PUT needs: absent means "not sent", never "delete it".
  app.get('/api/settings', handleApiGetSettings);
  app.put('/api/settings', safeOrigin, handleApiPutSettings);
  app.get('/api/profile', handleApiGetProfile);
  app.put('/api/profile', safeOrigin, handleApiPutProfile);
  app.delete('/api/profile', safeOrigin, handleApiDeleteProfile);

  // Account deletion. A self-hosted install still owes this to its players, and it is the same
  // handler — which now erases exactly the stores THIS build registered, no more and no less.
  // 🚨 NOT handleDeleteAccount. That handler SCHEDULES an erasure with a 7-day grace and leaves the
  // actual work to a reaper that lives in AssetServer — which this build does not ship. So on this
  // install it wrote a pending row that nothing would ever act on: a GDPR button that answered
  // {ok:true} and erased nothing, forever. Found 2026-08-26 while wiring the admin panel, after the
  // button had already been presented to the operator as working.
  //
  // The grace period is a hosted-service affordance (a reaper to run it, support to cancel it).
  // Here the honest contract is what the button says: erase now. The UI already requires two
  // deliberate clicks, which is the confirmation the grace was standing in for.
  app.post('/api/me/delete', safeOrigin, async (req, res) => {
    const user = await currentUser(req);
    if (!user) { json(res, 401, { error: 'not authenticated' }); return; }
    const r = adminEraseUser(user.sub);
    // The cookie is dropped by the client's follow-up POST /auth/logout; doing it here too would
    // duplicate a contract that already has one owner.
    json(res, 200, { ok: true, erased: true, removed: r.removed, profileBlobs: r.profileBlobs });
  });

  // ── scripting policy ───────────────────────────────────────────────────────
  app.get('/api/script-policy/:slug', (req, res) => {
    const slug = String(req.params.slug || '').toLowerCase();
    if (!getServer(slug)) { json(res, 404, { error: 'unknown shard' }); return; }
    json(res, 200, { verbs: getEffectiveVerbs(slug) });
  });

  // ── loader housekeeping ────────────────────────────────────────────────────
  app.get('/api/client-epoch', (_req, res) => {
    json(res, 200, { epoch: process.env.CLIENT_EPOCH || '1' });
  });

  // Boot diagnostics from the client. Accepted and logged, never stored: on a self-hosted install
  // the operator reads their own container log, and a database for this would be a table nobody
  // ever queries plus a write path reachable before authentication.
  app.post('/api/boot-failure', safeOrigin, (req: Request, res: Response) => {
    const b = (req.body ?? {}) as Record<string, unknown>;
    const stage = String(b.stage ?? 'unknown').slice(0, 60);
    const detail = String(b.detail ?? '').slice(0, 400);
    console.warn(`[minimal] client boot failure: stage=${stage} ${detail}`);
    res.status(204).end();
  });

  return app;
}

/** Start the minimal HTTP server. WS (/ws) and /uoam are attached by the entrypoint. */
export function startMinimalServer(port: number): http.Server {
  const app = buildMinimalApp();
  const server = http.createServer(app);
  server.listen(port, () => {
    console.log(`[minimal] listening on :${port}`);
    console.log(`[minimal] shard=${defaultSlug() || '(none configured)'}`);
    console.log(`[minimal] discord=${DISCORD_CLIENT_ID ? 'configured' : 'not configured (guests only)'}`);
    console.log(`[minimal] admins=${adminSubs().size || 'none — /admin is unreachable'}`);
    if (PUBLIC_ORIGINS.length === 0) {
      console.log('[minimal] PUBLIC_ORIGINS is empty — origin checks accept same-origin only');
    }
  });
  return server;
}
