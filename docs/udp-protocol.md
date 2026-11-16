# PNMT-UDP v1: the protocol of PNMT's UDP nodes

| | |
|---|---|
| Status | Implemented in PNMTD (`PNMTD.Lib/Udp`), version 1 |
| For | Developers of devices that report to PNMT over UDP |
| Port | 5028/udp by default (`PNMTD_UDP_PORT`) |
| See also | The admin UI has this documentation with live examples: on every UDP node (*Protocol* tab, with the node's id, suites and key) and under *Admin → UDP nodes → Protocol* (the reference key). docs/udp-iot-plan.md describes how PNMTD implements it |

The section numbers are those of docs/udp-iot-plan.md §3, which this file reproduces, so references such as "§3.6" mean the same in both. Sections outside §3 (§4.2 events, §4.7 what is kept, §4.11 firmware updates on the server) are in the plan.

**How PNMTD records what a node sends:**

- Heartbeats become sensor event 230, DATA event 232 with the payload as its message, and BOOT event 231.
- Only the last packet of each type is kept with its bytes.
- The commands users send, the device's answers to them and the firmware updates' steps are logged permanently with their bytes.

**Testing a device:** `pnmtd udp send` sends one packet and shows both directions, decoded and verified. `pnmtd udp node` is a complete simulated device. The reference client [examples/udp/pnmt_udp.py](../examples/udp/pnmt_udp.py) uses only Python's standard library (suites 0–3, 8–11).

## 3. The protocol: PNMT-UDP v1

This section is normative. The words MUST, SHOULD and MAY are used as in RFC 2119.

### 3.1 Exchange model

```
node                                              server
 │  uplink (HEARTBEAT, DATA, EVENT, …) ─────────────►│  validate, store, decide
 │◄─────────────── at most one downlink (ACK|COMMAND)│  within 100 ms
 │  (the receive window closes; the radio sleeps)    │
 ⋮  10 minutes later                                 ⋮
 │  uplink ─────────────────────────────────────────►│  a command was queued meanwhile
 │◄──────────────────────────────────── COMMAND      │
 │  CMD_RESULT ─────────────────────────────────────►│  the result is stored
 │◄──────────────────────────────────── ACK          │  (if asked for)
```

- **Uplink** means node → server, and **downlink** means server → node.
- Every accepted uplink opens one response slot. The server answers to the uplink's source address and port, from the socket the uplink arrived on, so NAT and firewall state on the path fits the answer.
- The server never starts an exchange. It does not know whether the device is listening, and the device's NAT mapping may be gone. That is why a command must wait for the node's next packet (R5).

### 3.2 Packet layout

All multi-byte fields are big-endian.

```
 Byte 0                          Byte 1
 7   6   5   4   3   2   1   0   7   6   5   4   3   2   1   0
┌───────┬───────────────┬───────┬───┬───┬───────┬───────────────┐
│  VER  │     TYPE      │  IDL  │ A │ M │  RSV  │     SUITE     │
└───────┴───────────────┴───────┴───┴───┴───────┴───────────────┘
┌───────────────────────────────────────────────────────────────┐
│ DEVICE ID   IDL+1 bytes (1–4), unsigned                       │
├───────────────────────────────────────────────────────────────┤
│ COUNTER     2 bytes: the low 16 bits of the frame counter     │
├───────────────────────────────────────────────────────────────┤
│ PAYLOAD     0–n bytes, by TYPE (encrypted with AES-CCM)       │
├───────────────────────────────────────────────────────────────┤
│ TAG         0, 4 or 8 bytes, by SUITE                         │
└───────────────────────────────────────────────────────────────┘
```

| Field | Bits | Meaning |
|---|---|---|
| `VER` | 2 | Protocol version. `01` = v1. `00` is invalid (it rejects zero-filled junk), and `10`/`11` are reserved for later versions |
| `TYPE` | 4 | Packet type (§3.4). **The top bit is the direction:** 0x0–0x7 are uplink and 0x8–0xF are downlink |
| `IDL` | 2 | The length of the device id minus one: `00` = 1 byte (ids up to 255), `01` = 2 (65,535), `10` = 3 (16.7 M), `11` = 4 (4.29 G). A device MAY use more bytes than needed. The server answers with the same length |
| `A` | 1 | Uplink: an acknowledgement is requested (§3.9). Downlink: 0 |
| `M` | 1 | Downlink: more commands are waiting (§3.10). Uplink: 0 |
| `RSV` | 2 | Reserved, MUST be 0. A packet with them set is dropped |
| `SUITE` | 4 | Security suite (§3.5) |
| `DEVICE ID` | 8–32 | Assigned by PNMT when the node is created, starting at 1. 0 is invalid |
| `COUNTER` | 16 | Uplink: the low 16 bits of the node's frame counter `FCNT` (§3.7). Downlink: **the counter of the uplink being answered**, echoed |
| `PAYLOAD` | – | Its length is what remains of the datagram after the header and the tag. There is no length field: UDP carries it |
| `TAG` | – | The token or the truncated MAC (§3.6) |

**Sizes:**

- The header (bytes 0–1, the id and the counter) is 5–8 bytes. With an id below 256 it is **5 bytes**.
- The smallest valid packet is 5 bytes: an unsigned heartbeat.
- A datagram MUST NOT exceed **508 bytes**, the UDP payload every IPv4 path delivers without fragmentation (576 − 60 − 8). The server drops larger ones.

### 3.3 Field rules

- The `IDL`, `A`, `M` and `RSV` bits of a downlink are set by the server. A device MUST ignore a downlink whose type is not a downlink type, whose suite is neither its uplink's nor one configured for it (the node's response suite, or the suite of its commands, §3.5, §3.10), or whose echoed counter is not that of its last uplink.
- An uplink that carries a downlink type is dropped, which stops reflection loops between two servers.
- Unknown types, suites and versions are dropped silently. A future v2 MUST change `VER` if it changes the layout.

