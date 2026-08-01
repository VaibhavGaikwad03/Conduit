# Conduit Wire Protocol v1

This document is the single source of truth shared by the Windows and Android apps.
Both implementations MUST stay in sync with it.

## Ports

| Purpose             | Port | Transport |
|---------------------|------|-----------|
| Discovery beacon    | 5461 | UDP (broadcast) |
| Session / features  | 5462 | TCP |
| Webcam video stream | 5463 | TCP (H.264) |
| Screen mirror stream| 5464 | TCP (H.264) |

## 1. Discovery (UDP 5461)

Each device broadcasts a JSON **identity beacon** to `255.255.255.255:5461` every 3s and
whenever the app starts. Listeners de-duplicate by `deviceId`.

```json
{
  "conduit": 1,
  "deviceId": "b6f1c8e2-...",
  "name": "Vaibhav's Galaxy",
  "type": "android",          // "android" | "windows"
  "tcpPort": 5462,
  "protocol": 1
}
```

A device that receives a beacon from a **paired** peer immediately opens a TCP session
to `senderIp:tcpPort` (if not already connected). Unpaired peers just appear in the UI.

## 2. Session framing (TCP 5462)

Every message on the TCP stream is **length-prefixed**:

```
+------------------+---------------------------+
| 4 bytes (uint32) | payload (N bytes)         |
| big-endian = N   | AES-256-GCM ciphertext OR |
|                  | plaintext during handshake|
+------------------+---------------------------+
```

- During the **handshake** (packets `identity`, `pair-request`, `pair-response`),
  the payload is plaintext UTF-8 JSON.
- After the shared key is established, every payload is `nonce(12) || ciphertext || tag(16)`
  where the plaintext is UTF-8 JSON. See §5.

## 3. Packet envelope

Every decrypted payload is a JSON object with this envelope:

```json
{
  "id": "uuid",              // unique per packet
  "type": "clipboard",       // see packet types below
  "ts": 1737835200000,       // unix millis
  "body": { }                // type-specific, see below
}
```

## 4. Packet types

| type                  | direction        | body |
|-----------------------|------------------|------|
| `identity`            | both (handshake) | `{ deviceId, name, deviceType, protocol, publicKey }` |
| `pair-request`        | initiator        | `{ publicKey, code }` (code = 6 digits) |
| `pair-response`       | responder        | `{ accepted: bool, publicKey }` |
| `ping`                | both             | `{}` |
| `pong`                | both             | `{}` |
| `clipboard`           | both             | `{ content, contentType: "text" }` |
| `file-offer`          | both             | `{ transferId, name, size, mime }` |
| `file-chunk`          | both             | `{ transferId, seq, dataB64 }` |
| `file-complete`       | both             | `{ transferId, ok, sha256 }` |
| `file-search`         | both             | `{ requestId, query }` — ask the peer to search its files by filename substring |
| `file-search-result`  | both             | `{ requestId, truncated, results: [{ id, name, size, folder, mime }] }` — reply to `file-search` |
| `file-request`        | both             | `{ id }` — ask the peer to send a file it returned in a `file-search-result` (streamed via `file-offer`/`file-chunk`/`file-complete`) |
| `open-link`           | both             | `{ url }` — open this URL in the peer's default browser (only `http`/`https` are honored) |
| `notification`        | android → win    | `{ key, appName, title, text, iconB64?, canReply, actions:[] }` |
| `notification-action` | win → android    | `{ key, action: "dismiss"\|"reply", text? }` |
| `media-state`         | both             | `{ playing, title, artist, app, position, duration, volume }` |
| `media-command`       | both             | `{ command: "play"\|"pause"\|"next"\|"prev"\|"volume", value? }` |
| `remote-command`      | both             | `{ command: "lock"\|"sleep"\|"ring"\|"ring-stop"\|"screenshot" }` |
| `battery`             | android → win    | `{ level, charging, temperature }` |
| `device-status`       | android → win    | `{ ringerMode }` — ringer mode: `"silent"` \| `"vibrate"` \| `"normal"` |
| `sms-list`            | android → win    | `{ threads: [{ address, name, snippet, ts }] }` |
| `sms-send`            | win → android    | `{ address, body }` |
| `webcam-start`        | win → android    | `{ port, facing }` — start streaming the phone camera to this PC's webcam port (default 5463). `facing` = `"front"` (default) or `"back"` |
| `webcam-stop`         | win → android    | `{}` — stop the webcam stream |
| `webcam-switch`       | win → android    | `{ facing }` — flip the live webcam stream to the `"front"` or `"back"` camera without reconnecting |
| `screen-start`        | win → android    | `{ port }` — start mirroring the phone screen to the PC on this TCP port (default 5464) |
| `screen-stop`         | win → android    | `{}` — stop mirroring the phone screen |
| `input`               | win → android    | `{ action, x, y, x2, y2, durationMs, key, text }` — remote control while mirroring (see below) |
| `disconnect`          | both             | `{}` — sender is closing the session on purpose; receiver should not auto-reconnect until the user reconnects |
| `error`               | both             | `{ code, message }` |

**Video streams** (webcam and screen mirror) are far too heavy for the JSON session channel, so
each uses its own dedicated TCP port (5463 webcam, 5464 screen). The control packets above only
tell the phone *when* and *where* to connect; the phone then opens the video socket back to the
PC and writes **length-prefixed Annex-B H.264 access units** (4-byte big-endian length + payload,
same framing as §2 but never encrypted — it is raw video on a separate port). `screen-start` first
prompts the phone user for the system screen-capture consent before any frame is sent.

**Remote input** (`input`) lets the PC drive the phone while its screen is mirrored. `x`/`y`/`x2`/`y2`
are normalized `0..1` coordinates over the phone screen, so they're resolution-independent. `action`:
`tap` (at `x,y`), `swipe` (from `x,y` to `x2,y2` over `durationMs`), `key` (`key` = `back`/`home`/
`recents`/`enter`/`backspace`), or `text` (type `text` into the focused field). The phone injects these
via an Accessibility Service the user enables once; if it isn't enabled, the phone prompts to enable it.

**File search** is peer-directed: a `file-search` makes the *other* device search its own files
(the phone via MediaStore — Downloads/Documents/media; Windows across the user folders). Each
result `id` is an **opaque random token** the responder maps to a local path/URI and remembers
briefly; a `file-request` is honored only for a token from a recent result, so a peer can never
pull an arbitrary path. Downloads reuse the normal `file-offer`/`file-chunk`/`file-complete` flow.

## 5. Encryption

Key exchange uses **ECDH over NIST P-256** (natively available on both .NET and Android's
`java.security`). Public keys are exchanged as base64 SubjectPublicKeyInfo (X.509) DER.

1. On connect, both sides send `identity` including their base64 **public key**.
2. Each side computes the shared secret via ECDH(ourPrivate, theirPublic).
3. The AES-256 key = SHA-256(sharedSecret).
4. Every post-handshake frame: `nonce = random(12)`, `ct = AES-256-GCM(key, nonce, json)`,
   frame payload = `nonce || ct || tag`.

## 6. Pairing handshake (first meeting)

```
A ──identity──▶ B          (exchange deviceId + publicKey)
A ◀──identity── B
A ──pair-request(code)──▶ B   A shows the 6-digit code
                              B shows the same code; user taps "Pair"
A ◀──pair-response(accepted)── B
        both persist the peer in their trusted store
```

After pairing, reconnections skip straight to `identity` → encrypted session.

## 7. Versioning

`protocol` is an integer. A device rejects sessions whose `protocol` it does not support
and sends an `error` packet with code `unsupported-protocol`.
