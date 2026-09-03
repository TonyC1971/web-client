// discordIntegration.ts — hot-reloadable Discord settings + outbound notifier
// (operator 2026-06-11: "announcements con X bot en X canal, achievements en
// otro, los eventos de muertos en otro").
//
// CONFIG lives in the SQLite `runtime_config` table (same store as the runtime
// flags) so every value is editable from the /admin panel and applies on the
// NEXT use — no container restart, no .env edit. The .env values
// (DISCORD_BOT_TOKEN / DISCORD_ANNOUNCEMENTS_CHANNEL_ID) remain as FALLBACKS
// so existing deploys keep working untouched.
//
// SECURITY: the bot token is WRITE-ONLY through the API — GET returns only
// `tokenSet: true/false`, never the value. It is stored in the same SQLite
// file as the rest of the runtime state (DATA_PATH volume — the exact same
// security domain as the .env that held it before).
//
// OUTBOUND feeds (each = one Discord channel id, empty = feed disabled):
//   announcements — the channel the LANDING PANEL reads (announcements.ts);
//                   inbound, needs Read Message History permission.
//   achievements  — "🏆 X unlocked Y" posts on auto/manual unlocks.
//   deaths        — "💀 X was slain by Y" posts from the death feed.
//   newcomers     — self-service shard moderation: a new registration lands as
//                   PENDING here, plus approve/reject outcomes (operator 2026-06-12).
//   minigames     — minigame-only events (TBH team wipes, tbh_* unlocks, future
//                   minigames) so they never mix with the normal shards' feeds
//                   (operator 2026-07-02).
//
// The notifier BATCHES: posts are queued and flushed once per 5s, max 5 per
// flush per channel (Discord's per-channel rate ceiling), queue hard-capped —
// a kill-storm degrades to dropped notifications, never to API hammering.

import { db } from './db.js';
import { SNOWFLAKE_RE as SNOWFLAKE } from './discordId.js';

const qGet = db.prepare('SELECT value FROM runtime_config WHERE key = ?');
const qSet = db.prepare('INSERT INTO runtime_config(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value');
const qDel = db.prepare('DELETE FROM runtime_config WHERE key = ?');

const KEY_TOKEN = 'discord-bot-token';
const KEY_OAUTH_ID       = 'discord-oauth-client-id';
const KEY_OAUTH_SECRET   = 'discord-oauth-client-secret';
const KEY_OAUTH_REDIRECT = 'discord-oauth-redirect-uri';
export const FEEDS = ['announcements', 'achievements', 'deaths', 'newcomers', 'minigames'] as const;
export type Feed = typeof FEEDS[number];
const feedKey = (f: Feed): string => `discord-ch-${f}`;


function rcGet(key: string): string | null {
  const r = qGet.get(key) as { value: string } | undefined;
  return r ? r.value : null;
}

/** Bot token: SQLite value, falling back to the .env one. null = not configured. */
export function getBotToken(): string | null {
  return rcGet(KEY_TOKEN) || process.env.DISCORD_BOT_TOKEN || null;
}

/** Channel id for a feed (SQLite, env fallback for announcements). null = feed off. */
export function getFeedChannel(feed: Feed): string | null {
  const v = rcGet(feedKey(feed));
  if (v && SNOWFLAKE.test(v)) return v;
  if (feed === 'announcements') {
    const env = process.env.DISCORD_ANNOUNCEMENTS_CHANNEL_ID || '';
    if (SNOWFLAKE.test(env)) return env;
  }
  return null;
}

/** OAuth app credentials for the Discord SSO flow (auth.ts reads these on
 *  EVERY login, so a panel edit applies to the next login attempt — no
 *  restart). SQLite values, .env fallbacks. */
export function getOAuth(): { clientId: string; clientSecret: string; redirectUri: string } {
  return {
    clientId: rcGet(KEY_OAUTH_ID) || process.env.DISCORD_CLIENT_ID || '',
    clientSecret: rcGet(KEY_OAUTH_SECRET) || process.env.DISCORD_CLIENT_SECRET || '',
    redirectUri: rcGet(KEY_OAUTH_REDIRECT) || process.env.DISCORD_REDIRECT_URI || 'http://localhost:8080/auth/discord/callback',
  };
}