### 3.4 Packet types

| TYPE | Name | Dir | Payload | Notes |
|---|---|---|---|---|
| 0x0 | `HEARTBEAT` | ↑ | Empty, or a few status bytes (opaque, shown in the event) | "I am alive." The usual packet. Becomes event 230 (§4.2) |
| 0x1 | `DATA` | ↑ | Opaque bytes: text, binary, CBOR or anything else the device implements | Becomes event 232, with the payload as the message (§4.2). The last one is kept with its bytes. Interpretation is a later phase (§8, U7) |
| 0x2 | `EVENT` | ↑ | `CHANNEL`(1) `CODE`(2) `MESSAGE`(UTF-8, rest) | A PNMT event in binary form: as `/event/<token>/<code>/<message>`, for channel sensors (§4.2, phase U7) |
| 0x3 | `CMD_RESULT` | ↑ | `CMD_ID`(1) `STATUS`(1) `RESULT`(rest, opaque) | The answer to a COMMAND (§3.10) |
| 0x4 | `BOOT` | ↑ | `REASON`(1, optional) `INFO`(UTF-8, optional, such as a firmware version) | The device has restarted. It counts as a heartbeat |
| 0x5 | `JOIN` | ↑ | `DEV_NONCE`(4) | Starts a session (§3.8). Only the 64-bit MAC suites: 3, 5, 9 and 11 |
| 0x6 | `OTA` | ↑ | `UPDATE_ID`(1) `OP`(1) `ARG`(rest) | Firmware update: fetch a chunk, decline, or report the outcome (§3.11) |
| 0x7 | – | ↑ | – | Reserved, dropped |
| 0x8 | `ACK` | ↓ | Empty | Acknowledges the uplink whose counter it echoes |
| 0x9 | `COMMAND` | ↓ | `CMD_ID`(1) `COMMAND`(rest, opaque) | Also acknowledges the uplink. `M` = more commands are waiting |
| 0xA | `JOIN_ACCEPT` | ↓ | `SERVER_NONCE`(4) | Answers a JOIN (§3.8) |
| 0xB | `OTA_OFFER` | ↓ | `UPDATE_ID`(1) `SIZE`(4) `CHUNK`(2) `SHA256`(32) `VERSION`(UTF-8, rest) | A firmware update is available (§3.11). Also acknowledges the uplink |
| 0xC | `OTA_CHUNK` | ↓ | `UPDATE_ID`(1) `OFFSET`(4) `DATA`(rest) | The answer to an OTA fetch (§3.11) |
| 0xD–0xF | – | ↓ | – | Reserved |

**Payload validation:** EVENT needs at least 3 bytes, CMD_RESULT at least 2, JOIN exactly 4, OTA at least 2, and an OTA fetch exactly 6 or 8. A packet that is too short counts as malformed.

**BOOT `REASON`:**

| Value | Meaning |
|---|---|
| 0 | Unknown |
| 1 | Power on |
| 2 | Watchdog |
| 3 | Brown-out |
| 4 | Software reset or update |
| 5 | Reset by a command |
| 6–127 | Reserved |
| 128–255 | Device-specific |

**CMD_RESULT `STATUS`:**

| Value | Meaning |
|---|---|
| 0 | OK |
| 1 | Unknown command |
| 2 | Invalid argument |
| 3 | Failed |
| 4 | Busy, try later |
| 5–127 | Reserved |
| 128–255 | Device-specific |

