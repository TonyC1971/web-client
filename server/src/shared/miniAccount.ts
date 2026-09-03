/**
 * What a player's account is CALLED on the minigames shard.
 *
 * 🚨 WHY THIS IS ITS OWN MODULE. This name is computed, never looked up, and it was computed in
 * two places: UOProxy builds it at login (the authoritative one, matched by MiniAutoLogin on the
 * shard) and AssetServer's `webAccountFor` rebuilt it to answer /api/gear without a live session.
 * The comment there said the rule came "straight out of MiniAutoLogin's own rule (see UOProxy
 * miniAccount)" — an assertion nothing checked, which is the exact shape that has cost this repo
 * a mute leaderboard and a pinned cache buster already.
 *
 * The stakes are named in UOProxy's own comment: "a mismatch is not a bug report, it is every
 * mini login failing at once." One of the two copies decides who you are when you log in; the
 * other decides whose backpack the website shows you. They cannot be allowed to disagree.
 *
 * ⚠️ A THIRD copy exists and cannot be collapsed: `MiniAutoLogin.username` on the shard, in C#,
 * in a checkout that is not part of this repo. That one is held by review and by the shape of
 * the failure (total, immediate, at login) rather than by a test — stated here so its absence is
 * a known limit and not an oversight.
 */

/** Discord-authenticated player: the account is the Discord id with a `d` in front. */
export function miniAccountForDiscord(discordSub: string): string {
  return 'd' + discordSub;
}

/** One account per guest BROWSER, keyed on the hex the guest session already carries. */
export function miniAccountForGuest(guestSub: string): string {
  return 'g' + guestSub.replace(/^guest-/, '');
}

/**
 * Spectators get a pooled BLANK account, same hex, different prefix.
 *
 * Kept beside the other two because the prefix letter is the whole difference and reading them
 * together is what stops a fourth one being invented.
 */
export function miniAccountForSpectator(sub: string): string {
  // 🚨 THE SAME SPECTATOR ARRIVES SPELLED TWO WAYS. UOProxy calls this at login with the GUEST
  // sub (`guest-<hex>`), and stores the session's own sub as `spec-<hex>` — same hex, same
  // person, and later callers hold the second spelling. Stripping only `guest-` produced
  // `sspec-<hex>` for those: a name that matches nothing, silently.
  //
  // Accepting both here rather than at the call site, because peeling the prefix by hand is how
  // a fourth copy of this rule gets written — which is the thing this module exists to prevent.
  return 's' + sub.replace(/^(guest|spec)-/, '');
}

/**
 * The account for a WEB session's `sub`, or null when the name cannot be derived from it alone.
 *
 * 🚨 A SPECTATOR RETURNS NULL, deliberately. A spectator's own account (`s<hex>`) exists, but the
 * character they are watching belongs to somebody else, so "the character behind this sub" has
 * no answer — the web must not guess one. Returning null makes that inexpressible case explicit
 * instead of quietly handing back the spectator's own empty account.
 */
export function miniAccountForSub(sub: string): string | null {
  if (!sub) return null;
  if (sub.startsWith('spec-')) return null;
  if (sub.startsWith('guest-')) return miniAccountForGuest(sub);
  return miniAccountForDiscord(sub);
}