/** Masked config for the admin GET — the token value never leaves the server. */
export function getDiscordConfigMasked(): Record<string, unknown> {
  const stored = rcGet(KEY_TOKEN);
  const channels = {} as Record<Feed, string>;
  for (const f of FEEDS) channels[f] = rcGet(feedKey(f)) ?? '';
  return {
    tokenSet: !!getBotToken(),
    tokenSource: stored ? 'panel' : (process.env.DISCORD_BOT_TOKEN ? 'env' : 'none'),
    channels,
    oauth: {
      clientId: rcGet(KEY_OAUTH_ID) ?? '',
      clientIdEffective: getOAuth().clientId,
      secretSet: !!getOAuth().clientSecret,
      secretSource: rcGet(KEY_OAUTH_SECRET) ? 'panel' : (process.env.DISCORD_CLIENT_SECRET ? 'env' : 'none'),
      redirectUri: rcGet(KEY_OAUTH_REDIRECT) ?? '',
      redirectUriEffective: getOAuth().redirectUri,
    },
  };
}

/** Apply a partial update. Empty string clears (token → env fallback; channel →
 *  feed off). Returns an error string or null. */
export function setDiscordConfig(body: { token?: unknown; channels?: unknown; oauthClientId?: unknown; oauthClientSecret?: unknown; oauthRedirectUri?: unknown }): string | null {
  if (body.oauthClientId !== undefined) {
    const v = String(body.oauthClientId).trim();
    if (v === '') qDel.run(KEY_OAUTH_ID);
    else if (!SNOWFLAKE.test(v)) return 'oauthClientId: must be the application id (a Discord snowflake)';
    else qSet.run(KEY_OAUTH_ID, v);
  }
  if (body.oauthClientSecret !== undefined) {
    const v = String(body.oauthClientSecret).trim();
    if (v === '') qDel.run(KEY_OAUTH_SECRET);
    else if (v.length < 20 || v.length > 80 || /\s/.test(v)) return 'oauthClientSecret: does not look like a Discord client secret';
    else qSet.run(KEY_OAUTH_SECRET, v);
  }
  if (body.oauthRedirectUri !== undefined) {
    const v = String(body.oauthRedirectUri).trim();
    if (v === '') qDel.run(KEY_OAUTH_REDIRECT);
    else {
      try {
        const u = new URL(v);
        if (u.protocol !== 'https:' && !(u.protocol === 'http:' && (u.hostname === 'localhost' || u.hostname === '127.0.0.1'))) {
          return 'oauthRedirectUri: must be https (http only for localhost)';
        }
      } catch { return 'oauthRedirectUri: not a valid URL'; }
      qSet.run(KEY_OAUTH_REDIRECT, v);
    }
  }
  if (body.token !== undefined) {
    const t = String(body.token).trim();
    if (t === '') qDel.run(KEY_TOKEN);
    else if (t.length < 50 || t.length > 100 || /\s/.test(t)) return 'token: does not look like a Discord bot token';
    else qSet.run(KEY_TOKEN, t);
  }
  if (body.channels !== undefined) {
    if (!body.channels || typeof body.channels !== 'object') return 'channels: must be an object';
    for (const [k, v] of Object.entries(body.channels as Record<string, unknown>)) {
      if (!(FEEDS as readonly string[]).includes(k)) return `channels: unknown feed '${k}'`;
      const id = String(v).trim();
      if (id === '') { qDel.run(feedKey(k as Feed)); continue; }
      if (!SNOWFLAKE.test(id)) return `channels.${k}: '${id}' is not a Discord channel id`;
      qSet.run(feedKey(k as Feed), id);
    }
  }
  return null;
}

// ── Outbound queue ───────────────────────────────────────────────────────────

const QUEUE_CAP = 100;
const FLUSH_MS = 5_000;
const PER_CHANNEL_PER_FLUSH = 5;
const queue: Array<{ channel: string; content: string }> = [];
let flushing = false;

// Per-source throttle for player-driven feeds (deaths): a modified client with
// a live session could report fake deaths every ~30s (the clamp allows ≤20 per
// report) and monopolise the Discord channel. One notify per (feed, key) per
// 60s caps that to a trickle without affecting honest play (you don't legit die
// twice a minute). Self-pruning so the map stays bounded.
const NOTIFY_THROTTLE_MS = 60_000;
// MINIGAMES feed (operator 2026-07-05 "no pongas tantos eventos de un mismo jugador seguidos —
// uno por cada hora máximo"): TBH wipes recur naturally every few minutes of idle play, so the
// generic 60s trickle still spammed the channel with the same player. One post per player per
// HOUR on this feed; the other feeds keep the 60s anti-grief cap.
const MINIGAMES_THROTTLE_MS = 3_600_000;
const lastNotify = new Map<string, number>();
function throttled(feed: Feed, key: string): boolean {
  const k = `${feed}:${key}`;
  const now = Date.now();
  const ttl = feed === 'minigames' ? MINIGAMES_THROTTLE_MS : NOTIFY_THROTTLE_MS;
  if (now - (lastNotify.get(k) ?? 0) < ttl) return true;
  lastNotify.set(k, now);
  if (lastNotify.size > 5000) { for (const [mk, t] of lastNotify) if (now - t > MINIGAMES_THROTTLE_MS) lastNotify.delete(mk); }
  return false;
}