**Unencrypted and encrypted types (R7):** every type can be sent with every suite the node allows. "Encrypted DATA" is a DATA packet with suite 6 or 7, so encryption is a property of the packet, not a separate type. A device can therefore send 9-byte HMAC heartbeats every 10 minutes and an AES-CCM DATA packet once a day.

### 3.5 Security suites

| SUITE | Name | Tag | Payload | Secret | Replay protection | Intended for |
|---|---|---|---|---|---|---|
| 0 | `NONE` | 0 | Clear | – | No | Labs and closed networks. Must be allowed explicitly per node |
| 1 | `TOKEN32` | 4 | Clear | A 4-byte token | No | Shell scripts and MCUs without crypto. As safe as an `http://` sensor URL: anyone who can see a packet can forge one |
| 2 | `HMAC-SHA256/32` | 4 | Clear | A 16-byte key | Yes | Signed heartbeats from Linux, scripts and ESP32. **Default** |
| 3 | `HMAC-SHA256/64` | 8 | Clear | The key | Yes | A stronger signature, and JOIN for the HMAC-SHA256 family |
| 4 | `AES-CMAC/32` | 4 | Clear | A 16-byte AES key | Yes | MCUs with an AES engine (nRF, STM32, ESP32). LoRaWAN's MIC |
| 5 | `AES-CMAC/64` | 8 | Clear | The key | Yes | A stronger signature, and JOIN for the AES family |
| 6 | `AES-CCM/32` | 4 | **Encrypted** | The key | Yes | Encrypted packets, smallest |
| 7 | `AES-CCM/64` | 8 | **Encrypted** | The key | Yes | Encrypted packets. **Recommended for encryption** |
| 8 | `HMAC-MD5/32` | 4 | Clear | A 16-byte key | Yes | Devices and libraries that only have MD5 (older firmware, modem AT stacks, small Arduino libraries). *Legacy* |
| 9 | `HMAC-MD5/64` | 8 | Clear | The key | Yes | A stronger signature, and JOIN for the HMAC-MD5 family. *Legacy* |
| 10 | `HMAC-SHA1/32` | 4 | Clear | A 16-byte key | Yes | Devices and libraries that only have SHA-1. *Legacy* |
| 11 | `HMAC-SHA1/64` | 8 | Clear | The key | Yes | A stronger signature, and JOIN for the HMAC-SHA1 family. *Legacy* |
| 12–15 | – | – | – | – | – | Reserved, for example ChaCha20-Poly1305 for MCUs without AES |

The numbers 8–11 come after the existing suites, so suites 0–7 and the vectors of §3.14 keep their values.

- **Key family.** A node has one 16-byte key, generated by PNMT or entered manually (§4.3), and one family: **HMAC-SHA256** (suites 2 and 3), **HMAC-SHA1** (10 and 11), **HMAC-MD5** (8 and 9) or **AES** (4–7). One key is never used with two families, not even two hash functions. Encryption needs the AES family.
- **MD5 and SHA-1 only as HMAC.** The tag is HMAC-MD5 or HMAC-SHA1, never a plain `MD5(key ‖ packet)`, which could be extended and forged without the key (length extension).
  - **Collisions don't break HMAC.** Collision attacks broke both hashes for signatures and certificates. HMAC's security rests on other properties, and no practical forgery of HMAC-MD5 or HMAC-SHA1 is known (RFC 6151, RFC 6194).
  - **Truncation dominates.** At 32 or 64 bits, the tag length bounds the strength, not the hash.
  - **Legacy all the same.** Both are for devices that have nothing better. The UI labels them *legacy*, and new nodes default to HMAC-SHA256.
- **Token.** A node additionally has a 4-byte token, independent of the key, for TOKEN32.
- **Allowed suites.** A node allows a set of suites: NONE, TOKEN32, and suites of its family. Anything else is rejected as "suite not allowed".
- **The response suite.** By default a downlink uses **the suite its uplink came with**, with the same key or session key. A node can be configured to answer in **another suite** of its family, or in NONE or TOKEN32. A single command can override it as well (§3.10). For example, a node sends TOKEN32 heartbeats, but its commands go out signed with HMAC-SHA256/64, or encrypted with AES-CCM/64.
  - JOIN_ACCEPT always uses the JOIN's suite.
  - OTA downlinks use the response suite only if it is keyed, and otherwise the GET's suite (§3.11).
  - With a keyed response suite, the server enforces the counter for every uplink of the node, whatever its suite (§3.7). Every keyed response is then bound to a fresh uplink counter, and an AES-CCM nonce is never reused.
  - **An encrypted answer to a NONE or TOKEN32 uplink comes only from a node that allows no keyed uplink suite.** Keyed and unkeyed uplinks have counter windows of their own (§3.7), so on a node with both, an unkeyed uplink's counter could meet a signed one's and repeat the nonce. There the AES-CCM response suite is skipped for unkeyed uplinks: the ack comes as the uplink did, and a command waits for a keyed uplink. A node that only ever sends NONE or TOKEN32 gets its encrypted answers as before.
