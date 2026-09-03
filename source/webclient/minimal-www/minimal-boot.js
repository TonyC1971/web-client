// minimal-boot.js — reads config.json and hands main.js the ONE shard this install serves.
//
// This runs BEFORE main.js (a plain <script> ahead of the module), because main.js reads
// window.__MINIMAL_SERVER during evaluation: the full client would be fetching /api/servers and
// drawing a picker at this point, and this build has neither.
//
// 🚨 THE FIELD NAMES ARE A CONTRACT WITH THE LOADER, not a local convention. They are exactly what
// a /api/servers record carries, because everything downstream in main.js still reads them by
// those names. Rename one and nothing throws — the loader falls back to its defaults
// ('gamefiles', client 7.0.45.1) and you get a client that boots against the WRONG fileset, which
// looks like corrupt artwork rather than a configuration mistake.
//
// Fetched, not inlined, so the person self-hosting edits a JSON file instead of the client bundle:
// a rebuild must never be the price of pointing this at a different server.
(function () {
  'use strict';

  var DEFAULTS = {
    slug: '',                     // required — the shard id, also the per-shard storage namespace
    displayName: '',              // shown in the client chrome; falls back to slug
    clientVersion: '7.0.45.1',    // UO client version reported to the shard
    encrypt: 'none',              // none | old_bfish | blowfish | blowfish_2_0_3 | twofish_md5
    gamefilesUrlBase: 'gamefiles',// where the .mul-derived files are served from
    externalGamefilesUrl: ''      // optional absolute base, if hosted elsewhere
  };

  // Synchronous on purpose. main.js is a module and starts evaluating as soon as it parses, so an
  // async read here would race it: the picker-less boot would find __MINIMAL_SERVER undefined and
  // hard-fail with "No shard configured" on a perfectly good install. A blocking read of a local
  // ~200-byte file costs nothing next to the ~1.4 GB of gamefiles this client is about to pull.
  var cfg = null;
  try {
    var xhr = new XMLHttpRequest();
    xhr.open('GET', 'config.json?_=' + Date.now(), false);   // no-cache: an edited config must win
    xhr.send(null);
    if (xhr.status >= 200 && xhr.status < 300) {
      cfg = JSON.parse(xhr.responseText);
    } else {
      console.error('[minimal] config.json returned HTTP ' + xhr.status);
    }
  } catch (e) {
    // 🚨 A 200 does NOT prove the file exists on a stack with an SPA fallback: nginx can answer a
    // missing path with index.html, and JSON.parse of an HTML page throws here rather than
    // returning nonsense. Reported as what it is instead of leaving a silent empty config.
    console.error('[minimal] could not read config.json:', (e && e.message) || e);
  }

  var server = {};
  for (var k in DEFAULTS) if (Object.prototype.hasOwnProperty.call(DEFAULTS, k)) {
    var v = cfg && typeof cfg === 'object' ? cfg[k] : undefined;
    server[k] = (typeof v === 'string' && v.trim()) ? v.trim() : DEFAULTS[k];
  }
  if (!server.displayName) server.displayName = server.slug;

  // ── which client this install serves at / ────────────────────────────────
  // An install can carry BOTH forks: ClassicUO here and TazUO at /tuo/. The admin panel picks which
  // one a bare visit to / lands on; both stay reachable by explicit URL whatever it says.
  //
  // 🚨 THE TARGET IS VERIFIED BEFORE WE FOLLOW THE SETTING. A stale 'tuo' — the bundle removed, the
  // mount dropped — would otherwise send every visitor to a 404, turning a cosmetic preference into
  // a dead site. Checking first makes the unknown fall to the bundle that is definitely here.
  //
  // Only ever redirects AWAY from the root, so /tuo/ can serve this same file without a loop.
  /* The player's own pick, when the operator has enabled the switch. It WINS over the admin's
     default: the admin decides whether the choice exists, and taking an offered choice away
     silently would be worse than never offering it. Read defensively — a browser with storage
     disabled must still boot. */
  var CLIENT_PICK_KEY = "uominimal.client";
  function playerPick() {
    try {
      var v = localStorage.getItem(CLIENT_PICK_KEY);
      return (v === 'cuo' || v === 'tuo') ? v : null;
    } catch (e) { return null; }
  }
  window.__minimalSetClient = function (which) {
    if (which !== 'cuo' && which !== 'tuo') return;
    try { localStorage.setItem(CLIENT_PICK_KEY, which); } catch (e) {}
  };
  /* 🚨 A READER, BECAUSE THE OTHER HALF OF THIS WAS GUESSING THE KEY AND GETTING IT WRONG. This file
     owns where the pick is stored; main.js paints the highlight from it and decides whether pressing
     Play has to hand off to the other bundle. It was reading 'uoweb:clientPick' — a name nothing
     here has ever written — so every read came back null: the highlight showed whichever bundle was
     running rather than what the player had chosen, and the hand-off never fired at all. Picking a
     client appeared to do nothing until the page was reloaded, at which point THIS file read its own
     key and redirected correctly, so the fix looked like "press it again" or F5.
     One owner, one accessor. Nobody outside this file should know the string. */
  window.__minimalGetClient = playerPick;

  if (location.pathname === '/' || location.pathname === '/index.html') {
    fetch('/api/config', { cache: 'no-store', credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (c) {
        if (!c) return null;
        /* 🚨 The landing asks /api/config for itself and this no longer publishes the flag.
           This block only runs at "/", because that is where the redirect decision lives — so
           on /tuo/ the flag stayed false and the selector never rendered: a player could switch
           to TazUO and never get back. And it was set inside this async .then() while the
           landing wiring ran during boot, so even at "/" whether the control appeared depended
           on which resolved first. Both failures were silent. */
        var want = (c.allowClientSwitch === true && playerPick()) || c.defaultClient;
        if (want !== 'tuo') return null;
        return fetch('/tuo/config.json', { method: 'HEAD', cache: 'no-store' })
          .then(function (t) {
            if (!t.ok) { console.warn('[minimal] wanted TazUO but /tuo/ is not installed — staying on ClassicUO'); return null; }
            /* 🚨 RE-READ THE PICK HERE, BECAUSE THIS DECISION WAS MADE BEFORE THE PLAYER COULD ACT.
               The landing is static HTML and usable the moment it parses, while this chain costs an
               /api/config round-trip plus the HEAD above. A player who arrives with 'tuo' saved,
               picks ClassicUO and presses start inside that window is navigated to /tuo/ anyway by
               this line, using a preference captured before the click existed. It looks like the
               selector was ignored, it only happens when the network is slow enough to open the
               window, and reloading "fixes" it — which is exactly how it was reported.
               Reading late costs one localStorage hit and makes the last thing the player did win. */
            if (playerPick() === 'cuo') return null;
            // 🚨 CARRY THE QUERY AND HASH ACROSS. This used to be `location.replace('/tuo/')`, which
            // silently dropped everything after the path — and every diagnostic this project tells
            // people to use is a query parameter. A player following "load with ?snapshot=off" on an
            // install that serves TazUO by default landed on /tuo/ with the parameter gone, saw the
            // bug unchanged, and reported that the workaround does not work. Same for ?dev=1 and
            // ?showcompat=1. Measured: /?showcompat=1 arrived at / with search empty.
            location.replace('/tuo/' + location.search + location.hash);
            return null;
          });
      })
      .catch(function () { /* offline or no backend: this build still plays */ });
  }

  /* Which fork this page actually is, derived from where it is served rather than configured:
     /tuo/ serves this very file, so a second source of truth could only disagree with reality. */
  try {
    var here = location.pathname.indexOf('/tuo/') === 0 ? 'tuo' : 'cuo';
    window.__minimalClientSwitch = window.__minimalClientSwitch || { allowed: false, pick: null };
    window.__minimalClientSwitch.current = here;
  } catch (e) {}
  window.__MINIMAL_SERVER = server;
  // One line, so a self-hoster who opens the console can see what the client actually resolved
  // rather than guessing why it connected somewhere unexpected.
  console.log('[minimal] shard=' + (server.slug || '(none)')
    + ' client=' + server.clientVersion
    + ' encrypt=' + server.encrypt
    + ' gamefiles=' + (server.externalGamefilesUrl || server.gamefilesUrlBase));
})();