/** Neutralise Discord formatting / mention metacharacters in an untrusted name
 *  so a crafted character name can't break the message or spoof a mention
 *  (allowed_mentions:none already blocks pings; this also stops markdown
 *  injection like `**bold**`, `||spoiler||`, backtick code blocks). */
export function mdSafe(s: string): string {
  // eslint-disable-next-line no-control-regex
  return String(s).replace(/[\x00-\x1f\x7f]/g, '').replace(/[\\`*_~|<>@#:]/g, '').trim().slice(0, 40);
}

/** Queue one message for a feed's channel. No-op when the feed/bot is off.
 *  `throttleKey` (optional) rate-limits player-driven feeds per source. */
export function notifyFeed(feed: Feed, content: string, throttleKey?: string): void {
  if (feed === 'announcements') return; // inbound-only feed — never post into it
  const channel = getFeedChannel(feed);
  if (!channel || !getBotToken()) return;
  if (throttleKey && throttled(feed, throttleKey)) return;
  if (queue.length >= QUEUE_CAP) return; // storm → drop, never hammer the API
  queue.push({ channel, content: String(content).slice(0, 1800) });
}

async function flush(): Promise<void> {
  if (flushing || queue.length === 0) return;
  flushing = true;
  try {
    const token = getBotToken();
    if (!token) { queue.length = 0; return; }
    const perChannel = new Map<string, number>();
    const keep: Array<{ channel: string; content: string }> = [];
    const batch = queue.splice(0);
    for (let i = 0; i < batch.length; i++) {
      const msg = batch[i];
      const used = perChannel.get(msg.channel) ?? 0;
      if (used >= PER_CHANNEL_PER_FLUSH) { keep.push(msg); continue; }
      perChannel.set(msg.channel, used + 1);
      try {
        const res = await fetch(`https://discord.com/api/v10/channels/${msg.channel}/messages`, {
          method: 'POST',
          headers: {
            Authorization: `Bot ${token}`,
            'Content-Type': 'application/json',
            // Generic on purpose: this file ships to self-hosters, and a User-Agent naming a repository
              // their visitors cannot open is both wrong and a pointer at somebody else's project.
              'User-Agent': 'uo-webclient (self-hosted, 1.0)',
          },
          body: JSON.stringify({ content: msg.content, allowed_mentions: { parse: [] } }),
        });
        if (res.status === 429) { keep.push(msg); }           // rate-limited → retry next flush
        else if (!res.ok) {
          console.warn(`[discord] post to ${msg.channel} failed: ${res.status} ${await res.text().then((t) => t.slice(0, 120)).catch(() => '')}`);
        }
      } catch (e) {
        // Network down — keep this message AND the not-yet-attempted tail of
        // the batch (review fix: `break` alone silently dropped the tail,
        // since splice(0) had already removed it from the queue).
        console.warn(`[discord] post failed: ${(e as Error).message}`);
        keep.push(...batch.slice(i));
        break;
      }
    }
    if (keep.length) queue.unshift(...keep.slice(0, QUEUE_CAP));
  } finally {
    flushing = false;
  }
}
setInterval(() => { void flush(); }, FLUSH_MS).unref();

/** Admin "send a test message" — returns an error string or null. */
export async function sendTest(feed: Feed): Promise<string | null> {
  const channel = getFeedChannel(feed);
  const token = getBotToken();
  if (!token) return 'no bot token configured (panel or DISCORD_BOT_TOKEN)';
  if (!channel) return `no channel configured for feed '${feed}'`;
  try {
    const res = await fetch(`https://discord.com/api/v10/channels/${channel}/messages`, {
      method: 'POST',
      headers: { Authorization: `Bot ${token}`, 'Content-Type': 'application/json', 'User-Agent': 'uonexus-webclient (test, 1.0)' },
      body: JSON.stringify({ content: `✅ UO Nexus test — feed **${feed}** wired to this channel.`, allowed_mentions: { parse: [] } }),
    });
    if (!res.ok) return `Discord API ${res.status}: ${(await res.text().catch(() => '')).slice(0, 160)}`;
    return null;
  } catch (e) {
    return (e as Error).message;
  }
}