- **On 32-bit tags:** a forgery succeeds with a probability of 2⁻³² per attempt, and every attempt is a packet. The per-node rate limit and the "rejected" counter that alerts the operator (§4.10) make brute force impractical. LoRaWAN relies on the same size. Use the 64-bit suites when commands matter.

### 3.6 Tags, encryption and nonces

Notation: `‖` is concatenation. `P` is every byte of the packet before the tag. `H` is the header, bytes 0–1 plus the id and the counter. `FCNT32` is the full 32-bit frame counter (§3.7), big-endian. For a downlink, `FCNT32` is the full counter of the uplink being answered. `K` is the node's key, or its session key after a JOIN (§3.8).

| Suite | Computation |
|---|---|
| NONE | No tag |
| TOKEN32 | `TAG = TOKEN` |
| HMAC-SHA256/n | `TAG = first n/8 bytes of HMAC-SHA256(K, FCNT32 ‖ P)` |
| HMAC-SHA1/n | `TAG = first n/8 bytes of HMAC-SHA1(K, FCNT32 ‖ P)` |
| HMAC-MD5/n | `TAG = first n/8 bytes of HMAC-MD5(K, FCNT32 ‖ P)` |
| AES-CMAC/n | `TAG = first n/8 bytes of AES-CMAC(K, FCNT32 ‖ P)` |
| AES-CCM/n | `(C, TAG) = AES-128-CCM(K, nonce = N, aad = H, plaintext = PAYLOAD, tag length n/8)`, and the packet is `H ‖ C ‖ TAG` |

**The CCM nonce (13 bytes):** `N = DIR(1) ‖ DEVICE_ID(4, padded) ‖ FCNT32(4) ‖ 00 00 00 00`, where `DIR` is `00` for an uplink and `01` for a downlink. 13 bytes is the size most hardware CCM engines take.

**Why the nonce is never reused:**

- An uplink's FCNT32 is unique per key, because the counter only increases (§3.7).
- Each accepted uplink gets at most one downlink, and the direction byte separates the two.
- A retransmitted uplink gets the stored response bytes again, not a new encryption (§3.9).

**Comparisons:** the server compares tags in constant time (`CryptographicOperations.FixedTimeEquals`). The tag covers the header, so a changed type, flag, suite, id or counter fails as well.

### 3.7 Frame counter and replay protection

- **FCNT** is a 32-bit counter per node, or per session after a JOIN. The device increments it for every **new** uplink and starts at 1. A retransmission reuses the counter (§3.9).
- **Only the low 16 bits travel.** The server reconstructs the full value as the one closest to the highest accepted counter `HIGH`:
  `d = (wire − (HIGH mod 65536)) mod 65536`, then `if d ≥ 32768: d −= 65536`, then `FCNT32 = HIGH + d`.
  The first packet of a node, or of a session, sets `HIGH`.
- **Accepted when:**
  - `FCNT32 > HIGH` and `FCNT32 − HIGH ≤ 16384` (a larger jump is rejected as "counter jump"; at one packet every 10 minutes that would take 113 days of loss); or
  - `HIGH − 63 ≤ FCNT32 ≤ HIGH` and the packet has not been seen (a 64-bit window bitmap, as in IPsec, which tolerates reordering).
- **Everything else is a replay** and is rejected. The exception is an identical retransmission (§3.9).
- **For NONE and TOKEN32**, the counter is only used to detect losses, retransmissions and restarts. It is not enforced: a counter that jumps back is noted as "counter reset (restart?)". The exception is a node with a keyed response suite: the counter is then enforced for all of its uplinks (§3.5).
- **NONE and TOKEN32 uplinks have a window of their own,** apart from the keyed suites' (`PlainFcntHigh`, `PlainFcntWindow`). Anyone who knows a device id can send an unkeyed packet, so it must never move the window the keyed suites are checked against: a forged NONE heartbeat with a high counter can't make a node's signed packets look like replays. The node's page shows the higher of the two, and a reset clears both.
- **Per node, replay protection can be switched off** for keyed suites, for bench testing. It is on by default.

**The device's side (normative):**

