// scriptPolicy.ts — per-shard policy for which rail JS-macro verbs scripts may
// call (operator 2026-06-11; inverted to allow-by-default 2026-06-11).
//
// Model (BLOCK-list — everything is ON by default; admins turn things OFF):
//   • Every gateable verb is ALLOWED by default. There are a lot of them, so the
//     admin opts OUT the ones they don't want, instead of opting in each one.
//   • A GLOBAL block-list (supreme admin) — verbs disabled for EVERY shard.
//   • A per-shard block-list (the shard's owner, or a supreme admin) — extra
//     verbs disabled for THAT shard.
//   • Effective allowed for a shard = ALL verbs − global-block − shard-block.
//   (print/sleep are always available; they're not game verbs and aren't gated.)
//
// The client (rail.js sandbox) fetches the effective ALLOWED list for the shard
// it's on and refuses any verb not in it — on TOP of the hard security allow-list
// baked into rail.js (which can never be widened from here). So this is a second,
// operator-controlled gate, not a way to reach unsafe verbs.
//
// Storage: runtime_config (db.ts). `script-verbs-block-global` = JSON string[]
// (global block); `script-verbs-block:<slug>` = JSON string[] (per-shard block).
// SQLite authoritative. (The pre-inversion `script-verbs-allow` key is ignored.)

import { db } from './db.js';

// The catalog of GATEABLE verbs — the safe game/inventory verbs the sandbox can reach. Grouped
// for the admin UI. Enforced in TWO client files against this same endpoint: rail.js GATED_VERBS
// (the JS sandbox) and legion-engine.js's `verb:` keys (LegionScript). `legionScript` is the odd
// one out: it is a LANGUAGE gate read by rail.js isLegionAvailable(), not a dispatch verb.
//
// 🚨 FIVE verbs run on a shard that has blocked everything else. This comment has said three and
// then seven; the drift is the point, because nothing connected the rail.js switch to this gate
// and a verb could go live ungoverned until somebody diffed two literals by hand. That diff now
// runs on every test pass (ALWAYS_ALLOWED in test/ScriptPolicyCoverage.test.ts carries the reason
// for each survivor) and fails on a sixth.
//
// SETTLED 2026-08-03 by the operator ("B, nadie ahora mismo está usando scripts"): `player` and
// `stopWalk` are now GATED like everything else. They used to run on a shard that had blocked all
// scripting, while their own siblings did not — getEquippedItems reads your gear and was gated,
// walkTo moves you and was gated, yet `player` read your character and `stopWalk` moved it,
// freely. From a shard owner's side that split was arbitrary and "block all scripting" did not
// mean what it says.
//
// The line is now one sentence: the console, waiting, and looking at your own target cursor are
// free; anything that touches your CHARACTER is something a shard can switch off. The change was
// safe to make BECAUSE nobody is scripting yet — the same edit next year breaks live scripts on
// every shard that blocks everything, so it would need a migration rather than a commit.
export interface ScriptVerbDef { key: string; label: string; group: string; }
export const SCRIPT_VERBS: ScriptVerbDef[] = [
  // Language gate (per-shard): block this to make a shard JS-ONLY (the rail hides
  // the LegionScript/LS tab). Default allow = JS + LS both available. Checked by
  // rail.js isLegionAvailable(), NOT by the JS sandbox.
  { key: 'legionScript', label: 'Allow LegionScript (Python) — else JS-only', group: 'Languages' },
  { key: 'say',          label: 'Speak in-world (player.say)',          group: 'Speech' },
  { key: 'chatSend',     label: 'Send a chat-channel message',          group: 'Speech' },
  { key: 'useItem',      label: 'Use / double-click an item',           group: 'Actions' },
  { key: 'attack',       label: 'Attack a mobile',                      group: 'Actions' },
  { key: 'useSkill',     label: 'Use a skill',                          group: 'Actions' },
  { key: 'castSpell',    label: 'Cast a spell',                         group: 'Actions' },
  { key: 'target',       label: 'Target an object/serial',              group: 'Targeting' },
  { key: 'targetSelf',   label: 'Target self',                          group: 'Targeting' },
  { key: 'targetLast',   label: 'Target last',                          group: 'Targeting' },
  { key: 'cancelTarget', label: 'Cancel target cursor',                 group: 'Targeting' },
  { key: 'requestTarget',label: 'Pick an object (target cursor)',       group: 'Targeting' },
  { key: 'moveItem',     label: 'Move an item to a container',          group: 'Inventory (mutates)' },
  { key: 'grabItem',     label: 'Grab an item into your pack',          group: 'Inventory (mutates)' },
  { key: 'equipItem',    label: 'Equip an item',                        group: 'Inventory (mutates)' },
  { key: 'getBackpackSerial',     label: 'Read backpack serial',        group: 'Reads' },
  { key: 'getContainerItems',     label: 'Read a container\'s items',   group: 'Reads' },
  { key: 'getEquippedItems',      label: 'Read equipped items',         group: 'Reads' },
  { key: 'getEquipmentDurability',label: 'Read gear durability',        group: 'Reads' },
  { key: 'getFriends',            label: 'Read friends list',           group: 'Reads' },
  { key: 'getItemArt',            label: 'Read item art for a graphic', group: 'Reads' },
  { key: 'getJournal',            label: 'Read the journal',            group: 'Reads' },
  { key: 'scanWorld',             label: 'Scan nearby mobiles/items',   group: 'Reads' },
  { key: 'getGumps',              label: 'Read open gumps + buttons',   group: 'Reads' },
  { key: 'objectAtCursor',        label: 'Read object under the cursor',group: 'Reads' },
  { key: 'player',                label: 'Read your own character',       group: 'Reads' },
  { key: 'walkTo',                label: 'Pathfind-walk to a coordinate', group: 'Navigation' },
  { key: 'stopWalk',              label: 'Stop your character walking',   group: 'Navigation' },
  { key: 'turn',                  label: 'Turn to face a direction',      group: 'Navigation' },
  { key: 'gumpReply',             label: 'Reply to / click a gump button', group: 'Actions' },
  { key: 'mouseMove',             label: 'Move the mouse cursor',       group: 'Mouse' },
  { key: 'mouseClick',            label: 'Click the mouse',             group: 'Mouse' },
  { key: 'mouseDoubleClick',      label: 'Double-click the mouse',      group: 'Mouse' },
  { key: 'manageFriends', label: 'Add / remove a friend', group: 'Lists' },
  { key: 'setWarMode', label: 'Toggle war mode', group: 'Combat' },
  { key: 'bandageSelf', label: 'Bandage self', group: 'Combat' },
  { key: 'toggleAbility', label: 'Toggle weapon ability', group: 'Combat' },
  { key: 'virtue', label: 'Invoke a virtue', group: 'Combat' },
  { key: 'setSkillLock', label: 'Set a skill lock', group: 'Character' },
  { key: 'setStatLock', label: 'Set a stat lock', group: 'Character' },
  { key: 'displayRange', label: 'Toggle the range radius', group: 'Character' },
  { key: 'trackingArrow', label: 'Show a tracking arrow', group: 'Character' },
  { key: 'mount', label: 'Mount / dismount', group: 'Mount' },
  { key: 'fly', label: 'Toggle gargoyle fly', group: 'Mount' },
  { key: 'logout', label: 'Log out', group: 'Session' },
  { key: 'rename', label: 'Rename a follower', group: 'Session' },
  { key: 'closeGump', label: 'Close a gump', group: 'Gumps' },
  { key: 'contextMenu', label: 'Open / pick a context menu', group: 'Gumps' },
  { key: 'pathfind', label: 'Cancel pathfinding', group: 'Navigation' },
  { key: 'markTile', label: 'Mark / unmark a ground tile', group: 'World' },
  { key: 'dress', label: 'Dress / undress from a config', group: 'Dress' },
  { key: 'autoLoot', label: 'Toggle / force auto-loot', group: 'Automation' },
  { key: 'autoFollow', label: 'Auto-follow a mobile', group: 'Automation' },
];
const VERB_KEYS = new Set(SCRIPT_VERBS.map((v) => v.key));

