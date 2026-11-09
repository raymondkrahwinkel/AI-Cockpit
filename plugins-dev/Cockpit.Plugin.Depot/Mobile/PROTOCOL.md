# Cockpit mobile protocol, v1 (AC-2222)

End-to-end encryption between the phone app and the Cockpit, with Depot as the relay that carries the bytes. Only the
two devices can read a message. Depot cannot open one, and it cannot slip in a key of its own without the handshake
failing. That is what enforces "nobody else, not an Admin or Operator either" (DEP-618) technically, not just as a promise.

Built only from primitives that both platforms ship:

| Primitive | .NET | Android |
|---|---|---|
| ECDH on P-256 | `ECDiffieHellman` | Android Keystore, `PURPOSE_AGREE_KEY` |
| HKDF-SHA256 (RFC 5869) | `HKDF` | `javax.crypto.Mac` (`HmacSHA256`) |
| HMAC-SHA256 | `HMACSHA256` | `javax.crypto.Mac` |
| AES-256-GCM, 96-bit nonce, 128-bit tag | `AesGcm` | `javax.crypto.Cipher` (`AES/GCM/NoPadding`) |

Code: `MobileKeyStore`, `PairingPayload`, `MobileHandshake` and `MobileFrameCipher` in this folder. Test vectors:
`protocol-vectors.json` next to this file. The C# side is tested against them (`MobileProtocolVectorTests`), and the
Kotlin side (AC-2226) has to pass the same vectors.

## 1. Notation

- `‖` is byte concatenation. `UTF8(s)` is the UTF-8 bytes of `s`, with no terminator and no length prefix.
- **Public key**: a P-256 point as the 65-byte uncompressed SEC1 encoding `0x04 ‖ X ‖ Y`, with X and Y as 32-byte
  big-endian values. This is the only form on the wire. A receiver refuses any other length, any other first byte,
  and any point that is not on the curve, before it runs an agreement with it.
- **ECDH(a, B)**: the raw shared secret, which is the 32-byte big-endian X coordinate of `a·B`. It is not hashed
  (.NET `DeriveRawSecretAgreement`, Java `KeyAgreement("ECDH").generateSecret()`).
- **base64url**: RFC 4648 §5, without padding.
- In the vectors every byte string is lowercase hex, unless the field says otherwise.

## 2. QR code (pairing)

The Cockpit asks Depot for a one-time pairing id (DEP-628: valid for 5 minutes, single use, bound to the user) and
shows a QR code. The QR text is:

```
base64url( UTF8( JSON ) )
```

The JSON object has these fields, written in this order (a reader must not depend on the order):

| Field | Type | Content |
|---|---|---|
| `v` | number | `1` |
| `depot` | string | The Depot base URL the phone talks to |
| `pairingId` | string | The id Depot issued, as Depot wrote it |
| `cockpitPub` | string | base64url of the Cockpit's static public key for this Depot connection (65 bytes → 87 characters) |
| `pairingSecret` | string | base64url of 32 random bytes (→ 43 characters), fresh for each QR code |

`pairingSecret` exists only in the Cockpit's memory while the code is on screen, and it reaches the phone only through
the camera. It is never stored, never logged and never sent to Depot. Depot only sees `pairingId`, because it
issued it.

**Single use and expiry, on the Cockpit's side.** The Cockpit enforces both itself and does not rely on Depot for them:

- A QR code expires 5 minutes after it is made (`PairingPayload.Lifetime`, the same 5 minutes as DEP-628).
- It pairs at most one phone. The handshake that pairs uses the code up with a compare-and-set, so of two pairing
  handshakes that both carry the right MAC, exactly one succeeds.
- The secret is wiped from memory as soon as the code pairs a phone, expires or is discarded (`Discard`, for example
  when the operator closes the dialog).
- A handshake that is refused (§ 3) does not use the code up, so a frame with a bad key cannot burn it for the real
  phone.

A phone refuses a payload whose `v` it does not know.

## 3. Handshake: the phone's first frame and the Cockpit's reply

The relay (DEP-629) opens a stream between the phone and the Cockpit and gives it a `streamId`. The first frame from
the phone is a handshake, sent in the clear:

| Offset | Bytes | Field |
|---|---|---|
| 0 | 1 | version, `0x01` |
| 1 | 1 | flag: `0x00` = a phone that is already paired, `0x01` = first pairing |
| 2 | 65 | `phonePub`: the phone's static public key (Android Keystore, never leaves the device) |
| 67 | 65 | `ephPub`: a new ephemeral public key, generated for this stream and thrown away when it ends |
| 132 | 32 | `mac`, only when the flag is `0x01` |

So the frame is exactly 132 bytes with flag `0x00` and exactly 164 bytes with flag `0x01`. Any other length, version
or flag is refused.

```
mac = HMAC-SHA256( key = pairingSecret, UTF8(pairingId) ‖ phonePub )
```

Because `phonePub` has a fixed length, the split between `pairingId` and `phonePub` is unambiguous.

**What the Cockpit checks.**

- **Flag `0x01`** is accepted only while a QR code is open, meaning not used and not expired (§ 2). The Cockpit
  recomputes `mac` with its own `pairingId` and `pairingSecret`, never with values from the frame, and compares in
  constant time (`CryptographicOperations.FixedTimeEquals`). After that it checks both points and derives the keys
  (§ 4). Only once all of that holds does it use the code up. `phonePub` remains provisional until key confirmation (§ 4).
- **Flag `0x00`** is accepted only if `phonePub` was paired earlier.
- `phonePub` and `ephPub` must be points on P-256 (§ 1). The point at infinity cannot be encoded in this form, and an
  all-zero X and Y is not on the curve, so both are refused.

Every refusal looks the same: no reason, no exception that carries frame content, no log line with the frame. The
caller closes the stream.

**The Cockpit's reply.** After it accepts the handshake, the Cockpit sends one frame back, in the clear:

| Offset | Bytes | Field |
|---|---|---|
| 0 | 1 | version, `0x01` |
| 1 | 65 | `cockpitEphPub`: a new ephemeral public key that the Cockpit generates for this handshake only |

This frame is exactly 66 bytes. The Cockpit throws the private half away as soon as it has derived the keys (§ 4). The
phone waits for this reply and sends no encrypted frame before it arrives. It refuses a reply with any other length or
version, or with a point that is not on the curve.

**Why this stops Depot.**

- If Depot swaps `phonePub` for a key of its own, the MAC no longer matches, because Depot does not know
  `pairingSecret`.
- Depot cannot swap `cockpitPub`: that key reached the phone through the QR code, not through Depot.
- If Depot swaps `ephPub` or `cockpitEphPub`, the two sides derive different keys. The first encrypted frame then fails
  its tag, and Depot still cannot compute either side's keys.
- If Depot plays a recorded handshake again, the Cockpit answers with a new `cockpitEphPub`. The keys therefore differ
  from those of the recorded stream, and none of the recorded frames opens.
- If Depot changes the version or the flag, the frame is either refused, or the keys come out different, because both
  are in the HKDF info (§ 4).

## 4. Session keys per stream

Both sides compute:

```
ikm  = ECDH(eph_phone, cockpit) ‖ ECDH(phone, cockpit) ‖ ECDH(eph_phone, eph_cockpit)     (96 bytes)
salt = UTF8(streamId)
bind = version ‖ flag ‖ cockpitPub ‖ cockpitEphPub ‖ phonePub ‖ ephPub                     (1 + 1 + 4 × 65 bytes)
k_pc = HKDF-SHA256(ikm, salt, info = ASCII("cockpit-mobile v1 phone-to-cockpit") ‖ bind, L = 32)
k_cp = HKDF-SHA256(ikm, salt, info = ASCII("cockpit-mobile v1 cockpit-to-phone") ‖ bind, L = 32)
```

`version` and `flag` are the first two bytes of the phone's handshake frame.

The phone computes `ECDH(eph_phone_private, cockpitPub)`, `ECDH(phone_private, cockpitPub)` and
`ECDH(eph_phone_private, cockpitEphPub)`. The Cockpit computes the same three values with its static private key (from
`ephPub` and `phonePub`) and its ephemeral private key (from `ephPub`).