- A device using keyed suites (2–11) **MUST NOT reuse a counter** under the same key. With AES-CCM, a reused counter leaks plaintext, and the server cannot prevent that.
- It either **persists FCNT** or **uses JOIN on every start** (§3.8).
- The recommended persistence is block reservation. Store `FCNT + 16` in flash at start and every 16 packets, and on a start continue from the stored value. That is one write every 160 minutes at a 10-minute interval, about 3,300 a year, which wear-levelled NVS takes easily.

**The server's side:**

- `HIGH` and the window are stored with the node, in the transaction of the uplink, before the response goes out.
- An admin can **reset the counter** (the next packet sets a new `HIGH`). This is the escape hatch for a device that lost its counter and cannot JOIN. The reset is logged.

### 3.8 Sessions (JOIN), for devices that cannot persist the counter

Per node, the **session mode** is either:

- **None**: the root key K and a persistent counter. JOIN is rejected.
- **JOIN required**: every start begins with a JOIN, and only the session key is accepted for other packets.

```
node                                                     server
 │ JOIN (suite 3/5/9/11, FCNT 0, DEV_NONCE = r1, root K) ─►│ verify, at most 1 JOIN per node per 10 s
 │◄──── JOIN_ACCEPT (SERVER_NONCE = r2, MAC with K)        │ SK = KDF(K, r1, r2), stored as *pending*
 │ HEARTBEAT (FCNT 1, tag with SK) ───────────────────────►│ pending verifies → active; HIGH = 1
```

- **JOIN** has an FCNT of 0 and its MAC is computed under the root key K. `DEV_NONCE` is 4 random bytes from the device.
- **JOIN_ACCEPT's MAC** is computed with `DEV_NONCE` in the place of `FCNT32` (`MAC(K, DEV_NONCE ‖ P)`), which binds it to that JOIN.
- **The KDF:**
  - The HMAC families: `SK = first 16 bytes of HMAC-<hash>(K, "PNMT-UDP1 SK" ‖ DEV_NONCE ‖ SERVER_NONCE)`, with the family's own hash. MD5's output is exactly 16 bytes, and SHA-1's 20 are truncated.
  - AES family: `SK = AES-128-ECB(K, 01 ‖ DEV_NONCE ‖ SERVER_NONCE ‖ 00×7)`.
- **The server keeps the active session and at most one pending session.** The pending session becomes active, and the old one is dropped, only when an uplink verifies under it.
  - A **replayed JOIN** therefore only creates a pending session that nobody can use.
  - **Old recorded packets** were made under an older session key, so they fail.
  - The device's current session keeps working throughout, so a replay cannot lock the device out.
- **Without an answer**, the device retries with a new `DEV_NONCE` after 10 s, 30 s, 60 s and then its normal interval.
- TOKEN32 and NONE are not affected by sessions.

### 3.9 Acknowledgements and retransmission

- **Per node, the ACK mode** is one of:
  - `Never`
  - `OnRequest` (default): an ACK when the uplink has `A = 1`
  - `Always`
- A COMMAND in the response replaces the ACK, because it acknowledges the uplink too.
- **The device:** with `A = 1` and no valid answer within its receive window, it MAY send the **identical datagram** again (the same counter), at most twice. The window is about 2 s on Wi-Fi or LTE-M, and up to about 10 s on NB-IoT.
- **The server:** if the same FCNT32 arrives with **byte-identical** content within 5 minutes of the original, it is a retransmission.
  - The stored response bytes go out again, or nothing if there was none, and the packet is counted as a retransmission.
  - The packet is **not processed twice**: no second event, no second command attempt.
  - The same counter with different bytes is a replay.

### 3.10 Commands

- **Delivery slots.** Every accepted uplink is a delivery slot: HEARTBEAT, DATA, EVENT, CMD_RESULT, BOOT, and the OTA reports *declined*, *verified*, *failed* and *applied*. JOIN is not one, because its response is JOIN_ACCEPT, and neither is an OTA fetch, whose response is always OTA_CHUNK (§3.11). **One command per response.**
- **The order** is the order the commands were queued in. `M = 1` says that another command is waiting. The device MAY then send a HEARTBEAT at once, and its CMD_RESULT also opens the next slot. A queue drains as `COMMAND → CMD_RESULT → COMMAND → …` without waiting for the next interval.
- **`CMD_ID`** is 8 bits per node, assigned when the command is queued, and it increments and wraps. A node has at most 32 open commands, so the id is never ambiguous.
- **Delivery modes:**
  - `ResultRequired` (default): the command is re-sent, with the same `CMD_ID` and bytes, on later uplinks until a CMD_RESULT with that id arrives, at most *max attempts* times (default 3).
  - `FireAndForget`: sent once, and no result is expected.
