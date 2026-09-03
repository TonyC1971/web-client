# Contributing

## Read this first: **this repository is generated**

The code here is published from a private monorepo by a whitelist export. It is a **snapshot**, not
a working tree.

That has one consequence worth stating plainly:

🚨 **A commit made in this repository is destroyed by the next publish.** Not merged, not conflicted
— overwritten, because publishing force-pushes a fresh snapshot. If you fix something here and it
disappears a week later, that is why.

So:

- **Open an issue**, or
- **Open a pull request anyway** — it is a perfectly good way to show the change. It will be applied
  upstream and it will arrive here in the next publish, but it will be *closed* rather than merged.
  That is not a rejection; there is simply nothing here to merge into.

## Where the code actually comes from

| what you see here | where it is maintained |
|---|---|
| `source/cuo/`, `source/tuo/` | forks of ClassicUO and TazUO, upstream + local patches |
| `source/webclient/minimal-www/` | the web layer for THIS build, hand-maintained |
| `server/src/shared/` | the modules shared with the full build — the transitive closure of the entrypoint, so the set moves when the code does |
| `server/src/minimal/` | the reduced proxy: only the routes this client calls |

The upstream monorepo also contains a full hosted deployment — portal, trading cards, marketplace,
cosmetics, minigames, achievements. **None of it is here**, and none of it is reachable from this
code: the export is derived from the import graph, not from a hand-written file list, and a test
fails upstream if anything from that half becomes reachable.

## Running it

See the README. In short: `.env`, one YAML in `servers/`, pre-compress your game files, then
`docker compose -f docker-compose.minimal.yml up -d`.

## The two things newcomers get wrong

**1. Pre-compress your game files — but they are not required to boot.** Run
`node server/scripts/precompress-gamefiles.mjs --in <dir> --out <dir>` over them once and nginx will
serve the `.br` twins instead of the raw files, which is a large bandwidth win on a 1.8 GB fileset.

This paragraph used to say the twins were mandatory and that without them every asset 404s. That was
wrong, and measured so: the loader requests each file **by name**, and `brotli_static` falls back to
the raw file when no twin exists. An install that skips this step works — it is just heavier. The
`.br` fallback inside `main.js` only fires for an *external* gamefiles base.

At quality 11 a full fileset takes hours; `--quality 9` is roughly a tenth of the time for a few
percent more bytes, and is the better default for a first run.

**2. Four files must agree on the shard slug**, and none of them fails with a message naming the
mismatch. The README has the table; the short version:

| Where | If it is wrong |
|---|---|
| `SHARD_SLUG` in `.env` | The container mounts an empty directory. Endless loading. |
| the folder under `gamefiles/` | Same. |
| `slug:` in `servers/<name>.yaml` | This is the name the browser asks for. |
| `slug` in `client/minimal/config.json` | The relay cannot resolve the shard: the client loads and then never connects. |

⚠️ This section used to say TWO files, and that a mismatch produced "corrupt-looking artwork". Both
were wrong. There is no second fileset on a single-shard install to load by mistake — what actually
happens is an empty mount or a connection that never resolves, and someone told to look for wrong
artwork will not recognise either.

If the boot log says `shard=default` after you wrote your YAML, the file was **skipped** — the reason
is printed on the line above it, starting with `[ServerRegistry] SKIP`.

## What is deliberately missing

No portal, no economy, no minigames, no leaderboards, no telemetry. If you want those, you are
looking for a different project — this one exists so that a shard can run the client and nothing
else.

`config.json` is yours: upgrades ship `config.example.json` and only create `config.json` when it is
absent, so updating the client never overwrites your settings.