- `k_pc` encrypts phone → Cockpit, `k_cp` encrypts Cockpit → phone. Two keys, so the two directions never share a nonce
  space, and a frame sent back to its own sender does not open.
- The two static terms bind the keys to both paired devices: only the holder of the phone's Keystore key and the holder
  of the Cockpit's key can compute them. A relay that swaps an ephemeral key only breaks the stream (denial of
  service), and learns nothing from it.
- The ephemeral-ephemeral term makes the keys new for every handshake. It also gives forward secrecy on both sides:
  once both ephemeral private keys are gone, a later leak of either static key does not open a past stream.
- `bind` ties the keys to exactly this protocol version, this kind of handshake, this pairing and this handshake's
  ephemeral keys.

**Key confirmation (AC-2239), on every stream, including a known phone.** Immediately after the clear reply, the
Cockpit sends exactly one encrypted `ready` in the § 6 envelope, at counter 0:

```json
{"type":"ready","id":"ready","body":{}}
```

The phone opens this frame before it saves `cockpitPub` or shows Paired/Secure. A failing tag or no `ready` within
15 seconds means Refused, with no new stored pairing. After opening it, the phone answers with its own encrypted
`ready` envelope at counter 0 (`id` is any nonempty string, `body` is an object, normally `{}`). Application frames
then continue at counter 1 in both directions. The vectors' `ready` object supplies counter-0 frames in both directions;
the separate `frames` array tests the cipher in isolation and starts its own counters at 0.

The Cockpit keeps a new `phonePub` only in memory until that valid phone `ready` arrives. Only then does it store the
key and pairing and open the assistant channel. Before confirmation, a frame that fails authentication or is not a
`ready` envelope ends the stream. At 15 seconds after sending the clear reply, an unconfirmed stream and its provisional
pairing are discarded, with no new stored phone key or channel. A late `ready` cannot revive it; the operator needs a
new QR code. A failed known-phone stream retains its earlier pairing but grants no access on that stream.

## 5. Frames

Every frame after the handshake, in either direction:

| Offset | Bytes | Field |
|---|---|---|
| 0 | 1 | version, `0x01` |
| 1 | 8 | `counter`, unsigned 64-bit big-endian |
| 9 | n | ciphertext, with 0 ≤ n ≤ 65 511 (`MobileFrameCipher.MaxPlaintextBytes`), so a whole frame is at most 65 536 bytes, the relay's limit (DEP-629) |
| 9 + n | 16 | GCM tag |

```
nonce = 0x00000000 ‖ counter (8 bytes, big-endian)     (12 bytes)
aad   = the 9 header bytes (version ‖ counter)
ciphertext ‖ tag = AES-256-GCM(key = k_pc or k_cp, nonce, plaintext, aad)
```

- **Sender.** Each direction starts at counter 0 and adds exactly 1 per frame. After `2^64 − 1` the sender stops; a
  new stream gets new keys. A plaintext over the maximum is not sent.
- **Receiver.** The receiver accepts a frame only if:
  - it is at least 25 bytes and at most the maximum;
  - its version is `0x01`;
  - its counter is exactly the next one in that direction (0, 1, 2, …);
  - its tag checks.

  A repeat or a step back is refused (replay), and so is a gap: a relay cannot quietly leave a frame out.
- A frame that is refused does not move the counter, so the genuine frame with that counter still opens.
- A refusal is silent, the same as in § 3: no content in an exception, no payload in a log. The caller decides whether
  the stream closes.

## 6. Messages inside a frame

The plaintext of a frame is one message as UTF-8 JSON:

```json
{ "type": "…", "id": "…", "body": { … } }
```

- `type`: what the message is. The types of v1 are in § 10.
- `id`: unique per sender within the stream, for correlating a reply.
- `body`: a JSON object whose shape depends on `type`.

A receiver ignores a `type` it does not know, and keeps the stream open.

## 7. Keys on the Cockpit

**What is stored, and where.** There is one static P-256 pair per Depot connection. It lives in the plugin's own
settings (`IPluginStorage`, which ends up as `Plugins.depot.Data` in `cockpit.json`):