- **The device MUST execute a given `CMD_ID` at most once.** It remembers the last ids it executed, at least the last one, and answers a repeat with its stored result.
- **Expiry:** a command that has not been delivered by its expiry (default 24 h) expires. A "open the valve" must not run three days late.
- **Security:**
  - Commands are delivered only in answer to an uplink with a keyed suite (2–11).
  - **A command goes out in the response suite** (§3.5): by default the suite of the uplink it answers. The node's setting or the command itself can choose another, for example AES-CCM/64 for a command that must be encrypted, whatever the uplink was.
  - A command whose suite would be NONE or TOKEN32 is still sent, because the default follows the uplink. The UI warns that anyone who sees such a packet can forge it, or can spoof the node to receive it, and suggests a keyed suite for the node's commands.
- **Size:** a command is at most 480 bytes, so the datagram stays at or below 508. Under 64 bytes is recommended for NB-IoT.
- **Meaning:** command bytes are opaque to PNMT. The protocol defines no built-in commands. The 1-byte `CMD_ID` plus opaque bytes leaves the meaning to the firmware. A convention such as "first byte = opcode" is suggested in the device guide, not enforced.

### 3.11 Firmware updates (OTA)

An update keeps the exchange model: the device asks, and the server answers once. It is an **offer** in a normal response slot, then a **pull** of the image in chunks by the device, then a **report**.

```
node                                                        server
 │ HEARTBEAT ────────────────────────────────────────────────►│ an update job waits for this node
 │◄──── OTA_OFFER (UPDATE_ID 3, SIZE 1,048,576, CHUNK 448, SHA256, "1.5.0")
 │ OTA GET (3, OFFSET 0) ────────────────────────────────────►│ state: downloading
 │◄────────────────────────────── OTA_CHUNK (3, 0, 448 bytes) │
 │ OTA GET (3, OFFSET 448) ──────────────────────────────────►│
 │◄──────────────────────────── OTA_CHUNK (3, 448, 448 bytes) │
 ⋮   2,341 chunks in all; a lost one is simply fetched again   ⋮
 │ OTA VERIFIED (3) ─────────────────────────────────────────►│ the SHA-256 matched on the device
 │◄───────────────────────────────────────────────────── ACK  │
 │ (installs, restarts)                                        │
 │ BOOT (REASON 4, "1.5.0") ─────────────────────────────────►│
 │ OTA APPLIED (3, "1.5.0") ─────────────────────────────────►│ state: applied
```

**The `OP`s of an OTA uplink:**

| OP | Name | `ARG` | Response | Meaning |
|---|---|---|---|---|
| 0x01 | `GET` | `OFFSET`(4) `LENGTH`(2, optional) | OTA_CHUNK | Fetch `LENGTH` bytes at `OFFSET`. Without `LENGTH`, `CHUNK` from the offer, which is also the maximum. The first GET accepts the offer |
| 0x02 | `DECLINE` | `REASON`(1) | A normal slot | 1 not enough space, 2 battery low, 3 busy, 4 already installed, 5 wrong target, 128–255 device-specific |
| 0x03 | `VERIFIED` | – | A normal slot | All bytes are in and the SHA-256 matches. The device installs next |
| 0x04 | `FAILED` | `REASON`(1) | A normal slot | 1 hash mismatch, 2 write error, 3 image rejected (signature, header or target), 4 install failed, 5 rolled back after the restart, 128–255 device-specific |
| 0x05 | `APPLIED` | `VERSION`(UTF-8) | A normal slot | The new image is running after the restart, and has passed its self-test if it has one |

**Rules:**

- **The offer** goes out in a normal slot (§3.10), after any queued commands. It is re-sent in later slots until an OTA uplink with its `UPDATE_ID` arrives, at most the job's number of attempts, and it expires (default 7 days). The `M` flag also covers a waiting offer.
- **`UPDATE_ID`** is 8 bits per node, like `CMD_ID`. A node has at most one open update.
- **Chunks:**
  - `CHUNK` is 64–480 bytes, chosen per update. The default is 448, the largest multiple of 64 that fits the 508-byte datagram with an 8-byte tag and a 4-byte id.
  - Each GET is a new uplink with a new counter. The device may fetch in any order, and fetch a chunk again; the server keeps no per-chunk state.
  - A lost chunk is re-fetched, either as an identical retransmission (§3.9) or as a new GET.
- **Resuming:** after a restart or a lost connection, the device continues with a GET at its first missing offset, as long as the update is open.
- **Edge cases:** these are answers to valid, authenticated uplinks, so rule 1 of §3.12 still holds.
  - A GET at an offset at or past `SIZE` gets an OTA_CHUNK with empty `DATA`.
  - A GET for an `UPDATE_ID` that is no longer open (cancelled, expired or finished) gets an OTA_CHUNK with `OFFSET = FFFFFFFF` and no data. That means **abort**, and the device discards the partial image.
