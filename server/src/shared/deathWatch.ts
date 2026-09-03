// deathWatch.ts — derive the killer's name from the SERVER's own packets, not from the client.
//
// 🚨 WHY THIS EXISTS. The public landing widget shows "<victim> † by <killer>". Until now that
// killer string arrived in the body of POST /api/metrics/report — free text from the browser. The
// numeric fields of that same object are clamped precisely because the object is not trusted; this
// one is a string and the clamp never looked at it. So anybody with a live session (a guest works)
// could make the front page print 40 characters of their choosing.
//
// 🚨 AND WHY NOT A CHARACTER FILTER, which is the obvious reflex. An advert or an insult is spelled
// with letters and spaces: "discord gg evilshard" passes any letters-only rule and still reads as an
// invite to everyone who sees it. Restricting the CHARSET would leave the capability untouched while
// looking like a defence, which is worse than leaving it visibly open. The root is not which
// characters get published, it is that unverified client text gets published at all.
//
// The fix changes the VOCABULARY instead. Every ingredient the client uses is already in the
// server->client stream, and this proxy already decompresses that stream (the Huffman s2c pass it
// does for the WASM client). So the same derivation happens here, from bytes the shard sent:
//
//   0x2C  DeathScreen      action != 1  -> this player just died   (matches PacketHandlers.cs)
//   0x11 0x1C 0xAE         serial + name, as the SERVER spelled it
//   0xC1 0xCC              same, localized variants
//   0x98  UpdateName       serial + name, the dedicated name packet
//   0x05  Attack Request   client->server, the serial the player attacked
//
// The result is that the published string can only ever be a name this shard sent during this
// session. The worst a forger achieves is blaming a mobile that really is on their screen.
//
// 🚨 OPERATOR CONSTRAINT THIS RESPECTS (2026-08-04): "la premisa es que los shards que se añadan a
// la web no tengan que hacer ediciones extras innecesarias". Nothing here needs a shard change, now
// or for any shard added later — 0x2C/0x98/0x1C are core UO protocol, not ModernUO specifics.
//
// ⚠️ TWO HONEST LIMITS.
//   1. Only plaintext streams. Where the connection is encrypted this sees nothing, no name is
//      published, and the widget falls back to "† slain". That fails to the SAFE side.
//   2. It is still an INFERENCE, the same one the client makes: the last mobile the player
//      ATTACKED, not the last that attacked them — the UO protocol carries no "killer" field. What
//      this removes is the ability to invent the string, not the imprecision.

/** UO packet lengths by opcode; -1 = variable, length in the two bytes after the opcode.
 *
 *  🚨 Transcribed from source/cuo/.../Network/PacketsTable.cs, with a quirk worth recording rather
 *  than rediscovering: the upstream array holds 255 entries (0x00-0xFE) and the comment on its LAST
 *  line says "ff" while the entry actually sits at 0xFE. There is no 0xFF entry, so it is added
 *  here as variable-length. Found by enumerating the declared indices, not by reading down the
 *  column — an earlier eyeball pass "found" three gaps that were only a regex tripping over
 *  comments with trailing text. */