| Value | How it is stored |
|---|---|
| Private key (PKCS#8) | Wrapped with the plugin's `ProjectSecretProtector` (AES-256-GCM, associated data `mobile/<connectionId>/cockpitKey`), then written as a declared secret (`SetSecret`) |
| Wrapping key (32 random bytes) | A declared secret (`SetSecret`) |
| Paired `phonePub`s | Plain JSON. They are public keys |

**What that protects against, and what it does not.**

- *Settings sync.* `Plugins.*.Data` is never carried (`SettingsCarrierWhitelist.NeverCarried`, and it is not in
  `Carried`), so nothing of this goes to Depot.
- *A backup.* A backup without credentials empties both declared secrets. A backup with credentials carries both: that
  is the operator's choice, as with every other credential. Restoring such a backup on another machine makes that
  machine the Cockpit the phones know.
- *Someone who can read the disk.* Encryption at rest of declared secrets is off by default. When it is off, the
  wrapping key sits next to the wrapped key in the same file, and anyone who can read `cockpit.json` can unwrap the
  private key. The wrapping then only binds the key to its connection; it is not protection. When the operator has
  turned encryption at rest on, both secrets are encrypted under the operator's key.

**Behaviour.**

- `TryGetCockpitKey` creates the pair the first time it is needed, when nothing is stored for that connection yet.
- If a stored key cannot be read, the Cockpit never silently makes a new pair. This covers:
  - a missing or different wrapping key;
  - a damaged value;
  - phones that are paired while the key itself is gone.

  `TryGetCockpitKey` then returns `false` and changes nothing; the caller tells the operator. Only `ResetCockpitKey` (an
  explicit choice of the operator) makes a new pair. It forgets every paired phone, and each phone has to pair again.
- `RemovePhone` revokes one phone. The Cockpit's pair and the other phones stay.
- All `MobileKeyStore` instances share one lock, so two of them can never both create a pair for the same connection.

## 8. What Depot sees, and what it does not

| Depot sees | Depot does not see |
|---|---|
| The `pairingId` it issued, and which user and Cockpit asked for it (DEP-628) | `pairingSecret` and `cockpitPub` (they are only in the QR code) |
| Which phone talks to which Cockpit, when, and for how long (`streamId`, relay metadata) | Any session key, or any private key of either device |
| The size and time of every frame, and the counter in the frame header | The content of any frame: message types, ids and bodies |
| The handshake's `phonePub` and `ephPub`, and the reply's `cockpitEphPub` (public keys) | Anything that lets it compute the shared secrets |

A relay that tampers can still drop, delay or swap frames, or swap keys. The result is a refused handshake, a refused
frame or a stream that stops, never a frame that the other side accepts as genuine.

## 9. Known limits of v1, and what the caller must do

1. **Frames are not hidden in size or timing** (see § 8).
2. **A relay can refuse service.** It can drop a stream, swap an ephemeral key or hold frames back. Each of these ends
   in a refused handshake, a failing tag or a stream that stops. None of them leads to a frame that is accepted.
3. **A stream has no authenticated end.** The relay can cut a stream after any frame. The receiver then has a correct
   prefix of what was sent, never a changed or reordered one, but it cannot tell a cut from a sender that stopped. A
   message type that needs to know it is complete has to say so itself (AC-2227).
4. **"Phone online" and a new pairing require explicit `ready` confirmation (§ 4).** An accepted handshake proves
   only that the frame carried a paired key, or the right MAC. Each side must open the other's encrypted `ready`
   within 15 seconds. A QR with a substituted `cockpitPub` can pass the MAC check, but produces different keys:
   the phone refuses the Cockpit's `ready`, and the Cockpit never stores the provisional phone key. No application
   message reaches the assistant before the phone's valid `ready`; timeout discards the provisional pairing.

The two limits of the first draft, forward secrecy on one side only and a recorded handshake that could be played
again, are gone since the Cockpit's ephemeral key (§ 3, § 4). Zyra decided this on 09-11, from Raymond's requirement
in DEP-618: Depot must not be able to interact with anybody's Cockpit in any way.

## 10. Message types, v1 (AC-2227)

Every message is an envelope as in § 6. A reply carries the `id` of the message it answers as `replyTo` in its body.
The confirmation has id `ready`; the Cockpit's application ids are `c1`, `c2`, ….

**Who may send.** A frame reaches the assistant only from a phone this Cockpit holds a key for, over that phone's current
stream, after its keys opened the frame. Each paired phone is its own channel onto the assistant (the same conversation
as the chat window, next to Slack and Discord), opened for that phone's device id alone. Forget closes the channel, drops
the stream and wipes the key: from then on that phone gets nothing and nothing it sends gets in. The Cockpit sends to a
phone only after the phone's `ready` opened (§ 9.4), apart from its own initial `ready`.

**Phone → Cockpit.**

| `type` | `body` | What happens |
|---|---|---|
| `assistant.send` | `{ "text": string }` | Handed to the assistant as the operator's message, by the same route as typed text. Answered with `assistant.sent`. Refused while a question of the assistant waits (see `assistant.question`), because the Cockpit would read the text as its answer. The refusal lifts at the operator's next message, the moment the host stops reading text as the answer. |
| `assistant.history` | `{ "limit"?: number, "before"?: string }` | The tail of the conversation: at most 50 rows (`limit` is clamped to 1…50), older than the row with id `before` when given. Answered with `assistant.history`. |
| `assistant.chat` | `{ "open": bool }` | The chat is on screen on the phone (`true`) or not (`false`). Live rows go out only while it is open. A new stream starts closed. |
| `cockpit.status` | `{}` | Answered with `cockpit.status`. |

**Cockpit → phone.**

| `type` | `body` | When |
|---|---|---|
| `assistant.sent` | `{ "replyTo", "ok": bool, "error"?: string }` | After `assistant.send`. |
| `assistant.history` | `{ "replyTo", "more": bool, "last": bool, "rows": [row] }` | After `assistant.history`. A page fills as few frames as it can; `last` marks the final frame of the page, `more` says there are older rows. |
| `assistant.rows` | `{ "last": bool, "rows": [row] }` | While the chat is open: rows that arrived or changed. A streaming row is sent as its latest snapshot, at most about four times a second. |
| `assistant.turnEnded` | `{}` | While the chat is open: the assistant finished its turn. |
| `assistant.consent` | `{ "promptId": string, "title": string }` | While the chat is open (and on opening it): the assistant waits on a permission prompt. Read-only in v1: the app shows "Waiting for you on the desktop", and the Cockpit takes no answer from the phone. |
| `assistant.consentClosed` | `{ "promptId": string }` | The prompt was answered on the desktop. |
| `assistant.question` | `{ "questionId": string, "question": string }` | While the chat is open (and on opening it): the assistant asked a question that is answered on the desktop. Read-only in v1, like `assistant.consent`. |
| `assistant.questionClosed` | `{ "questionId": string }` | The operator's next message, typed anywhere, moved past the question. |
| `notify` | `{ "kind": "assistantMessage" }` | The assistant finished a turn with a new answer while the chat was not open. No text, nothing of the answer; only to a phone with an open stream. |
| `cockpit.status` | `{ "replyTo", "name": string, "version": string?, "assistant": bool }` | After `cockpit.status`: the machine name, the Cockpit version, and whether this Cockpit has an assistant. |

A `row` is `{ "id", "kind", "text", "timestamp", "toolName"?, "resultText"?, "truncated": bool }`. `kind` is one of
`assistantText`, `userText`, `toolUse`, `toolResult`, `thinking`, `question`, `turnCompleted`, `error`, `divider`. A row
too big for one frame keeps the start of its text and says `truncated: true`. Attachments do not go over the line in v1.

**History comes from the host.** The Cockpit reads the page from the assistant's own transcript (`ReadRowsAsync`, host 0.171.0),
which a resumed conversation repaints from its log, so it works after a restart of Cockpit as well and nothing is kept by this
plugin. `before` is a row `id` the phone already holds; one it does not hold, or a Cockpit with no assistant, gives an empty page
with `more: false`. A page is at most 50 rows, sent oldest first, each frame within the 64 KiB limit.
Nothing of the conversation is written to disk or to a log by this plugin.

**Back-pressure.** At most 32 frames wait to go to one phone. Past that a frame is not queued but dropped, so a phone
that keeps asking cannot hold up the relay for the others; a page that loses a frame never sends its `last`.