- **Integrity:**
  - The offer carries the image's SHA-256. Like every downlink it is authenticated with the node's key, and so is every chunk.
  - The device **MUST** verify the SHA-256 over the whole image before installing.
  - It **SHOULD** also verify the image's signature in its bootloader (ESP-IDF secure boot, MCUboot). PNMT checks neither the content nor a signature, so a signed image is the protection against a compromised PNMT admin account.
- **Rollback:** the device should keep the old image until the new one has started and passed its self-test (A/B slots, as ESP-IDF OTA and MCUboot do). After a rollback it reports FAILED with reason 5. PNMT counts an update as *applied* only on APPLIED, or on a BOOT whose `INFO` equals the offered `VERSION`.
- **Security:**
  - OTA uplinks must use keyed suites (2–11), never NONE or TOKEN32. A chunk is up to 25 times larger than its GET, so an unauthenticated GET would be an amplifier.
  - OTA downlinks use the node's response suite if it is keyed, and otherwise the GET's suite (§3.5).
  - Over AES-CCM, chunks are encrypted, so the image stays confidential in transit.
- **Pace:** stop-and-wait, one chunk per round trip.
  - The node's rate limit is raised while an update is open (default 20 GETs/s).
  - 1 MiB takes about 4 minutes at a 100 ms round trip (Wi-Fi, LTE-M) and about 80 minutes at 2 s (NB-IoT). The volume is in §3.13.
- **During an update**, the device's other uplinks still open slots, so commands keep being delivered.

### 3.12 Server rules (normative)

1. **No answer to a packet that fails any check.** There are no error responses: no oracle for guessing ids or keys, and nothing to reflect.
2. **At most one response per accepted uplink**, plus the identical resends of §3.9.
3. The response goes to the uplink's source address and port, from the listening socket.
4. The response leaves within **100 ms** of receipt, with a target p99 below 50 ms against a local PostgreSQL.
5. **Nothing unsolicited**, ever.
6. **No amplification:** an ACK has the size of an uplink without payload, and a JOIN_ACCEPT the size of a JOIN. Only COMMAND, OTA_OFFER and OTA_CHUNK may be larger than their uplink:
   - Commands are queued by signed-in users and limited by attempts and size.
   - Offers come from updates an Admin started.
   - Chunks answer only uplinks with a keyed suite (§3.11).
7. Downlink types, reserved bits, reserved types, unknown suites, `VER ≠ 01`, datagrams under 5 bytes and datagrams over 508 bytes are dropped.

### 3.13 Data volume

Per packet with IPv4 (+28 bytes) and IPv6 (+48 bytes). A device id below 256.

| Packet | PNMT-UDP | IPv4 total | IPv6 total |
|---|---|---|---|
| HEARTBEAT, NONE | 5 B | 33 B | 53 B |
| HEARTBEAT, TOKEN32 or any /32 suite | 9 B | 37 B | 57 B |
| HEARTBEAT, any /64 suite | 13 B | 41 B | 61 B |
| ACK, /32 | 9 B | 37 B | 57 B |
| DATA with 8 bytes, AES-CCM/64 | 21 B | 49 B | 69 B |
| HTTP `GET /event/…` (for comparison) | – | about 1 KB | about 1 KB |
| HTTPS `GET /event/…` | – | about 5 KB or more | about 5 KB or more |

At one heartbeat every 10 minutes (4,320 a month) with HMAC/32 over IPv4:

| Setup | Per month |
|---|---|
| Without ACK | about 156 KiB |
| With ACK | about 312 KiB |
| HTTP | about 4 MiB |
| HTTPS | about 20 MiB |

**A firmware update of 1 MiB** with 448-byte chunks and HMAC/64 over IPv4 takes 2,341 round trips:

| Direction | Per round trip | Total |
|---|---|---|
| Down (OTA_CHUNK) | 494 B | about 1.10 MiB |
| Up (OTA GET) | 47 B | about 107 KiB |
| Both | | **about 1.21 MiB, 21 % over the image size** |

On a metered SIM, one update costs about as much as 8 months of 10-minute heartbeats without ACKs. The UI shows this estimate before an update starts.

### 3.14 Examples and test vectors

All examples use device id 7, key `00 01 02 … 0F` (16 bytes) and token `5A 17 C3 9E`. The HMAC values were computed with Python's `hmac`. Phase U1 adds them, and the AES vectors, as unit tests.