const PACKET_LEN = new Int16Array([
  104, 5, 7, -1, 2, 5, 5, 7, 14, 5, 11, 266, -1, 3, -1, 61,
  215, -1, -1, 10, 6, 9, 1, -1, -1, -1, -1, 37, -1, 5, 4, 8,
  19, 8, 3, 26, 7, 20, 5, 2, 5, 1, 5, 2, 2, 17, 15, 10,
  5, 1, 2, 2, 10, 653, -1, 8, 7, 9, -1, -1, -1, 2, 37, -1,
  201, -1, -1, 553, 713, 5, -1, 11, 73, 93, 5, 9, -1, -1, 6, 2,
  -1, -1, -1, 2, 12, 1, 11, 110, 106, -1, -1, 4, 2, 73, -1, 49,
  5, 9, 15, 13, 1, 4, -1, 21, -1, -1, 3, 9, 19, 3, 14, -1,
  28, -1, 5, 2, -1, 35, 16, 17, -1, 9, -1, 2, -1, 13, 2, -1,
  62, -1, 2, 39, 69, 2, -1, -1, 66, -1, -1, -1, 11, -1, -1, -1,
  19, 65, -1, 99, -1, 9, -1, 2, -1, 26, -1, 258, 309, 51, -1, -1,
  3, 9, 9, 9, 149, -1, -1, 4, -1, -1, 5, -1, -1, -1, -1, 13,
  -1, -1, -1, -1, -1, 64, 9, -1, -1, 3, 6, 9, 3, -1, -1, -1,
  36, -1, -1, -1, 6, 203, 1, 49, 2, 6, 6, 7, -1, 1, -1, 78,
  -1, 2, 25, -1, -1, -1, -1, -1, -1, 268, -1, -1, 9, -1, -1, -1,
  -1, -1, 10, -1, -1, -1, 5, 12, 13, 75, 3, -1, -1, -1, 10, 21,
  -1, 9, 25, 26, -1, 21, -1, -1, 106, -1, -1, -1, -1, -1, -1, -1,
]);

/** Offset of the 30-byte ASCII name, measured from the START of the packet.
 *
 *  🚨 EVERY ONE OF THESE IS A VARIABLE-LENGTH PACKET, so the payload begins at +3 (opcode plus the
 *  two length bytes) and the serial lives at +3, not +1. Getting that wrong does not crash — it
 *  reads a plausible-looking string from the wrong place, which is the failure mode this module
 *  exists to avoid, so each offset below was taken from ClassicUO's own reader rather than derived:
 *
 *    0x11 CharacterStatus    serial(4) name[30]                                      -> 3+4  = 7
 *    0x1C Talk               serial(4) graphic(2) type(1) hue(2) font(2) name[30]    -> 3+11 = 14
 *    0xAE UnicodeTalk        ...as 0x1C plus lang[4]                                 -> 3+15 = 18
 *    0xC1 DisplayCliloc      ...as 0x1C plus cliloc(4)                               -> 3+15 = 18
 *    0xCC DisplayClilocAffix ...as 0xC1 plus an EXTRA flags byte read only for 0xCC  -> 3+16 = 19
 *
 *  That last one is the trap: 0xC1 and 0xCC share a handler and look identical until the line
 *  `flags = p[0] == 0xCC ? p.ReadUInt8() : 0` shifts 0xCC by one byte. */
const NAME_AT: Record<number, number> = {
  0x11: 7,
  0x1c: 14,
  0xae: 18,
  0xc1: 18,
  0xcc: 19,
};

/** Payload start for a variable-length packet: opcode + the two length bytes. */
const VAR_SERIAL_AT = 3;

/** How many serial->name pairs one session may hold. A busy screen shows a few dozen mobiles; the
 *  cap is what stops a long session — or a hostile stream of invented serials — from growing
 *  without bound on a process that also relays every other player. Oldest-first eviction. */
export const MAX_NAMES = 512;

/** Matches the 40 the widget already truncates to, so what is stored is what can be shown. */
const NAME_MAX = 40;

function readName(buf: Buffer, at: number, max: number): string {
  let end = at;
  const stop = Math.min(buf.length, at + max);
  while (end < stop && buf[end] !== 0) end++;
  // Control bytes are stripped rather than making the packet invalid: a shard may legitimately
  // send odd bytes, and the widget must never receive anything that could disturb its rendering.
  // eslint-disable-next-line no-control-regex
  return buf.toString('latin1', at, end).replace(/[\x00-\x1f\x7f]/g, '').trim().slice(0, NAME_MAX);
}

/**
 * Walk packet boundaries, invoking `onPacket` per packet. Returns bytes consumed — less than the
 * buffer when it ends mid-packet, so the caller keeps the remainder for the next chunk. Returns -1
 * when the stream is desynchronised and must not be parsed further.
 *
 * Measured on the NAS inside the proxy container: 0.49 us/KiB and 10.9 ns/packet over 4 MiB of a
 * plausible mix. The Huffman decode this same stream already pays is 136.4 us/KiB, so the walk adds
 * about 0.36%. Even at a pessimistic 5-byte mean packet it stays near 2 us/KiB.
 */