const qGet = db.prepare('SELECT value FROM runtime_config WHERE key = ?');
const qSet = db.prepare(
  'INSERT INTO runtime_config(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value',
);

function readList(key: string): string[] {
  const r = qGet.get(key) as { value: string } | undefined;
  if (!r) return [];
  try {
    const a = JSON.parse(r.value);
    if (Array.isArray(a)) return a.filter((s) => typeof s === 'string' && VERB_KEYS.has(s));
    console.warn(`[script-policy] ${key} is stored but is not an array — treating as "nothing blocked"`);
    return [];
  } catch {
    // 🚨 THIS IS A FAIL-OPEN AND IT IS DELIBERATE, BUT IT MUST NOT BE SILENT. An empty list
    // means "no verb is blocked", so unparseable stored policy quietly disables the gate for
    // that shard. Failing CLOSED instead would strand a shard with all scripting off because
    // one row got mangled, which is the worse outcome for an availability problem — but the
    // version that says nothing is worse than both, because nobody ever learns the policy
    // stopped applying. The row is only ever written by writeList (JSON.stringify of a
    // filtered array), so reaching here means something outside this module touched it.
    console.warn(`[script-policy] ${key} does not parse — the policy is NOT being applied for it`);
    return [];
  }
}
function writeList(key: string, list: unknown): string[] {
  const clean = Array.isArray(list)
    ? [...new Set(list.map(String).filter((s) => VERB_KEYS.has(s)))]
    : [];
  qSet.run(key, JSON.stringify(clean));
  return clean;
}

const slugKey = (slug: string) => `script-verbs-block:${String(slug).trim().toLowerCase()}`;

/** Global block-list (supreme admin). Default empty = nothing blocked (all on). */
export function getGlobalBlock(): string[] { return readList('script-verbs-block-global'); }
export function setGlobalBlock(list: unknown): string[] { return writeList('script-verbs-block-global', list); }

/** Per-shard block-list (owner / supreme). */
export function getShardBlock(slug: string): string[] { return readList(slugKey(slug)); }
export function setShardBlock(slug: string, list: unknown): string[] { return writeList(slugKey(slug), list); }

/** Effective ALLOWED verbs for a shard = ALL verbs − global-block − shard-block. */
export function getEffectiveVerbs(slug: string): string[] {
  const allow = new Set<string>(VERB_KEYS);
  for (const b of getGlobalBlock()) allow.delete(b);
  for (const b of getShardBlock(slug)) allow.delete(b);
  return [...allow];
}