| # | Packet | Bytes | Size |
|---|---|---|---|
| 1 | HEARTBEAT ↑, `A=1`, HMAC-SHA256/32, FCNT 42 | `40 82 07 00 2A DF 27 88 7E` | 9 |
| 2 | ACK ↓ for #1 | `60 02 07 00 2A 55 D9 7A 31` | 9 |
| 3 | DATA ↑ `"21.5C"`, HMAC-SHA256/64, FCNT 43 | `44 03 07 00 2B 32 31 2E 35 43 35 0A B3 32 32 76 BB 45` | 18 |
| 4 | HEARTBEAT ↑, HMAC/32, FCNT 44 | `40 02 07 00 2C 73 CE 03 12` | 9 |
| 5 | COMMAND ↓ for #4, `CMD_ID 1`, command `02 02 58` | `64 02 07 00 2C 01 02 02 58 E6 B0 5E F4` | 13 |
| 6 | CMD_RESULT ↑ `CMD_ID 1`, `STATUS 0`, `"OK"`, `A=1`, FCNT 45 | `4C 82 07 00 2D 01 00 4F 4B FA A0 DD 20` | 13 |
| 7 | HEARTBEAT ↑, TOKEN32, **id 300** (`IDL=01`), FCNT 1 | `41 01 01 2C 00 01 5A 17 C3 9E` | 10 |
| 8 | HEARTBEAT ↑, NONE, FCNT 1 | `40 00 07 00 01` | 5 |
| 9 | HEARTBEAT ↑, `A=1`, HMAC-MD5/32, FCNT 42 | `40 88 07 00 2A CD 5C FD B1` | 9 |
| 10 | HEARTBEAT ↑, `A=1`, HMAC-MD5/64, FCNT 42 | `40 89 07 00 2A 6E 1E 4C 4C A3 C2 F2 A7` | 13 |
| 11 | HEARTBEAT ↑, `A=1`, HMAC-SHA1/32, FCNT 42 | `40 8A 07 00 2A 68 AB C6 CF` | 9 |
| 12 | HEARTBEAT ↑, `A=1`, HMAC-SHA1/64, FCNT 42 | `40 8B 07 00 2A B8 25 70 92 47 E1 82 E1` | 13 |

Vectors #9–#12 are #1 with another suite. The suite code is in byte 1, which the MAC covers, so a /64 tag is not the /32 tag extended.

Reading #1:

| Bytes | Field |
|---|---|
| `40` | VER 1, TYPE HEARTBEAT, IDL 1 byte |
| `82` | `A`, suite 2 |
| `07` | Device id |
| `00 2A` | Counter 42 |
| `DF 27 88 7E` | First 4 bytes of `HMAC-SHA256(K, 00 00 00 2A ‖ 40 82 07 00 2A)` |

In #5, `02 02 58` is an example opcode (`02` = set interval) followed by 600 as a u16. PNMT does not interpret it.

**From a shell (TOKEN32, no crypto needed):**

```sh
echo 41 01 01 2C 00 01 5A 17 C3 9E | xxd -r -p | nc -u -w1 pnmt.example.com 5028
```

The bytes are the same every time. That is fine for TOKEN32, which does not enforce the counter. Sends less than 5 minutes apart count as retransmissions, and the node stays alive.

**Python, HMAC/32 heartbeat (reproduces #1):**

```python
import hmac, hashlib, struct
def heartbeat(dev_id, fcnt, key, ack=True):          # dev_id < 256
    pkt = bytes([0x40, (0x80 if ack else 0) | 2, dev_id]) + struct.pack('>H', fcnt & 0xFFFF)
    tag = hmac.new(key, struct.pack('>I', fcnt) + pkt, hashlib.sha256).digest()[:4]
    return pkt + tag
```

### 3.15 Device checklist

- [ ] Persist FCNT (block reservation), or use JOIN at every start (§3.7, §3.8)
- [ ] Never reuse a counter with a keyed suite, except for an identical retransmission
- [ ] Listen after an uplink when an ACK was requested or commands are expected. A device that never listens gets no commands
- [ ] Accept a downlink only if its type is a downlink type, its suite is the uplink's or one configured for it, its echoed counter is the uplink's, and its tag verifies. Ignore everything else
- [ ] Execute each `CMD_ID` once, and answer a repeat with the stored result
- [ ] Send BOOT after a restart, with a reason (it shows up as 231 in PNMT)
- [ ] Keep datagrams at or below 508 bytes, and preferably below 100
- [ ] Use the 64-bit suites if commands can do harm
- [ ] OTA, if supported:
  - verify the SHA-256 of the whole image before installing, and let the bootloader verify its signature;
  - keep the old image until the new one has passed its self-test, and report the outcome (VERIFIED, FAILED, APPLIED);
  - resume with GET at the first missing offset;
  - reserve counter blocks large enough for thousands of GETs (for example 256 instead of 16)