export function walkPackets(buf: Buffer, onPacket: (op: number, at: number, len: number) => void): number {
  let o = 0;
  const n = buf.length;
  while (o < n) {
    const op = buf[o];
    let len = PACKET_LEN[op];
    if (len < 0) {
      if (o + 3 > n) break;                  // length header not in this chunk yet
      len = (buf[o + 1] << 8) | buf[o + 2];
      // A variable packet shorter than its own header is a desynchronised stream, not a packet.
      // Refusing is the only safe move: guessing would walk garbage for the rest of the session
      // and could manufacture "names" out of unrelated bytes — which is the whole thing this
      // module exists to prevent.
      if (len < 3) return -1;
    }
    if (len <= 0) return -1;
    if (o + len > n) break;                  // packet straddles the chunk boundary
    onPacket(op, o, len);
    o += len;
  }
  return o;
}

/**
 * Per-session observer. Fed the two directions of one player's stream; reports a death with the
 * killer's name when it can name one, and with null when it cannot.
 *
 * Deliberately holds no reference to the session, the database or the shard: it is a pure
 * accumulator over bytes, which is what makes it testable without a socket and cheap to reason
 * about on the relay's hot path.
 */
export class DeathWatch {
  private names = new Map<number, string>();
  private lastAttacked = 0;
  /** Set once the stream desynchronises: parsing stops for good rather than inventing names. */
  private desynced = false;

  /** Bytes of a packet that straddled a chunk boundary, waiting for the rest.
   *
   *  🚨 WITHOUT THIS THE WHOLE MODULE IS DEAD IN PRODUCTION, which the first version was. TCP
   *  delivers arbitrary chunk boundaries and a name packet is routinely split; discarding the tail
   *  leaves the next chunk starting mid-packet, so the walker reads a length out of the middle of
   *  a name and the session desynchronises for good. Every gate passed anyway because they all fed
   *  whole packets per call — the one thing a real stream never does. */
  private carryS2C: Buffer = Buffer.alloc(0);
  private carryC2S: Buffer = Buffer.alloc(0);

  /** Ceiling on a held tail, and the bound is EXACT rather than a round number.
   *
   *  A variable packet declares its length in two bytes, so no packet can need more than 65535. A
   *  leftover longer than that therefore cannot be a packet still waiting for data — more bytes
   *  would never complete it — so it is garbage and the stream is desynchronised.
   *
   *  🚨 THE FIRST VERSION CAPPED THE JOINED BUFFER AT 96 KiB INSTEAD, measured BEFORE walking, and
   *  that could have killed a healthy session: a genuine 65535-byte packet still arriving plus one
   *  64 KiB chunk is 128 KiB of perfectly legal in-flight data. Bounding the LEFTOVER after the
   *  walk cannot false-positive, because anything the walker leaves behind is by definition an
   *  unfinished packet.
   *
   *  ⚠️ AND THIS BRANCH IS UNREACHABLE TODAY — said here rather than left to look like a tested
   *  guard. A declared length is two bytes, so it is at most 65535 and the leftover can never
   *  exceed it; removing this line leaves the whole gate green, which by the project's own rule
   *  means it is not evidenced by anything. What IS evidenced is the INVARIANT: the test measures
   *  the peak tail across a worst-case stream and asserts it stays within one packet. This line is
   *  the belt that keeps that true if the length handling above ever changes. */
  private static readonly MAX_CARRY = 65535;

  /** A UO session opens with a raw 4-byte seed (or a batched seed + 0x91), which is NOT a packet:
   *  walking it reads the seed's first byte as an opcode. Guarded by shape rather than by counting
   *  messages so a reconnect or a split first frame cannot slip past it. */
  private c2sStarted = false;

  /** Latches on the first 0x2C of a death — some servers send several, and each would otherwise
   *  report the same death again. Cleared by `rearm()` when the player is alive again. */
  private deathLatched = false;

