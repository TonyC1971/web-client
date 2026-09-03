/**
 * ONE definition of "this is a Discord snowflake". Do not declare a second one.
 *
 * Discord ids — users, channels, guilds, roles — are all snowflakes: decimal, currently 17-19
 * digits, drifting upward over time. The bound here is 15-20: wide enough to survive that drift
 * without a code change, narrow enough that a truncated paste or a stray identifier does not
 * pass for an id.
 *
 * 🚨 WHY THIS FILE EXISTS. This constant was declared FOUR separate times, three of them under
 * the same name `SNOWFLAKE` — and one of those three used a different bound (15-25) while the
 * rest used 15-20. Nothing was broken by that particular divergence, but the SHAPE is the one
 * that has now produced two real defects in this codebase:
 *
 *   - the leaderboard's account parser, duplicated, updated in one copy only, which silently
 *     un-fixed a shipped fix (see mgAccount.ts); and
 *   - resolveDiscordRef, where "not 15-20 digits" fell through to a NICKNAME lookup, so a
 *     user who pre-claimed a digit-only nickname could receive an admin action aimed at a
 *     mistyped id — on routes that adjust points, ban, and grant admin scopes.
 *
 * Both were invariants held in two places, and in both the fix was to delete the second place
 * rather than correct it. So this one gets deleted in advance.
 *
 * 🚨 WHAT DELIBERATELY DOES *NOT* USE THIS, and must not be "unified" into it later:
 * `mgAccount.ts` and `subToPublicNick` accept a wider 5-25 digits. They are LOOKUP SANITISERS
 * over historical shard records, not gates: a value that gets through them is used as a
 * parameterised key and resolves to null when it matches nothing. Tightening them cannot add
 * safety — it can only drop a legitimate old record off the leaderboard. Leniency reading
 * archived data and strictness making decisions are different jobs that happen to look alike.
 */

/** Decimal Discord id (user, channel, guild, role). 15-20 digits. */
export const SNOWFLAKE_RE = /^\d{15,20}$/;

/** True when `v` is a string shaped like a Discord snowflake. Never throws. */
export function isSnowflake(v: unknown): v is string {
  return typeof v === 'string' && SNOWFLAKE_RE.test(v);
}
