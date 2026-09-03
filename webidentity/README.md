# WebIdentity — let your shard see each web player's real IP

Without this, **your server sees every browser player as one address**: the relay's.

That is not cosmetic. Every per-IP defence your shard has — connection throttles, ping limits,
account-creation limits, the audit log — treats all of them as the same person. One player's
reconnect storm gets the relay throttled and locks *everyone* out, and your logs cannot tell two
players apart.

WebIdentity fixes it. The relay puts a small packet at the front of each connection carrying that
player's real address and a shared secret; a handler on your shard reads it and uses that address
everywhere it would have used the relay's. It is the same packet (`0xA4`) the official ClassicUO web
client uses, byte for byte.

**It is off until you configure both halves.** Do the shard first.

---

## 1. Install the handler on your shard

Pick your server. Copy the files into your scripts folder and restart.

### ModernUO

Copy all three into `Projects/UOContent/Custom/ClassicUO/`:

```
modernuo/Network.cs
modernuo/Network.ModernUO.cs
modernuo/WebIdentity.cs
```

⚠️ **All three, and only the ModernUO ones.** `WebIdentity.cs` will not compile without the two
`Network` files, and the `Network.<emulator>.cs` for the wrong server will not compile at all.

### ServUO / RunUO

Copy all three into `Scripts/ClassicUO/`:

```
runuo/Network.cs
runuo/Network.ServUO.cs
runuo/WebIdentity.cs
```

### Sphere (Source-X)

Sphere has no upstream reference, so this is a source patch rather than a drop-in. See
[`sphere/README.md`](sphere/README.md) — there is an automated script (`apply_webidentity.py`) and a
[manual walkthrough](sphere/INSTALL_MANUAL.md) if you would rather see every edit. You rebuild Sphere
afterwards.

### Something else on .NET

[`dotnet/webidentity-dotnet.zip`](dotnet/) is a standalone parser you can call from your own code.
The packet is documented in [DESIGN.md](DESIGN.md#packet-0xa4--wire-format).

---

## 2. Set the secret, in two places

**On your shard.** ModernUO / ServUO / RunUO read it from the handler's configuration; Sphere reads
it from `sphere.ini`. The per-server sections above say exactly where.

**On the relay**, in your shard's YAML:

```yaml
webIdentity:
  enabled: true
  secret: use-the-same-long-random-string-on-both-sides
```

The two must match exactly. **Minimum 16 characters**, and the relay refuses to start with less —
the shard trusts whatever arrives with a valid secret, so a guessable one is worse than leaving this
off.

Generate one:

```bash
node -e "console.log(require('crypto').randomBytes(24).toString('hex'))"
```

---

## 3. Check it worked

Restart the relay and connect a player. The relay logs one line per session:

```
[webident-pre] sent 0xA4 preamble (149B) slug=myshard encrypt=none userId=… role=…
```

On the shard side, your log should now show real client addresses instead of the same one repeated.
That is the whole point — if the addresses still all match, the handler is not reading the packet.

**If it does not work**, in this order:

| Symptom | Usually |
|---|---|
| No `[webident-pre]` line at all | `enabled: true` missing, or the secret is under 16 characters. |
| The relay logs it, the shard ignores it | The handler is not installed, or you copied the files for the wrong emulator. |
| Shard says "invalid client" after this | The shard-side handler is not registered, so your server is reading 149 bytes it does not understand as part of the login. Take the block back out of the YAML while you sort the shard side. |
| Everything is fine but addresses are still identical | Your players really are behind one address (same household, same VPN). Check with two different networks. |

---

## What it does not do

- **It is not authentication.** It tells your shard where a connection came from. It does not say
  who the person is, and your accounts still work exactly as they did.
- **It does not encrypt anything.** The secret proves the packet came from your relay; it is not a
  password and it is not hidden from anyone able to watch traffic between relay and shard. Keep those
  two on a network you trust, which they normally already are.
- **It does not touch desktop players.** The handler only reacts to packets marked `CUOWEB`;
  anything else falls through untouched.

The reasoning behind each of those, the packet layout, and the full per-emulator detail are in
[DESIGN.md](DESIGN.md).

---

## Licence

`modernuo/` and `runuo/` are the reference implementation from
[ClassicUO/packets](https://github.com/ClassicUO/packets), redistributed under its licence — see
[LICENSE.upstream.md](LICENSE.upstream.md). `modernuo/WebIdentity.cs` carries one adaptation for
current ModernUO (`SpanReader` in place of the removed `CircularBufferReader`), noted at the top of
the file. The Sphere patch is ours.