  get isDesynced(): boolean { return this.desynced; }
  get trackedNames(): number { return this.names.size; }

  /** Client -> server. Only the attack request matters, and only as a POINTER: it selects which
   *  remembered name to use and can never introduce one. That asymmetry is the security property —
   *  a forged 0x05 blames a different mobile, it does not publish new text. */
  onClientToServer(buf: Buffer): void {
    if (this.desynced) return;

    // Skip the opening seed. The shapes are exactly the ones the relay's own 0xEF swap handles:
    // 4 bytes alone, 65 with 0x91 first, or 69 batched. Anything else means the handshake is past.
    if (!this.c2sStarted) {
      const isSeed = buf.length === 4
        || (buf.length === 65 && buf[0] === 0x91)
        || (buf.length === 69 && buf[4] === 0x91);
      if (isSeed) return;
      this.c2sStarted = true;
    }

    const stream = this.join(this.carryC2S, buf);
    const consumed = walkPackets(stream, (op, at) => {
      if (op === 0x05) this.lastAttacked = stream.readUInt32BE(at + 1);
    });
    if (consumed < 0) { this.desynced = true; return; }
    this.carryC2S = this.keepTail(stream, consumed);
  }

  /**
   * Server -> client, already decompressed. Returns the killer's name when this chunk contained
   * the player's death, or undefined when it did not. `null` means "died, no name known" — which
   * the caller must publish as a nameless death rather than falling back to anything client-sent.
   */
  onServerToClient(buf: Buffer): string | null | undefined {
    if (this.desynced) return undefined;
    const stream = this.join(this.carryS2C, buf);
    let died = false;
    const consumed = walkPackets(stream, (op, at, len) => {
      if (op === 0x2c) {
        // PacketHandlers.DeathScreen: action != 1 is the death, everything else is ghost chatter.
        if (len >= 2 && stream[at + 1] !== 1 && !this.deathLatched) { this.deathLatched = true; died = true; }
        return;
      }
      if (op === 0x98) {
        // UpdateName: serial(4) then the name runs to the end of the packet.
        if (len > 7) this.remember(stream.readUInt32BE(at + VAR_SERIAL_AT), readName(stream, at + 7, len - 7));
        return;
      }
      const off = NAME_AT[op];
      if (off !== undefined && len >= off + 30) {
        this.remember(stream.readUInt32BE(at + VAR_SERIAL_AT), readName(stream, at + off, 30));
      }
    });
    if (consumed < 0) { this.desynced = true; return undefined; }
    this.carryS2C = this.keepTail(stream, consumed);
    if (!died) return undefined;
    return this.names.get(this.lastAttacked) ?? null;
  }

  /** Re-arm after the player is observably alive again, mirroring the client's own death latch. */
  rearm(): void { this.deathLatched = false; }

  /** Prepend a held tail to the new chunk. No cap here on purpose — see MAX_CARRY. */
  private join(carry: Buffer, buf: Buffer): Buffer {
    return carry.length === 0 ? buf : Buffer.concat([carry, buf]);
  }

  /** Whatever the walker could not consume, kept for the next chunk. Copied rather than sliced: a
   *  subarray keeps the whole original chunk alive behind it, which on a relay is a slow leak. */
  private keepTail(stream: Buffer, consumed: number): Buffer {
    if (consumed >= stream.length) return Buffer.alloc(0);
    const tail = stream.subarray(consumed);
    if (tail.length > DeathWatch.MAX_CARRY) { this.desynced = true; return Buffer.alloc(0); }
    return Buffer.from(tail);
  }

  /** Test seam: how many bytes are being held for the next chunk. */
  get carryBytes(): number { return this.carryS2C.length + this.carryC2S.length; }

  private remember(serial: number, name: string): void {
    if (!serial || !name) return;
    // Re-insert so recently seen names are youngest — eviction below drops the oldest.
    if (this.names.has(serial)) this.names.delete(serial);
    this.names.set(serial, name);
    while (this.names.size > MAX_NAMES) {
      const oldest = this.names.keys().next().value as number;
      this.names.delete(oldest);
    }
  }
}
