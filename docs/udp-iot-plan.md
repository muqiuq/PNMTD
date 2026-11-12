# Plan: IoT nodes over UDP (PNMT-UDP v1)

| | |
|---|---|
| Status | **Implemented: phases U1–U6** (2026-11-12). U7 stays for later. §12 lists where the implementation differs from this plan |
| Date | 2026-11-12 |
| Revision | 3: **the review decisions (§11)**:<ul><li>UDP port 5028, configurable;</li><li>the listener on by default;</li><li>keys in plaintext;</li><li>responses in the received suite unless configured otherwise;</li><li>heartbeats and data as sensor events, with only the last packet of each type kept;</li><li>**only the commands users send, and the device's answers to them, logged with their bytes**;</li><li>notes on every sensor;</li><li>firmware only by Admins.</li></ul>Revision 2 added the HMAC-MD5 and HMAC-SHA1 suites, generated and manual keys, the permanent command log, the admin page, the protocol subpage and OTA |
| Scope | Small devices report to PNMTD over a custom, very low-data UDP protocol. The server records heartbeats and data as sensor events, can acknowledge them, delivers queued commands when a device reports, and **updates the device's firmware from an uploaded image**. Every command a user sends, and every answer to it, is logged permanently, and the UI shows those bytes as hex and ASCII. This document designs the protocol (§3) and its integration into PNMT (§4–§7) |
| Non-goals | Any change to the sensor API (`/event`, `/read`, frozen by docs/webui-migration-plan.md §3.3). Fragmentation of DATA and command payloads larger than one datagram: only firmware images are chunked (§3.11). Building, signing or verifying firmware: PNMT transports images, and the device and its bootloader decide what to run. Provisioning keys over the air. Gateways for LoRaWAN, MQTT or CoAP |
| References | docs/webui-migration-plan.md (architecture, conventions, roles). LoRaWAN 1.0.x, for the ideas borrowed here: a 16-bit counter on the wire, a 4-byte MIC, a downlink only after an uplink, and a join with nonces. RFC 2104/4231 (HMAC), RFC 2202 (HMAC-MD5 and HMAC-SHA1 test cases), RFC 6151 and RFC 6194 (the security of MD5 and SHA-1, including in HMAC), RFC 4493 (AES-CMAC), RFC 3610 and NIST SP 800-38C (AES-CCM) |

---

## 1. Summary

**Today:** a device reports through `GET /event/<token>/<code>/<message>`. That costs about 1 KB per report over plain HTTP (TCP handshake, headers, JSON answer, teardown), and about 5 KB or more over HTTPS. Battery-powered devices on NB-IoT, LTE-M or metered links pay for every byte and every second the radio is on.

**Target:** PNMTD also listens on a UDP port. A device sends one datagram, for example a 9-byte signed heartbeat (37 bytes on the wire with IPv4). It may receive one datagram in reply, either an acknowledgement or a queued command. The device is a sensor of a new type, `UDP_NODE`, so missing-heartbeat detection, notifications, the dashboard and the uptime bars work as for every other sensor.

| Topic | Decision |
|---|---|
| Transport | UDP on one port, IPv4 and IPv6: **5028/udp by default, configurable** (`PNMTD_UDP_PORT`, §4.9) |
| Exchange model | **Only the device starts an exchange.** Each accepted uplink opens exactly one response slot, used for an ACK or a COMMAND. The server never sends anything unsolicited (§3.1) |
| Wire format | **PNMT-UDP v1:** a 2-byte header, a 1–4-byte device id, a 16-bit counter, the payload and a 0/4/8-byte tag. A heartbeat is **5 to 13 bytes** (§3.2) |
| Packet types | Uplink: HEARTBEAT, DATA, EVENT, CMD_RESULT, BOOT, JOIN. Downlink: ACK, COMMAND, JOIN_ACCEPT (§3.4) |
| Signatures and encryption | The **security suite is chosen per packet**, from the suites the node allows: NONE, TOKEN32, HMAC-SHA256, HMAC-SHA1 and HMAC-MD5, AES-CMAC, and AES-CCM (encrypted), each keyed suite with a 4- or 8-byte tag. Cheap heartbeats and encrypted data can therefore come from the same device. **Responses use the suite the uplink came with**, unless the node (or a single command) is configured to use another (§3.5) |
| Keys | **Generated automatically** when a UDP node is added: a 16-byte key and a 4-byte token. Either can be **overwritten manually** at any time, for devices whose key is fixed in firmware. Stored in **plaintext**, like the sensor tokens (§4.3) |
| Replay protection | A 32-bit frame counter, of which the low 16 bits travel, and a 64-packet window. Devices that cannot persist the counter use JOIN to start a session (§3.7, §3.8) |
| Acknowledgement | Optional. Per node: never, when the packet asks for it (default), or always. A device that gets no answer retransmits identical bytes, and the server answers again without processing them twice (§3.9) |
| Commands | Queued in PostgreSQL and delivered in the response to the node's **next uplink**, whatever its type. Re-sent until the device's CMD_RESULT arrives, up to a maximum number of attempts, with an expiry (§3.10, §4.6) |
| Heartbeats and data | **Recorded as sensor events, as with every other sensor:** 230 for a heartbeat, 232 with the payload as the message for DATA, and 231 for a restart. The existing dedupe keeps a steady heartbeat at two rows. **Only the last packet of each type is kept** with its bytes, overwritten by the next one (§4.2, §4.7) |
| Command log | **The only packet log: the commands users send, and the device's answers to them.** They are stored permanently, with their bytes, in a table of its own, `udp_command_log`: queued, sent, re-sent, the result received, cancelled, expired and failed. Deleting the node does not remove it. The UI shows the decoded header, a hex dump and the ASCII rendering (§4.3, §6.4) |
| Firmware updates (OTA) | **An Admin uploads a `.bin`** and starts an update for one or several nodes. The offer travels in a normal response slot. The device then **pulls the image in chunks** (one uplink per chunk, so the device-initiated model holds), verifies the SHA-256 from the offer, and reports *verified*, *failed* or *applied* (§3.11, §4.11) |
| Admin page | **"UDP nodes" under Admin** lists every UDP node defined, with its settings, key source, firmware, notes and state. It also shows the listener, firmware images and updates, the command log of all nodes and unknown senders (§6.6) |
| Protocol docs in the UI | Each node has a **Protocol** subpage: the protocol reference, with examples built by the server's own codec for this node's id and suites, plus a builder for custom packets (§6.5) |
| Notes | **Every sensor**, of every type, gets a free-text **notes** field, as hosts have (§4.1) |
| PNMT model | A node is a sensor of type `UDP_NODE` (70) with heartbeat semantics: 230 when alive, 232 for data, 231 after a restart, and the existing 460 when missing (§4.1, §4.2) |
| Sensor API | Untouched. The contract replay keeps gating every build |
| Default | **The listener is on.** It answers only packets from nodes defined in PNMT. `Udp__Enabled=false` turns it off (§4.9) |

---

## 2. Requirements and design goals

### 2.1 Requirements (from the request)

| # | Requirement | Where |
|---|---|---|
| R1 | IoT nodes send UDP packets with their id and a short signature | §3.2, §3.5 |
| R2 | Several signature types, including simpler ones | §3.5: NONE, TOKEN32, HMAC with SHA-256, SHA-1 or MD5, and AES-CMAC |
| R3 | The server registers heartbeats | §4.2, plus `HeartbeatCheckTask` for missing ones |
| R4 | An optional automatic acknowledgement | §3.9 |
| R5 | Custom commands, sent only when a packet from the node arrives (which may be every 10 minutes) | §3.10, §4.6 |
| R6 | A very low-data protocol | §3.2, §3.13 |
| R7 | Packet types besides the heartbeat, both unencrypted and encrypted | §3.4 combined with §3.5 (AES-CCM) |
| R8 | Custom data in whatever format the device implements | §3.4 DATA (opaque bytes) |
| R9 | Every response to a command is logged, and its data is shown as ASCII and HEX. Clarified in the review: **only the packets of commands users send are logged.** Heartbeats and data go to sensor events, and the last packet of each type is kept | §4.3 `udp_command_log`, §4.7, §6.3, §6.4 |
| R10 | MD5 and SHA-1 signature options | §3.5 (suites 8–11) |
| R11 | The key is generated when a node is added, and can be overwritten manually | §4.3 *Keys and tokens*, §6.1, §6.2 |
| R12 | Command communication is stored permanently, in its own table | §4.3 `udp_command_log` |
| R13 | An admin page lists all UDP nodes | §6.6 |
| R14 | A notes field on the sensor, for every type (review decision 8) | §4.1, §6.1 |
| R15 | The protocol documentation, with examples, as a subpage of the UDP sensor in the UI | §6.5 |
| R16 | Firmware updates over the air, from an uploaded `.bin` | §3.11, §4.11, §6.6 |

### 2.2 Design goals

1. **Bytes first.** No magic number, no lengths that UDP already provides, and no field that the server can infer. Each header field was checked against "can this be implicit?"
2. **Easy on a microcontroller.** Fixed positions, big-endian, no varints and no TLV parsing. A heartbeat can be built with a few lines of C or with `xxd | nc` (§3.14).
3. **Security that scales up.** A shell script can send an unsigned or token heartbeat, and a field device can use AES-CCM. Both are the same protocol, with only the suite code changed.
4. **Safe as a server on the internet.** No answer to anything invalid, no reflection, no amplification, and invalid packets cause no database writes (§3.12, §4.10).
5. **Fits PNMT.** A node is a sensor. Everything PNMT already does with sensors (hosts, notifications, dashboard, events, uptime) applies without special cases.

### 2.3 Alternatives considered

| Option | Why not |
|---|---|
| HTTP `/event` (exists) | About 1 KB per report, and 5 KB or more with TLS. Needs TCP, which is costly on NB-IoT. It stays for everything that has a TCP stack |
| CoAP with DTLS | A standard, but the DTLS handshake costs hundreds of bytes and keeps session state on the device. Each record adds 13–29 bytes. Mapping it onto sensors would still need custom resources |
| CoAP with OSCORE | Lean on the wire, but complex to implement (CBOR, COSE, a security context). Hardly any small libraries exist |
| MQTT-SN | Needs a gateway and broker, topic registration, and keep-alives. It has no built-in "deliver on the next uplink" |
| LwM2M | Far too heavy for "I am alive" plus a few bytes |

The custom format is the cheapest, and it maps directly onto PNMT's model. It borrows LoRaWAN's proven choices (§References), so it does not invent new cryptography: it only uses HMAC, CMAC and CCM as specified.

---

## 3. The protocol: PNMT-UDP v1

This section is normative. The words MUST, SHOULD and MAY are used as in RFC 2119. Phase U1 copies it to `docs/udp-protocol.md`, the reference for device developers (§8). The UI carries the same documentation, with live examples per node, as the Protocol subpage (§6.5).

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

---

## 4. Integration into PNMTD

### 4.1 A node is a sensor

- **A new sensor type, `UDP_NODE = 70`** (`PNMTD.Lib/Models/Enum/SensorType.cs`). It lives on a host and has a name, Interval and GracePeriod, Enabled and Ignore, like a HEARTBEAT sensor.
- **Its protocol settings and state** are in a 1:1 table, `udp_nodes` (§4.3).
- **Wherever HEARTBEAT is special-cased, UDP_NODE joins it:**
  - `HeartbeatCheckTask` (the 460 check, with its startup grace and uplink guard);
  - `SensorService` (Interval and GracePeriod validation);
  - `SystemService` (the type list on the Documentation page);
  - `SensorTypeRules`;
  - the UI's `useLookups` and `SensorFormView`.
- **The sensor keeps its write and read tokens** (the columns are required). An HTTP `/event` to a UDP node works as for any sensor, which the frozen API requires anyway.
- **Notes on every sensor** (review decision 8):
  - **Storage:** a new nullable column `sensors.notes`, for **all sensor types**, with the rules of the hosts' notes: at most 4000 characters, and blank is stored as null.
  - **API and UI:** `SensorRequest` and `SensorDetail` get `notes`. The sensor form and the sensor page show them for every type (§6.1, §6.2).
  - **Uses:** for a UDP node, for example the location, the SIM or ICCID, the wiring, or when the battery was changed. For a ping sensor, which switch port it watches.
  - **Contract:** the sensor API does not read or return the column, so the contract is unaffected.

### 4.2 Events and status codes

Heartbeats and data are **sensor events, as with every other sensor** (review decision 7). There is no separate packet history for them; §4.7 explains what is kept instead.

| Situation | Event on the node's sensor |
|---|---|
| HEARTBEAT, and the uplinks that only prove the node is alive (CMD_RESULT, the OTA reports, JOIN) | **230**. The message is "Alive", or the heartbeat's status bytes rendered as text when it has some. The message is usually constant, so the existing dedupe keeps a steady stream at two rows that only move their time |
| DATA | **232**, with **the payload as the message**: as text if it is printable UTF-8, otherwise `hex:` followed by the bytes in hex. A VALUECHECK-style regex can be applied in U7 |
| BOOT | **231** "Restarted: watchdog (fw 1.4.2)", with the reason decoded from `REASON` and `INFO` |
| An open OTA download | At most one **230** a minute, so a long download does not end in 460 |
| No accepted uplink for Interval + GracePeriod | **460** "Heartbeat missing", from `HeartbeatCheckTask`, unchanged |
| Rejected packets (bad tag, replay, …) | **None.** They only move the counters and the in-memory list of recent rejections (§4.7), so a spoofer cannot flip the node's state. Alerting on them is §4.10 |
| EVENT (phase U7) | A child sensor per `CHANNEL`, created on demand as ENCAPSULADED creates its siblings, gets `CODE` and `MESSAGE`. Until U7, EVENT packets count as alive (230) |

- **New constants in `PNMTStatusCodes`:** `UDP_ALIVE = 230`, `UDP_BOOT = 231` and `UDP_DATA = 232`. All are success codes (≤ 399). The Documentation page lists them.
- **Events are kept as for every sensor:** the newest 2000 per sensor, and the events page, the dashboard and notifications show them.
- **The event's `Source`** is `udp:` plus the endpoint (`udp:203.0.113.5:40123`, `udp:[2001:db8::1]:40123`). `StripPortFromRemoteAddressIfAny` works on both, so the dedupe behaves as with HTTP.
- **Event writing is extracted** from `SensorApiController` into a shared `SensorEventWriter`: the per-sensor advisory lock, dedupe, value check and trim to 2000. The HTTP path and the UDP path then use the same code. The extraction is an allowed internal change (migration plan §3.3), and the contract replay proves it changed nothing.

### 4.3 Data model (one EF migration, `AddSensorNotesAndUdpNodes`)

**`udp_nodes`**: 1:1 with `sensors`, deleted with the sensor.

| Column | Type | Notes |
|---|---|---|
| `sensor_id` | uuid PK, FK | |
| `device_id` | bigint, unique | 1 … 4,294,967,295. A new node without one gets a **random free id** of `Udp:DeviceIdBytes` bytes (default 2: 1–65,535). A taken id is followed by +1 (past the largest, from 1 on), at most 100 tries; then the creation fails and asks for an id. Editable |
| `key_family` | smallint | `HmacSha256`, `HmacSha1`, `HmacMd5` or `Aes` |
| `allowed_suites` | int | A bitmask of `SUITE` values |
| `key` | bytea(16) | **Plaintext**, like the sensor tokens (review decision 3). Generated or entered, as described under *Keys and tokens* below |
| `token` | bytea(4) | Plaintext |
| `key_source`, `token_source` | smallint | `Generated` or `Manual` |
| `key_changed_at`, `key_changed_by`, `token_changed_at`, `token_changed_by` | timestamptz, uuid (user) | When the value was last generated or entered, and by whom |
| `session_mode` | smallint | `None` or `JoinRequired` |
| `session_key`, `pending_session_key`, `pending_session_at` | bytea null, bytea null, timestamptz null | §3.8, plaintext |
| `ack_mode` | smallint | `Never`, `OnRequest` or `Always` |
| `replay_protection` | bool | Default true |
| `response_suite` | smallint null | **Null (default): answer in the suite the uplink came with.** Otherwise a fixed suite for every downlink, which must be NONE, TOKEN32 or one of the node's family (§3.5) |
| `fcnt_high`, `fcnt_window` | bigint null, bigint | §3.7 |
| `last_uplink_digest`, `last_uplink_fcnt`, `last_uplink_at`, `last_response` | bytea, bigint, timestamptz, bytea null | Retransmissions (§3.9) |
| `last_endpoint`, `last_suite`, `last_boot_at`, `last_boot_reason` | | For the status strip |
| `next_cmd_id`, `next_update_id` | smallint | §3.10, §3.11 |
| `firmware_version` | text null | The running version, from BOOT `INFO` or OTA APPLIED |

A node's notes are the sensor's `notes` (§4.1).

**Keys and tokens:**

- **Generated on creation.** When a UDP node is added, PNMT generates the 16-byte key and the 4-byte token with `RandomNumberGenerator`, so the form needs no input for them. Both are marked `Generated`.
- **Manually overwritable.** A User or Admin may overwrite either value when adding the node or at any time later. This is for devices whose key is fixed in firmware or set by a provisioning tool. The input is:
  - for the key, 32 hex digits;
  - for the token, 8 hex digits;
  - case-insensitive, with spaces, colons and a `0x` prefix allowed.

  The value is then marked `Manual`.
- **Validation:**
  - The length must be exact.
  - Values of one repeated byte (`00…00`, `FF…FF`) are rejected.
  - A key that another node already uses gives a **warning** rather than an error: one compromised device would expose the other. It is a plain query on `udp_nodes.key`.
- **Regenerate** replaces the value with a new random one, which is marked `Generated` again.
- **Every change, manual or generated, takes effect at once:**
  - the cache is invalidated;
  - the active and pending sessions are dropped;
  - the counter window is reset (counters are per key, §3.7);
  - the retransmission cache is cleared.

  Packets under the old key are rejected from then on. The UI says so before saving.
- **Audit:** the change (who, when, which value: key or token, and generated or manual) goes to the audit log. The value itself never appears in a log.
- **Plaintext, as the sensor tokens** (review decision 3). Database dumps (`backup.sh`) therefore contain the device keys, and must be protected like the sensor tokens they already contain (§4.10). In exchange:
  - there is no key ring that could be lost;
  - restoring a dump onto another server just works;
  - no device ever needs re-provisioning because of PNMT.

**`udp_last_packets`**: **only the last packet of each type per node**, overwritten by the next one (review decisions 7 and 10). There is no packet history: the history of heartbeats and data is in the sensor events (§4.2), and that of commands in `udp_command_log`.

| Column | Type | Notes |
|---|---|---|
| `sensor_id`, `type` | uuid FK, smallint, PK together | One row per node and uplink type: HEARTBEAT, DATA, EVENT, BOOT, JOIN. An upsert on every accepted uplink of that type. Deleted with the node |
| `received_at`, `endpoint`, `suite`, `fcnt` | timestamptz, text, smallint, bigint | |
| `datagram` | bytea | The bytes as received |
| `payload` | bytea null | The plaintext payload. **For AES-CCM, decrypted** |
| `response` | bytea null | What PNMT answered (ACK, JOIN_ACCEPT), if anything |

CMD_RESULT and the OTA reports are not kept here: they are in the command log. OTA GETs are not kept at all.

**`udp_commands`**: the commands and their current state. **Permanent.**

| Column | Type | Notes |
|---|---|---|
| `id` | uuid | |
| `sensor_id` | uuid FK null | Set to null when the node is deleted (`ON DELETE SET NULL`), so the command survives |
| `device_id`, `node_name` | bigint, text | Snapshots, so the command stays readable after the node is renamed or deleted |
| `cmd_id` | smallint | The id on the wire |
| `payload` | bytea | |
| `state` | smallint | §4.6 |
| `delivery` | smallint | `ResultRequired` or `FireAndForget` |
| `suite` | smallint null | Null: the node's response suite. Otherwise this command's own suite (§3.10) |
| `max_attempts`, `attempts` | smallint | |
| `created`, `created_by` | timestamptz, uuid (user) | Who queued it |
| `expires_at`, `last_sent_at`, `completed_at` | timestamptz null | |
| `result_status`, `result` | smallint null, bytea null | From CMD_RESULT |

The table has an index on `(sensor_id, state, created)`.

**`udp_command_log`**: **the permanent record of all command communication, and the only log with packet bytes.** It holds the packets a user caused (commands, and firmware milestones) and the device's answers to them. There is one row per step, written in the same transaction as the step itself.

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `created` | timestamptz | |
| `command_id` | uuid FK null | Null for an unmatched result and for OTA rows |
| `ota_job_id` | uuid FK null | OTA rows |
| `sensor_id` | uuid FK null | `ON DELETE SET NULL` |
| `device_id`, `node_name` | bigint, text | Snapshots, as in `udp_commands` |
| `kind` | smallint | Commands: `Queued`, `Sent`, `Resent` (the answer to a retransmission, §3.9), `ResultReceived`, `UnmatchedResult`, `Cancelled`, `Expired` or `Failed`. Updates: `OtaQueued`, `OtaOffered`, `OtaStarted` (the first GET), `OtaDeclined`, `OtaVerified`, `OtaFailed`, `OtaApplied`, `OtaCancelled` or `OtaExpired`. **The chunks themselves are not logged one by one** (§4.11) |
| `direction` | smallint null | `Down` for Sent and Resent, `Up` for the two result kinds, null for the others |
| `cmd_id` | smallint | The id on the wire |
| `attempt` | smallint null | Sent and Resent |
| `endpoint`, `suite`, `fcnt` | text, smallint, bigint, all null | Of the packet. The FCNT is the uplink's, which a downlink echoes |
| `datagram` | bytea null | The bytes on the wire, as received or sent |
| `payload` | bytea null | For Queued, Sent and Resent: the command bytes. For results: `STATUS ‖ RESULT`, decrypted for AES-CCM |
| `result_status` | smallint null | The results' `STATUS` |
| `user_id`, `user_name` | uuid null, text null | For Queued and Cancelled: who did it (the name as a snapshot) |
| `note` | text null | For example "max attempts reached" or "unknown CMD_ID 9" |

The table has indexes on `(command_id, created)`, `(sensor_id, created)` and `(created)`.

**How the command tables are kept:**

- **Permanent** means that nothing in the app deletes rows from `udp_commands` or `udp_command_log`: not the hourly task, not deleting the node, and not the UI. They are in PostgreSQL, so `backup.sh` includes them.
- **Volume:** a row is a few hundred bytes. Even 100 nodes with 10 commands a day each come to about 1 million rows, roughly 300 MB, a year.

**`udp_firmware`**: uploaded images. Kept until an Admin deletes them, which is refused while an open update uses the image.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid | |
| `name`, `version` | text | For example "Pump controller" and "1.5.0". `version` is the `VERSION` in the offer |
| `file_name`, `size`, `sha256` | text, int, bytea(32) | The hash is computed on upload |
| `content` | bytea, `STORAGE EXTERNAL` | The image. Uncompressed TOAST, so reading a range does not read the whole value |
| `notes` | text null | Change log, target hardware |
| `uploaded_at`, `uploaded_by` | timestamptz, uuid | |

**`udp_ota_jobs`**: one update of one node. **Permanent**, like the commands.

| Column | Type | Notes |
|---|---|---|
| `id` | uuid | |
| `sensor_id` | uuid FK null | `ON DELETE SET NULL` |
| `device_id`, `node_name` | bigint, text | Snapshots |
| `firmware_id` | uuid FK null | `ON DELETE SET NULL`, because firmware may be deleted after the job ends |
| `firmware_name`, `firmware_version`, `firmware_size`, `firmware_sha256` | | Snapshots |
| `update_id` | smallint | The id on the wire |
| `chunk_size` | smallint | 64–480 |
| `state` | smallint | `Queued`, `Offered`, `Downloading`, `Verified`, `Applied`, `Declined`, `Failed`, `Cancelled` or `Expired` |
| `reason` | smallint null | From DECLINE or FAILED |
| `offer_attempts`, `max_offer_attempts` | smallint | |
| `bytes_served`, `chunks_served`, `chunk_retransmissions`, `highest_offset` | bigint, int, int, bigint | Progress |
| `created`, `created_by`, `offered_at`, `started_at`, `last_request_at`, `finished_at`, `expires_at` | | |
| `applied_version` | text null | |

### 4.4 Server components

```
PNMTD.Lib/Udp/                     pure: no I/O, no database, so testable with vectors alone
  PacketHeader.cs, PacketType.cs, SecuritySuite.cs
  PacketCodec.cs                   parse and build (§3.2–§3.4)
  PacketTags.cs                    TOKEN32, HMAC (HMACSHA256/HMACSHA1/HMACMD5.HashData), AES-CMAC (RFC 4493 over
                                   Aes.EncryptEcb, about 60 lines, unless the BCL has it), AES-CCM (AesCcm)
  FrameCounterWindow.cs            reconstruction and the 64-packet window (§3.7)
  SessionKeys.cs                   the KDFs (§3.8)
PNMTD/Udp/
  UdpListener.cs                   BackgroundService: dual-stack socket, receive loop, a bounded channel
  UdpPacketProcessor.cs            the pipeline of §4.5
  UdpNodeCache.cs                  device id → node snapshot (keys, settings), invalidated on edits
  UdpCommandDispatcher.cs          picks the command or OTA offer for a response slot (§3.10, §3.11)
  UdpOtaServer.cs                  answers OTA GETs from UdpFirmwareCache, tracks progress (§4.11)
  UdpFirmwareCache.cs              images of open updates in memory (LRU, bounded)
  UdpStats.cs                      counters per reason and per node, recent rejections, unknown senders (§4.7)
  UdpMaintenanceTask.cs            expires commands and offers, marks stalled downloads (hourly); deletes nothing
PNMTD/Services/Domain/UdpNodeService.cs, UdpCommandService.cs, UdpFirmwareService.cs, UdpOtaService.cs
PNMTD/Services/Domain/UdpProtocolDocs.cs     the examples of the Protocol subpage, built with the codec (§6.5)
PNMTD/Services/Domain/SensorEventWriter.cs   extracted from SensorApiController (§4.2)
PNMTD/Api/V1/UdpController.cs, Contracts/Udp.cs
PNMTD/Cli/UdpCommand.cs            pnmtd udp send | node | reset-counter (§7)
```

**Concurrency:**

- One receive loop reads datagrams into a bounded `Channel` (10,000 entries). When the channel is full, new datagrams are dropped and counted.
- *N* workers (default 4) take the work. Datagrams are **partitioned by device id**, so the packets of one node are processed in order: counter, window and commands. Packets of different nodes run in parallel.
- The row's `SELECT … FOR UPDATE` inside the transaction is the second guard.
- `Socket.SendToAsync` on the shared socket is safe from several workers.

**Hosting:**

- Registered like the other hosted services, **on by default** (`Udp:Enabled`, default true), and not behind `BackgroundTasks:Enabled`. The integration tests run the listener on a random port.
- If the port cannot be bound, the error is logged and shown on System health. The app keeps running: HTTP and the sensor API must not fail because of UDP.

### 4.5 Processing pipeline

The cheap checks come first, and nothing before step 10 touches the database.

| # | Step | On failure |
|---|---|---|
| 1 | Size between 5 and 508 bytes | Dropped. `malformed` is counted |
| 2 | `VER = 01`, an uplink type, `RSV = 0` and `M = 0`, a known suite | Dropped, counted |
| 3 | The id, the counter and the tag fit in the datagram | Dropped, counted |
| 4 | The id belongs to a node (cache) | The unknown-senders ring (§4.7), dropped |
| 5 | The sensor is enabled | Rejected as `Disabled` |
| 6 | The suite is allowed, and the session mode fits (JOIN versus session key) | Rejected as `SuiteNotAllowed` or `SessionRequired` |
| 7 | Rate limit per node (token bucket, default 30/min, burst 10) and per source address | Rejected as `RateLimited` |
| 8 | The tag verifies, and AES-CCM decrypts. For JoinRequired, the pending session is tried first, then the active one | Rejected as `BadTag` |
| 9 | Counter: a retransmission? | **The stored response is sent again**, and processing stops. If the response was a COMMAND, a `Resent` row goes to `udp_command_log` |
| 10 | Counter: a replay, or a jump over 16384? (§3.7) | Rejected as `Replay` or `CounterJump` |
| 11 | The payload is valid for the type (§3.4) | Rejected as `Malformed` |
| 12 | **One transaction:**<ul><li>update the counter window, `last_*` and the session promotion;</li><li>write the event (230, 231 or 232, §4.2) through `SensorEventWriter`;</li><li>upsert the type's row in `udp_last_packets`;</li><li>match a CMD_RESULT to its command;</li><li>pick the response (JOIN_ACCEPT, COMMAND, OTA_OFFER, ACK or nothing) and its suite (§3.5), and mark the command's attempt;</li><li>write the permanent `udp_command_log` rows: `ResultReceived` or `UnmatchedResult`, `Sent`, and `Failed`, with their datagrams</li></ul> | A database error means no response. The device retries later: no answer means "not stored" |
| 13 | After the commit: send the response, and store it as `last_response` | |

**"Rejected"** means the counter for that reason goes up, and the packet joins the node's in-memory list of recent rejections (§4.7). **Nothing is written to the database for a rejected packet**, so a flood of forged packets cannot fill the disk.

**An OTA GET takes a short path** after step 11, because thousands of them arrive in a row:

- update the counter window and `last_*`, and the job's progress counters, in a small batch (at most once a second per job, plus on the job's state changes);
- read the chunk from `UdpFirmwareCache` and send it.

An OTA GET writes no command-log row and keeps no last packet (§4.7, §4.11). Its only event is the 230 at most once a minute (§4.2). The other OTA OPs take the normal path, and their outcome goes to the command log.

### 4.6 The life of a command

```
(queued by a user)
Queued ──(delivered in a response)──► Sent ──(CMD_RESULT with its CMD_ID)──► Completed
  │                                    │ ├──(max attempts reached, no result)──► Failed
  │                                    │ └──(FireAndForget: Sent is final)
  ├──(expiry)──► Expired ◄──(expiry)───┤
  └──(cancel)──► Cancelled ◄─(cancel)──┘
```

- **Completed** stores `STATUS` and `RESULT`. The UI shows the result as hex and ASCII and marks `STATUS ≠ 0` as an error.
- **An attempt** is one delivery in the response to a *new* uplink. A retransmission's resend is the same attempt.
- **A CMD_RESULT** whose `CMD_ID` matches no Sent command goes to the command log as `UnmatchedResult`, with its bytes, so a late or unexpected answer is never lost.
- **The suite** of each send is the command's own, else the node's response suite, else the uplink's (§3.5). The command log records which one was used.
- **Failed** is decided at the first delivery slot after the last attempt without a result, or by the hourly task once the command has expired.
- **Expiry** is checked on every delivery and by the hourly task.
- **Every transition writes a row to `udp_command_log`** (§4.3): `Queued` and `Cancelled` from the API with the user, `Sent`, `Resent`, `ResultReceived`, `UnmatchedResult` and `Failed` from the processor with the datagram, and `Expired` from the task. Commands and their log are permanent, and nothing removes them.

### 4.7 What is kept

**Only the packets that users cause are logged** (review decision 10): commands and their answers, and the firmware milestones, all in the permanent `udp_command_log`. Everything else is kept only as far as it is useful for monitoring:

| What | Where it goes | How long |
|---|---|---|
| HEARTBEAT, DATA, BOOT (and EVENT until U7) | A **sensor event** (§4.2), plus the **last packet of the type** with its bytes in `udp_last_packets`, overwritten each time | Events as for every sensor (2000 per sensor). One last packet per type |
| JOIN and JOIN_ACCEPT | The last JOIN, with the answer, in `udp_last_packets` | One |
| COMMAND, its resends, CMD_RESULT, and unmatched results | `udp_command_log`, with the bytes | **Permanent** |
| OTA_OFFER and the OTA reports | `udp_command_log` (the `Ota…` kinds), with the bytes | **Permanent** |
| OTA GETs and chunks | Only the job's progress counters. A 1 MiB update means about 4,700 datagrams, and the bytes are in the image anyway | – |
| ACKs | `udp_last_packets.response` of the uplink they answered | One |
| Rejected packets of known nodes | **In memory only:** the counters per reason, and the node's **last 20 rejections** (time, reason, address, bytes) | Until the restart |
| Unknown device ids, unparseable datagrams | **In memory only:** a ring of the last 200 entries, grouped by (device id, source address), with first and last seen, a count, the last suite and the last datagram's bytes. The admin page shows it, so that a new device that is already sending can be adopted (§6.6) | Until the restart |

- **Nothing in the database grows with the traffic** except the sensor events (which are bounded) and the command log, which grows with what users do.
- **Statistics** come from in-memory counters per node, in 24 hourly buckets. They start again after a restart:
  - packets in the last 24 hours;
  - **loss** in the last 24 hours, from the counter gaps: `1 − accepted / (max FCNT − min FCNT + 1)`. A free radio-quality metric;
  - rejected packets by reason;
  - bytes in and out.

### 4.8 Configuration

```jsonc
"Udp": {
  "Enabled": true,                    // on by default; Udp__Enabled=false turns the listener off
  "Port": 5028,                       // Udp__Port; the compose file sets it from PNMTD_UDP_PORT (§4.9)
  "PublicEndpoint": null,             // "pnmt.example.com:5028" for the UI; default: PublicBaseUrl's host + Port
  "Workers": 4,
  "QueueLength": 10000,
  "RetransmissionWindowSeconds": 300,
  "DeviceIdBytes": 2,                 // a new node's random id fits in 1–4 header bytes (§4.3)
  "RateLimit": { "PerNodePerMinute": 30, "PerNodeBurst": 10, "PerAddressPerSecond": 50 },
  "Diagnostics": { "RecentRejectionsPerNode": 20, "UnknownSenders": 200 },
  "Commands": { "MaxOpenPerNode": 32, "DefaultExpiryHours": 24, "DefaultMaxAttempts": 3, "MaxBytes": 480 },
  "Ota": {
    "MaxImageBytes": 16777216,        // 16 MiB per upload
    "DefaultChunkBytes": 448,
    "DefaultOfferExpiryDays": 7, "DefaultMaxOfferAttempts": 20,
    "GetsPerSecondPerNode": 20,       // the raised rate limit during an update
    "CacheBytes": 67108864            // 64 MiB of images in memory
  }
}
```

### 4.9 Deployment

- **The port is 5028 by default, and configurable** (review decision 1). One setting in `.env`, `PNMTD_UDP_PORT`, changes both the published port and the listener's port, so the two cannot drift apart:
  ```yaml
  ports:
    - "${PNMTD_UDP_BIND_ADDRESS:-0.0.0.0}:${PNMTD_UDP_PORT:-5028}:${PNMTD_UDP_PORT:-5028}/udp"
  environment:
    Udp__Port: ${PNMTD_UDP_PORT:-5028}
  ```
  The bind address is separate from HTTP's, because HTTP is often bound to 127.0.0.1 behind a reverse proxy, while UDP must be reachable from the devices.
- **On by default** (review decision 2). An upgrade to the release with UDP therefore publishes 5028/udp. The listener answers only packets from nodes defined in PNMT, so with no nodes it never answers anything. The release notes and `UPGRADING` say so. `Udp__Enabled=false` in `.env` turns it off; the port then stays published but closed (ICMP "port unreachable").
- **Dockerfile:** `EXPOSE 5028/udp`. Binding a port above 1024 as the non-root user needs no capability. `read_only`, `cap_drop: [ALL]` and `no-new-privileges` stay.
- **`start.sh`:** `-p ${UDP_PORT:-5028}:5028/udp`, so `pnmtd udp node` can talk to the debugging stack at once.
- **`info.sh`** prints the UDP endpoint and whether the listener is on.
- **`deploy/README.md`:**
  - **No reverse proxy is involved.** Caddy, nginx and Traefik as configured there proxy HTTP. UDP goes straight to the container, so TLS termination does not apply, and none is needed: the suites authenticate.
  - **Firewalls:** Docker's published ports bypass `ufw`, so 5028/udp is usually reachable as soon as the stack runs. A cloud security group still needs a rule for it.
  - The **source address** stays the client's with Docker's iptables NAT. Rootless Podman with slirp4netns hides it; with pasta it is kept. Responses work either way, because they travel back through the same NAT. Only the address PNMT shows and puts in the events is affected.
- **The health check** (`/healthz`) stays database-only. The listener's state is on System health and in `info.sh`.
- **Firmware uploads** need no new volume: the images are in PostgreSQL and in `backup.sh`'s dump. The upload endpoint alone raises Kestrel's request body limit to `Udp:Ota:MaxImageBytes`. A reverse proxy in front of the UI needs the same limit (`client_max_body_size` in nginx); `deploy/README.md` lists it per proxy.

### 4.10 Security and abuse

| Threat | Mitigation |
|---|---|
| Forged heartbeats or data | Suites 2–11. Per node, only the allowed suites are accepted. NONE and TOKEN32 must be chosen deliberately, and the UI marks them as weak. MD5 and SHA-1 are used only inside HMAC (§3.5) and are marked *legacy* |
| Replay of recorded packets | The counter and its window (§3.7), and JOIN sessions (§3.8) |
| Forged commands to the device | The device verifies the tag and the echoed counter (§3.15). Commands go out in the received suite by default. For nodes that report with NONE or TOKEN32, the UI warns and suggests a keyed response suite for commands (§3.5, §3.10) |
| Brute-forcing a 32-bit tag | Per-node rate limits. A **"rejected packets" warning**: more than 20 bad tags in 10 minutes logs a warning, which the node's page, the admin page *UDP nodes* and System health show. 64-bit suites for command-capable nodes |
| Reflection and amplification | No answer to invalid packets. Responses are no larger than their uplinks, except for commands, which are bounded. Downlink types arriving at the server are dropped (§3.12) |
| Enumerating device ids | Unknown ids get no answer. Behaviour is the same for unknown and wrong-key |
| Flooding | The bounded queue, rate limits, no database write for rejected packets, and the bounded in-memory lists. Target: 5,000 datagrams/s of junk without delaying valid packets (§9) |
| Theft of a database backup | **Device keys and tokens are in plaintext** (review decision 3), as the sensor tokens already are. A dump therefore lets someone impersonate nodes and the server. `backup.sh` already writes its folder as 0700 and its files as 0600. `deploy/README.md` adds that backups are credentials. If a dump leaks, regenerate the keys (§4.3) |
| A spoofer flipping a node to failed | Impossible: rejected packets create no events (§4.2) |
| Malicious firmware pushed through PNMT | Upload and update are **Admin only** and go to the audit log. The device verifies the SHA-256 from the authenticated offer and, as recommended, the image's signature in its bootloader. That signature check is the real protection, because PNMT does not sign images (§3.11) |
| A forged or tampered image in transit | Every chunk is authenticated with the node's key, and the whole image's SHA-256 is checked on the device. AES-CCM also keeps it confidential |
| OTA as an amplifier | OTA answers keyed suites only, under the raised but bounded per-node rate limit (§3.11) |
| The listener on by default exposes a new port | It answers only defined nodes, and only valid packets (§3.12). Rejected packets cost no database writes. `Udp__Enabled=false` turns it off |
| Bricked devices | The device's job: A/B slots, a self-test and rollback, reported as FAILED reason 5. PNMT's job: one job per node, a dry run on one node first (the UI suggests it for multi-node updates), and *applied* only on the device's own report |

### 4.11 Firmware updates on the server

- **Upload:** an Admin uploads a `.bin` (multipart, at most `MaxImageBytes`) with a name, a version and notes.
  - The server computes the SHA-256 and stores the image in `udp_firmware`.
  - It warns, without refusing, when an image with the same hash already exists.
  - It does **not** parse the image. A *magic check* is shown as a hint only: ESP32 images start with `E9`, and MCUboot images with `3D B8 F3 96`.
- **Starting an update:** an Admin picks an image and one or several nodes (from the node's page or the admin page), the chunk size, the expiry and the attempts. This creates one `udp_ota_jobs` row per node, in state `Queued`, plus an `OtaQueued` row in the command log.
  - A node that already has an open update, or allows no keyed suite, is refused with the reason.
  - The dialog shows the expected volume and duration per node (§3.13).
- **Delivery:** the dispatcher offers the job in the node's next free slot (after its commands) and moves it to `Offered`. The first GET moves it to `Downloading`. VERIFIED, FAILED, DECLINED and APPLIED move it on (§3.11), and each transition writes its command-log row. **The node's `firmware_version`** is updated by APPLIED and by every BOOT's `INFO`.
- **Serving chunks:** `UdpOtaServer` answers from `UdpFirmwareCache`.
  - The cache loads an image once from PostgreSQL when its first GET arrives, and evicts it least recently used within `CacheBytes`.
  - It never runs a database query per chunk.
  - Progress (`bytes_served`, `highest_offset`, re-fetches, `last_request_at`) is flushed at most once a second per job.
- **Concurrency:** several nodes may download at once. Each is bound by its own rate limit, and the shared cache holds each image once.
- **Cancelling:** an Admin may cancel an open job. The next GET for it gets the abort answer (§3.11), and an offer not yet answered is no longer sent.
- **Expiry:** an offer expires after `DefaultOfferExpiryDays`. A download that has seen no GET for 24 h is marked `Failed` ("stalled") by the hourly task.

---

## 5. API v1

Following the conventions of the migration plan (§3.7): plural resources, ProblemDetails, camelCase, UTC timestamps, string enums, and paging. **Bytes travel as Base64** in JSON. The UI renders hex and ASCII itself (§6.7).

**For the admin page (§6.6):**

| Method | Path | Policy | Notes |
|---|---|---|---|
| GET | `/api/v1/udp/status` | Admin | Listener on or off, the endpoint, a bind error if any, the counters by reason, packets per minute, the queue depth and dropped datagrams |
| GET | `/api/v1/udp/nodes` | Admin | **Every UDP node defined, including disabled ones.** For each: the sensor, host, device id, enabled, state, key family, allowed suites, key and token source with when they changed, session and ACK mode, last seen, last address, last suite, FCNT, open commands, and rejected packets and loss in 24 h. Paged on the server, with search (name, host, device id), filters (state, family, enabled) and sorting |
| GET | `/api/v1/udp/command-log` | Admin | `udp_command_log` across all nodes, deleted ones included. Paged. Filters: `sensorId`, `deviceId`, `kind`, `from`, `to` |
| GET | `/api/v1/udp/unknown-senders` | Admin | The in-memory ring (§4.7) |
| GET | `/api/v1/udp/firmware` | Admin | Uploaded images: name, version, size, SHA-256, notes, who uploaded them and when, and how many jobs use them |
| POST | `/api/v1/udp/firmware` | Admin | Multipart: `file`, `name`, `version`, `notes`. Returns the SHA-256 and the magic hint (§4.11). Audit-logged |
| PUT, DELETE | `/api/v1/udp/firmware/{id}` | Admin | Edit the name, version and notes, or delete. Delete is refused while an open job uses the image. Audit-logged |
| GET | `/api/v1/udp/firmware/{id}/content` | Admin | Downloads the `.bin` |
| GET | `/api/v1/udp/ota-jobs` | Admin | All updates, paged. Filters: `state`, `firmwareId`, `sensorId`, `from`, `to`. Includes progress |
| POST | `/api/v1/udp/ota-jobs` | Admin | `{ firmwareId, sensorIds[], chunkSize, expiresInDays, maxOfferAttempts }`. One job per node. The response lists the nodes refused, with the reason. Audit-logged |
| DELETE | `/api/v1/udp/ota-jobs/{id}` | Admin | Cancels an open job (§4.11) |

**Per node:**

| Method | Path | Policy | Notes |
|---|---|---|---|
| GET | `/api/v1/sensors/{id}` | CanRead | **For every sensor type**, `SensorDetail` gets `notes`. For a UDP node it also gets `udp`: settings, state, 24-hour statistics, `keySource`, `tokenSource`, when they changed, and `listenerEnabled`, which the form needs without the Admin-only status. The key and the token are in `integration`, which is null for a Viewer, as today |
| POST, PUT | `/api/v1/sensors` | CanWrite | **For every sensor type**, `SensorRequest` gets `notes` (at most 4000 characters, as on hosts). For a UDP node it also gets `udp`: device id, key family, allowed suites, session mode, ack mode, **`responseSuite`** (null = as received), replay protection, and **optional `key` and `token`** (hex). On POST, an omitted value is **generated**, and a given one is stored as `Manual`. On PUT, an omitted value stays as it is. Validation as in §4.3. A key that another node already uses comes back as a warning in the response |
| PUT | `/api/v1/sensors/{id}/udp/key` | CanWrite | `{ key?, token? }`: **overwrites manually**, with the effects of §4.3. Audit-logged |
| POST | `/api/v1/sensors/{id}/udp/regenerate-key` | CanWrite | `{ key: bool, token: bool }`: new random values. The device must be re-provisioned. Audit-logged |
| POST | `/api/v1/sensors/{id}/udp/reset-counter` | CanWrite | §3.7. Audit-logged |
| POST | `/api/v1/sensors/{id}/udp/reset-session` | CanWrite | Drops the active and the pending session |
| GET | `/api/v1/sensors/{id}/udp/last-packets` | CanRead | The node's `udp_last_packets`, one per type, with their bytes and the answer sent (§4.7) |
| GET | `/api/v1/sensors/{id}/udp/rejections` | CanRead | The node's recent rejections, from memory: time, reason, address, bytes |
| GET | `/api/v1/sensors/{id}/udp/commands` | CanRead | Paged |
| GET | `/api/v1/sensors/{id}/udp/commands/{commandId}/log` | CanRead | The command's rows from `udp_command_log`, oldest first, with their bytes |
| POST | `/api/v1/sensors/{id}/udp/commands` | CanWrite | `{ payload (Base64), delivery, suite?, expiresInHours, maxAttempts }`. `suite` is null for the node's response suite (§3.5). Validates the size, the open-command limit, and that the suite belongs to the node. Returns a warning when the command would go out unsigned or with only a token |
| DELETE | `/api/v1/sensors/{id}/udp/commands/{commandId}` | CanWrite | Cancels a Queued or Sent command |
| GET | `/api/v1/sensors/{id}/udp/ota-jobs` | CanRead | The node's updates with progress, and each job's command-log rows |

**For the Protocol subpage (§6.5):**

| Method | Path | Policy | Notes |
|---|---|---|---|
| GET | `/api/v1/udp/protocol/examples?sensorId=` | CanRead | The worked examples of §6.5, **built by the server's own codec** (`UdpProtocolDocs`). Each comes as the datagram (Base64) plus a field map (offset, length, field, decoded value). Without `sensorId`, the reference key and the vectors of §3.14. With `sensorId`, the node's device id, suites and family. They use the node's real key only for CanWrite; a Viewer gets the reference key and a note saying so |
| GET | `/api/v1/udp/protocol/build` | CanRead | The packet builder: `?sensorId&type&suite&fcnt&ack&payload` (Base64) → the datagram and its field map, keyed as above. Only builds, never sends |

The OpenAPI document and the generated UI types (`openapi.json`, `schema.d.ts`) are updated by the build as usual.

---

## 6. Web UI

### 6.1 The sensor form

- **Notes, for every sensor type:** a multi-line text field, as on the host form, at most 4000 characters (§4.1).
- **The type "UDP node"** shows a *UDP* section:
  - Device id: optional, a random free one when empty (§4.3); shown as its header bytes too (`IDL 2 bytes · 9C 41`).
  - Key family: HMAC-SHA256 (default), AES, HMAC-SHA1 or HMAC-MD5 (the last two marked *legacy*).
  - **Key and token:** "Generated automatically when saved" by default, so nothing needs to be entered. *Enter manually* opens hex fields for the key (32 digits) and the token (8 digits). They validate as you type and have a *Generate* button that fills in a random value to copy into the firmware first. The panel shows the generated values after saving.
  - Allowed suites: checkboxes with each suite's tag size and a "weak" or "encrypted" mark.
  - **Response suite:** "As received" (default), or one suite from the allowed list. A hint warns when commands could go out unsigned or with only a token (§3.5).
  - Session mode, ACK mode, replay protection.
  - Interval and GracePeriod as for HEARTBEAT, with the hint "10 min + 5 min grace".
- **When the listener has been turned off** (`Udp__Enabled=false`), the form says so, with the setting that turns it on again.

### 6.2 The sensor page of a node

- **Notes** appear under the page header, as on the host page, and are editable inline for User and Admin (`InlineField`). This applies to every sensor type, not just UDP nodes.
- **The integration panel.** `UdpNodePanel.vue` takes the place of `SensorUrlPanel` for this type. It shows:
  - the endpoint `pnmt.example.com:5028/udp`;
  - the device id;
  - the key, hidden until revealed, and copyable as hex or as a C array (`{0x00, 0x01, …}`);
  - the token;
  - for both, their source: "generated on 12 Nov 2026" or "entered manually on 12 Nov 2026 by alice";
  - a ready-made example for the chosen suite (the bytes of a heartbeat with counter 1), the `xxd | nc` line for TOKEN32 and NONE, and the `pnmtd udp send` line;
  - **"Set manually…"** for the key or the token: a dialog with the hex field and its validation, including the warning when another node uses the same key;
  - **"Regenerate"** for the key or the token;
  - the settings in one line (suites, response suite, ACK, session, replay protection), and **"Edit settings…"**: a dialog with the form's UDP section for the existing node. Empty id, key and token stay as they are; a new key family brings a new key. Before saving it says what the change means for the device (a new id, suites it may no longer use, JOIN on or off).

  Both actions sit behind a confirmation that says the device stops working until it has the new value, and that sessions and the counter are reset (§4.3).
- **The status strip:**
  - last seen (relative), next expected, and the last address and suite;
  - FCNT and session state;
  - packets, loss and rejected packets by reason in the last 24 hours;
  - the last restart and its reason;
  - the firmware version, with an open update's progress.
- **Subpages**, shown as tabs, each with its own route so they can be linked:

  | Tab | Route | Content |
  |---|---|---|
  | *Events* | `/sensors/:id` | As today. Heartbeats (230), data (232) and restarts (231) are here, as for every sensor |
  | *Last packets* | `/sensors/:id/last-packets` | §6.3 |
  | *Commands* | `/sensors/:id/commands` | §6.4 |
  | *Firmware* | `/sensors/:id/firmware` | The running version, the node's updates with progress bars, their command-log timeline, and, for an Admin, *Update firmware…* and *Cancel* (§4.11) |
  | *Protocol* | `/sensors/:id/protocol` | §6.5 |

### 6.3 Last packets, and the packet inspector

There is no packet history (§4.7), so this tab shows **the last packet of each type** and **the recent rejections** held in memory:

| Type | Received | FCNT | Suite | Bytes | Preview | Answer |
|---|---|---|---|---|---|---|
| HEARTBEAT | 14:22:07 | 42 | HMAC-SHA256/32 | 9 | | ACK |
| DATA | 14:32:09 | 43 | HMAC-SHA256/64 | 18 | `21.5C` | – |
| BOOT | yesterday 03:10 | 1 | HMAC-SHA256/32 | 15 | `·1.5.0` | – |

*Recent rejections (since the restart):* 14:41:55, BadTag, 198.51.100.7:5028, 9 bytes. A firmware developer sees at once why a packet was refused.

**The packet inspector** is a shared component (`PacketInspector.vue`). It opens from a row here, from every sent or received step of the command log (§6.4), and from the Protocol subpage's examples (§6.5). It shows three blocks:

**The decoded header:**

| Field | Value |
|---|---|
| Version | 1 |
| Type | DATA (0x1) |
| Flags | – |
| Suite | HMAC-SHA256/64 |
| Device id | 7 |
| Counter | 43 (wire `00 2B`) |
| Tag | `35 0A B3 32 32 76 BB 45` ✓ |

**The datagram as a hex dump**, with offset, 16 bytes per line, and ASCII where non-printable bytes are `·`. Each line has a copy button for its hex.

```
0000  44 03 07 00 2B 32 31 2E  35 43 35 0A B3 32 32 76  D···+21.5C5··22v
0010  BB 45                                             ·E
```

**The payload**, as ASCII and hex side by side. For AES-CCM it is labelled *decrypted* and the wire bytes stay visible above it. A *Text/Hex* toggle chooses the preview column.

The tab refreshes every 5 s while it is visible (`refetchInterval`).

### 6.4 Commands tab (R9, R12)

**This is the packet log of a node** (review decision 10): every command a user sent, and every answer the device gave, with their bytes.

- **The list:**
  - the state badge, when the command was queued and by whom, and its `CMD_ID`;
  - the command as a hex and ASCII preview;
  - attempts of the maximum, and the expiry;
  - the result: `STATUS` in words, and `RESULT` as hex and ASCII.
- **Expanding a command** shows its **communication**, from the permanent `udp_command_log`, as a timeline. For example:
  - Queued by alice;
  - Sent, attempt 1, to 203.0.113.5:40123 with HMAC-SHA256/32, in answer to FCNT 44;
  - Resent;
  - Result received: OK.

  Each sent or received step opens in the packet inspector (§6.3): the decoded header, the datagram as a hex dump with ASCII, and its payload (the command bytes, or `STATUS` and `RESULT`) as hex and ASCII. This history never ages out.
- **Actions:** cancel, and *send again*, which copies a command into a new one.
- **"New command"** opens a dialog:
  - input as **Text** (UTF-8) or **Hex** (`02 02 58`, spaces allowed);
  - a live preview: the hex dump and ASCII, plus "13 bytes on the wire with HMAC/32";
  - the delivery mode, the expiry and the attempts;
  - **the suite**: "Node default" (its response suite, by default as received), or one of the node's suites, for example AES-CCM/64 to encrypt this command;
  - a delivery estimate: "Delivered with the node's next packet. The last arrived 3 min ago, every 10 min → around 14:32";
  - a warning when the command could go out unsigned or with only a token, which happens when the node reports with NONE or TOKEN32 and no keyed suite is set.

### 6.5 Protocol subpage (R15)

**The protocol documentation is built into the UI**, as a subpage of every UDP node (`/sensors/:id/protocol`). A node-independent copy, with the reference key, sits at `/admin/udp-nodes/protocol` and is linked from the admin page and the Documentation page.

- **Content.** The same structure as §3 of this plan (later `docs/udp-protocol.md`), written for device developers:
  1. the exchange model, with its diagram;
  2. the packet layout, as a bit diagram and a field table;
  3. packet types and payloads;
  4. security suites, **with this node's allowed suites highlighted** and the others greyed out;
  5. tags, encryption and nonces;
  6. counter and replay rules, JOIN, ACKs and retransmission;
  7. commands;
  8. OTA;
  9. the device checklist;
  10. code: Python, C (pseudo-code), shell.

  The text lives in Vue components (`views/sensors/udp/protocol/`). There is no markdown renderer and no extra dependency.
- **Worked examples for this node.** Each example is built by the server's own codec (`GET /api/v1/udp/protocol/examples`), so the documentation can never drift from the implementation.
  - The examples: a heartbeat with ACK requested; the ACK; DATA `"21.5C"`; a COMMAND and its CMD_RESULT; BOOT; for JOIN-mode nodes, JOIN and JOIN_ACCEPT; an OTA offer, GET and chunk.
  - Each uses one of the node's allowed suites, its device id, and its key. A Viewer gets the reference key instead, labelled as such.
  - Each example shows an **annotated hex dump**: every field in its own colour, with a legend, the offsets, the ASCII rendering, and a table with each field's decoded value.
  - Each can be copied as hex, as a C array, as an `xxd | nc` line, and as a `pnmtd udp send` command.
- **The packet builder.** Pick a type, an allowed suite, a counter, ACK, and a payload (Text or Hex). The server builds the datagram (`GET /api/v1/udp/protocol/build`), and the page shows it annotated as above. It only builds, never sends. Firmware developers can compare it with what their device sends.
- **The reference vectors** of §3.14, built live with the reference key `00 01 … 0F`. A unit test compares them with the spec, so the page, the spec and the codec agree.
- **Code snippets** with this node's values filled in: the device id, the suite constants, the server endpoint, and the key as a C array (User and Admin only).

### 6.6 Admin page: UDP nodes

A new page, **"UDP nodes"**, at `/admin/udp-nodes` in the Admin group of the drawer (`pi pi-wifi`, role Admin). It lists **every UDP node defined**, wherever its host is, including disabled ones.

- **The listener card** (at the top):
  - on or off, the endpoint, and a bind error if any;
  - packets per minute, accepted, and rejected by reason;
  - the queue depth and dropped datagrams;
  - the "rejected packets" warnings (§4.10).
- **Tab *Nodes*:** a server-paged `ListTable`, with search over name, host, device id and **notes**, filters (state, key family, enabled, firmware version) and sorting.

  | Column | Content |
  |---|---|
  | Name | A link to the sensor page |
  | Host | A link to the host |
  | Device id | Decimal and as header bytes (`7 · 07`, `300 · 01 2C`) |
  | State | The status badge, as everywhere |
  | Enabled | |
  | Key | The family, plus "generated" or "manual" with the date (hover: by whom) |
  | Suites | The allowed suites, compact (`H32 H64 C64`), with weak and legacy suites marked |
  | Session / ACK | `JOIN` or `–`, and `on request`, `always` or `never` |
  | Last seen | Relative, with the last address and suite on hover |
  | FCNT | |
  | Open commands | |
  | Firmware | The running version. An open update shows as a progress bar |
  | 24 h | Packets, loss, rejected |
  | Notes | The first line, with the full text on hover |

  - **Row actions** (`MoreMenu`): open, edit, set key manually, regenerate key or token, reset counter, reset session, enable or disable, update firmware. The same dialogs and confirmations as on the sensor page.
  - **Selecting several rows** offers *Update firmware…* for all of them. The dialog suggests trying one node first.
  - **"Add UDP node"** opens the sensor form with the type preset. The host is chosen there.
- **Tab *Firmware*:**
  - **Upload** a `.bin` by drag and drop or a file picker, with a name, a version and notes. The page then shows the size, the SHA-256 and the magic hint (§4.11).
  - **The list of images:** name, version, size, SHA-256 (short, copyable), uploaded by and when, and how many nodes run the version.
  - **Actions:** download, edit, delete (refused while in use), and *Update nodes…*.
- **Tab *Updates*:** every OTA job, newest first.
  - **Columns:** node, image and version, state, a progress bar (bytes of size), the rate and ETA while downloading, re-fetches, and when it started and finished.
  - **Actions:** cancel.
  - **Expanding a job** shows its command-log timeline.
- **Tab *Command log*:** `udp_command_log` for all nodes, newest first. Columns: time, node (deleted nodes by their snapshot name and device id), kind, `CMD_ID`, attempt, direction, and a preview as hex and ASCII. Filters: node or device id, kind, and a time range. Expanding a row shows the datagram and payload as on the Commands tab.
- **Tab *Unknown senders*:** the in-memory ring (§4.7), with *Create node with id 42*, which opens the sensor form pre-filled.
- **A link to the *Protocol* reference** (§6.5), without a node.

Users and Viewers reach their nodes through hosts, sensors and the dashboard as before. The nodes are sensors, so the page only adds the cross-node admin view.

### 6.7 Other places

- **System health** gets a UDP listener row: bound or not, the error, the queue depth and dropped datagrams. It links to the admin page.
- **The dashboard** needs no change: nodes are sensors. The "rejected packets" warning (§4.10) is not a sensor state, so it shows on the admin page and System health instead.
- **The Documentation page** gets a short *UDP nodes* section: the sensor type, codes 230 and 231, the endpoint, and a link to the Protocol reference (§6.5), where the full documentation is.
- **`src/format/bytes.ts`** has `hexDump(bytes, width = 16)`, `asciiPreview(bytes)` (0x20–0x7E as is, everything else `·`), `parseHexInput(text)` and `fromBase64`. They are unit-tested.
- **Roles:**
  - Viewer: sees nodes (as sensors), their events, last packets, rejections, commands and their communication, with payloads, but not keys or tokens.
  - User: also adds and edits nodes, sets keys manually or regenerates them, queues and cancels commands, and sees keys.
  - Admin: also has the admin page *UDP nodes*: all nodes, the listener, the command log of all nodes, and unknown senders. **Only an Admin uploads firmware and starts or cancels updates.**
  - Everyone who can see a node can see its Protocol subpage. It shows the real key in examples to User and Admin only.

---

## 7. Tooling

- **`pnmtd udp send`** is built into the app's CLI and uses the codec in `PNMTD.Lib`. It sends one packet and prints what went out and what came back, decoded and verified, as a hex dump.
  ```sh
  pnmtd udp send --server 127.0.0.1:5028 --id 7 --suite hmac32 --key 000102030405060708090a0b0c0d0e0f \
                 --type data --text "21.5C" --ack --counter-file /tmp/node7.fcnt
  ```
  `--suite` takes `none`, `token32`, `hmac32`, `hmac64`, `cmac32`, `cmac64`, `ccm32`, `ccm64`, `sha1-32`, `sha1-64`, `md5-32` and `md5-64`.
- **`pnmtd udp node`** is a simulated device:
  - a heartbeat every `--interval`, and BOOT at start;
  - optional JOIN;
  - it answers every COMMAND with a CMD_RESULT that echoes it (`--result ok|fail|ignore`);
  - it persists its counter in a file;
  - **OTA:** it accepts offers, downloads the image (`--ota-dir`), verifies the SHA-256, and reports VERIFIED. After a simulated restart it reports BOOT and APPLIED with the new version. `--ota-fail hash|decline|rollback` and `--ota-drop 5%` exercise the failure paths and lost chunks.

  The integration and Playwright tests use it, and so does `SEED=1 ./start.sh`, so the UI has live nodes to show.
- **`pnmtd udp reset-counter <device id>`** is for the server (Admin, logged).
- **`examples/udp/pnmt_udp.py`** is a reference client using only the standard library (suites 0–3), about 150 lines, for Raspberry Pis and scripts.
- *Later:* `examples/udp/c/` (C99, crypto through callbacks, with an ESP-IDF/mbedTLS example, since mbedTLS has HMAC, CMAC and CCM).

---

## 8. Phases

Effort is a rough estimate for one developer. Each phase ends green in `./run-tests.sh`, including the sensor contract.

| # | Phase | Scope | Done when | Effort |
|---|---|---|---|---|
| U1 | Protocol core | `PNMTD.Lib/Udp`: codec, the twelve suites, the counter window, the KDFs. Test vectors: §3.14, RFC 4231, RFC 2202, RFC 4493, RFC 3610. `docs/udp-protocol.md` (§3, normative). `pnmtd udp send` | The vectors and the codec's property tests are green. `udp send` builds vectors #1–#12 byte for byte | 2–3 d |
| U2 | Listener and heartbeats | **`sensors.notes` for every type**, in the API, form and page (§4.1). `UDP_NODE`, `udp_nodes`, `udp_last_packets`, the migrations. The listener, **on by default on 5028** (configurable), and the pipeline for HEARTBEAT, DATA, EVENT (alive only) and BOOT. ACK modes, the response suite, retransmissions, **events 230/231/232**, `HeartbeatCheckTask`, `SensorEventWriter`. The in-memory statistics and diagnostics, config, compose, Dockerfile, `start.sh` and `info.sh`. **Keys generated on creation, manual overwrite and regeneration, in plaintext** (§4.3). API: node settings, keys, last packets, rejections, status, unknown senders, the node list, protocol examples and the builder. UI: form with notes, key fields and response suite; integration panel; status strip; **Last packets with the packet inspector**; **the Protocol subpage** (the sections so far, examples and builder); **the admin page *UDP nodes*** (listener, *Nodes*, *Unknown senders*) | A simulated node is alive on the dashboard and turns 460 when stopped. Its heartbeats and data are events, and its last packets are visible with their bytes. A node added without a key gets one, and a manually entered key works at once. Notes work on every sensor type. The admin page lists every node. The Protocol subpage shows examples that `pnmtd udp send` reproduces byte for byte. The contract replay is green | 6–8 d |
| U3 | Commands | `udp_commands` and the **permanent `udp_command_log`**, the dispatcher, `M`, CMD_RESULT matching, attempts, expiry, cancellation. API, the **Commands tab with each command's communication**, the admin page's *Command log* tab, and the commands section of the Protocol subpage. `pnmtd udp node` answers commands | A command queued in the UI reaches the simulated node with its next heartbeat. Its result shows as hex and ASCII. Every step is in the command log, with its bytes, and stays there after the node is deleted. Retries, expiry, and the suite choice (as received, node setting, per command) are tested | 3–4 d |
| U4 | Encryption and sessions | AES-CCM/32 and /64 end to end (decryption into `payload`, encrypted responses, including a CCM response suite for nodes that report with another suite, and the counter enforcement that comes with it, §3.5). JOIN and JOIN_ACCEPT, pending and active sessions, reset-session. The UI shows the session state, and the Protocol subpage covers encryption and JOIN | Encrypted DATA and commands work with the simulator. A replayed JOIN does not disturb the session. Nonce uniqueness is tested | 2–3 d |
| U5 | Firmware updates (OTA) | The OTA types in the codec (§3.11). `udp_firmware` and `udp_ota_jobs`, the upload with its size limit, the dispatcher's offers, `UdpOtaServer` with `UdpFirmwareCache`, the raised rate limit, progress, abort, expiry and stall, `firmware_version`. API (§5). UI: the *Firmware* and *Updates* tabs of the admin page, the node's *Firmware* subpage, multi-node updates, the OTA section of the Protocol subpage. The simulator's OTA mode | A 4 MiB image uploaded in the UI reaches 3 simulated nodes, one of them with 5 % chunk loss, and all report APPLIED. Decline, hash failure, rollback, cancel and expiry end in the right state with their command-log rows. No chunk is ever served to a NONE or TOKEN32 uplink | 4–6 d |
| U6 | Hardening and release | Rate limits, the "rejected packets" warning, the flood test, fuzzing the codec, the Python reference client. Playwright: create a node, see its events and last packets, send a command and inspect its log, upload firmware and update a node, open the Protocol subpage. README, `deploy/README.md` (the port and how to change it, firewalls, backups as credentials, the upload limit per proxy), release notes on the new default-on port, the Documentation page, `pnmtd dev seed` | Targets of §9 met. The docs are complete | 2–3 d |
| U7 | *Later* | EVENT → channel child sensors (§4.2). DATA → events with value checks (a regex on the ASCII rendering, 275/475). Command presets ("reboot" = `05`). The C/ESP-IDF reference. A notification when a command fails. Live updates (SSE, as planned for the UI). ChaCha20-Poly1305 suites. A server time in ACK on request. OTA: staged rollouts (10 %, then the rest), delta images, scheduled update windows | – | – |

**Total for U1–U6: about 19–27 days.**

---

## 9. Testing

- **Codec (U1):**
  - a round trip for every type, suite and id length;
  - every header bit;
  - malformed input: short, long, `VER` 0/2/3, reserved bits, a tag that does not fit, a downlink type sent uplink;
  - property tests: parse(build(x)) = x, and parse never throws on random bytes;
  - a fuzzing run in CI (SharpFuzz or a random-bytes loop with 10⁶ inputs).
- **Crypto (U1):** RFC 4231 (HMAC-SHA256), RFC 2202 (HMAC-MD5 and HMAC-SHA1), RFC 4493 (AES-CMAC), RFC 3610 packet vectors with 4- and 8-byte tags (AES-CCM), the protocol vectors of §3.14, and the KDF vectors.
- **Counter (U1):**
  - reconstruction across the 16-bit wrap;
  - the window: reordering, duplicates, jumps above 16384;
  - TOKEN32/NONE resets that are not enforced.
- **Integration (U2–U4)** with a real UDP socket on loopback against `TestApp` with PostgreSQL and the listener on a random port:
  - the listener is on without any setting, on 5028, and on another port when `Udp__Port` is set;
  - a heartbeat creates 230, and a steady stream keeps two rows;
  - DATA creates 232 with the payload as text, or as `hex:` for binary;
  - BOOT creates 231;
  - stopping the node creates 460, but a long OTA download does not;
  - **only the last packet per type is kept:** a second heartbeat overwrites the first in `udp_last_packets`, and no other table grows with heartbeats or data;
  - ACK modes;
  - **the response suite:**
    - by default the received suite;
    - the node's fixed suite;
    - a command's own suite;
    - with a keyed response suite, a replayed TOKEN32 uplink gets no second response;
  - a retransmission gets the same bytes back and is processed once;
  - a bad tag, a replay, a disallowed suite and a disabled sensor get **no response**, write **nothing to the database**, and show up in the node's recent rejections;
  - the unknown-senders ring and the bound on the rejections list;
  - commands: delivery on the next uplink, FIFO, `M`, CMD_RESULT, re-sending, max attempts, expiry, cancellation;
  - **the command log:**
    - every transition writes its row with the right kind, bytes, suite, user and FCNT, and an unmatched result is recorded;
    - the rows and `udp_commands` survive the hourly task and deleting the node (with `sensor_id` null and the snapshots intact);
    - nothing in the app deletes them;
  - **keys:**
    - a node created without a key gets a random key and token marked `Generated`, stored in plaintext;
    - manual values are validated (length, hex, repeated bytes) and marked `Manual`;
    - a duplicate key gives a warning;
    - any change resets sessions, the counter window and the retransmission cache, and rejects packets under the old key at once;
    - the audit entry contains no key material;
  - JOIN: a session, a replayed JOIN, and a reset;
  - **notes on every sensor type:** stored and returned, 4000 characters at most, blank becomes null, and the admin page's search finds them for UDP nodes;
  - **OTA (U5)** with the simulator:
    - upload: the size limit, the SHA-256, the duplicate warning;
    - offer delivery after commands, and re-offer until answered;
    - a full download with chunk loss and retransmissions;
    - resuming after a simulated restart;
    - the end-of-image and abort answers;
    - decline, hash failure, rollback, cancel during the download, offer expiry, stall;
    - `firmware_version` from APPLIED and BOOT;
    - the command-log rows and job progress;
    - no row of any kind per chunk;
    - **no chunk for a NONE or TOKEN32 uplink**;
    - the raised rate limit applies only while the update is open;
    - two nodes downloading the same image share one cache entry;
    - deleting an image in use is refused;
  - a database outage means no response.
- **Isolation:** the sensor contract replay is unchanged, including with `sensors.notes` filled in. `SensorEventWriter`'s extraction is covered by the existing `SensorApi` tests and the replay.
- **API:**
  - the role matrix for the new endpoints: a Viewer sees no keys and cannot queue or set keys, and only an Admin reaches `/api/v1/udp/*`, firmware and updates (except the protocol endpoints, which are CanRead);
  - validation and paging;
  - **protocol examples:**
    - without a node, they equal the vectors of §3.14 byte for byte;
    - with a node, a CanWrite user's examples verify under the node's key, and a Viewer's under the reference key, never the node's.
- **UI:**
  - Vitest for `hexDump`, `asciiPreview` and `parseHexInput`, with edge cases: empty, 0x00–0x1F, 0x7F–0xFF, odd hex, whitespace;
  - component tests for the packet inspector, the Last packets, Commands and Firmware tabs, the annotated hex dump of the Protocol subpage, the notes field, and the upload dialog;
  - Playwright against the image with `pnmtd udp node` running.
- **Load (U6):**
  - 5,000 datagrams/s of junk and wrong tags while 200 simulated nodes report: no valid packet takes longer than 100 ms to its response;
  - the junk writes no database rows at all;
  - memory stays bounded.

---

## 10. Risks

| Risk | Mitigation |
|---|---|
| The device's receive window closes before the answer, or carrier-grade NAT drops the answer | Answer within 100 ms (§3.12). The device guide gives window sizes per network. Devices never depend on an ACK: missing heartbeats are what PNMT alerts on anyway |
| Devices lose their counter (no persistence), so every packet is a "replay" | JOIN (§3.8), block reservation (§3.7), the admin's counter reset, and a clear "Replay" status with a hint on the node's page |
| A leaked database dump exposes every device key (plaintext, decision 3) | Backup files are 0700/0600 already. `deploy/README.md` treats them as credentials. The keys can be regenerated per node (§4.3) |
| Debugging a device is harder without a packet history (decision 10) | The last packet of each type with its bytes, the in-memory recent rejections, the Protocol subpage's builder, and `pnmtd udp send`, which prints both directions (§6.3, §6.5, §7) |
| The default-on port surprises an operator after the upgrade | Release notes and `UPGRADING`. With no nodes defined it never answers. `Udp__Enabled=false` turns it off (§4.9) |
| A slow database delays answers past the receive window | One small transaction per packet, indexes, workers. The p99 target is checked in the load test. If it is not met, ACKs could be sent before the commit (a design change: an ACK would then no longer mean "stored") |
| Rootless Podman hides the source address | Responses are unaffected. It is documented (§4.9) |
| The sensor contract changes through the `SensorEventWriter` extraction | The contract replay gates the build. If in doubt, the UDP path gets its own copy and the extraction waits |
| MCUs without AES and too small for SHA-256 | TOKEN32 and NONE work. A ChaCha20-Poly1305 suite is reserved for later |
| An update bricks devices | The device keeps the old image and rolls back (§3.11). PNMT suggests one node first, and counts *applied* only on the device's report. Staged rollouts come later (U7) |
| An update is slow or expensive on NB-IoT and metered SIMs | The volume and duration are shown before starting (§3.13). The chunk size and expiry are per job. Resuming means nothing is downloaded twice |
| Large images in PostgreSQL bloat the database and its backups | `MaxImageBytes` (16 MiB), and deleting unused images. The images are typically 0.2–2 MiB |
| The Protocol subpage drifts from the implementation | Its examples come from the codec, and a test pins them to the vectors of §3.14 (§6.5) |

---

## 11. Decisions

The review of revision 2 decided every open question (2026-11-12). The sections above are written to these decisions.

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Which UDP port? | **5028/udp by default, configurable** with `PNMTD_UDP_PORT` (compose and listener together) or `Udp__Port` | §4.8, §4.9 |
| 2 | Listener on or off by default? | **On.** `Udp__Enabled=false` turns it off | §4.4, §4.9 |
| 3 | Device keys encrypted or in plaintext? | **Plaintext**, like the sensor tokens. No key ring and no `export-keys`. Backups are treated as credentials | §4.3, §4.10 |
| 4 | EVENT with channels? | **Defined on the wire now, implemented in U7** | §3.4, §4.2 |
| 5 | Do Viewers see payloads? | **Yes**, but not keys or tokens | §6.7 |
| 6 | In which suite do responses and commands go out? | **The suite the uplink came with, by default.** A different suite can be configured per node (`response_suite`) and per command. This replaces both the "no commands over weak suites" rule and *encrypted only* | §3.5, §3.10 |
| 7 | Log every heartbeat? | **No.** Heartbeats (and data) are **sensor events like those of the other sensors**, and only the last packet of each type is kept | §4.2, §4.7 |
| 8 | Notes on UDP nodes only, or on every sensor? | **On every sensor** (`sensors.notes`) | §4.1 |
| 9 | Who may update firmware? | **Admin only**, for uploading and for starting or cancelling updates | §4.11, §6.7 |
| 10 | What is logged with its bytes? | **Only the packets that users send (commands) and the answers to them**, plus the firmware milestones. There is no general packet log, and OTA chunks are summarized per job | §4.3 `udp_command_log`, §4.7 |

**Follow-ups from these decisions** (no question left, noted for the implementation):

- **Decision 6:** a keyed response suite makes the server enforce the counter for every uplink of the node, even TOKEN32 ones, so a keyed response is never repeated for a replayed uplink (§3.5).
- **Decision 10:** DATA payloads are kept in the events (232) and as the last DATA packet, not in a log of their own. If a history of raw binary data is needed later, that would be a U7 item.

---

## 12. Implementation notes (where the code differs from the sections above)

- **One migration**, `AddSensorNotesAndUdpNodes`, holds the notes column and every UDP table, the OTA tables included: all phases landed together. It also sets the firmware content to `STORAGE EXTERNAL`.
- **AES-CMAC and AES-CCM are built on AES-ECB** in `PNMTD.Lib/Udp/AesModes.cs`.
  - .NET has no CMAC, and its `AesCcm` is not supported on macOS.
  - Both pass the RFC 4493 and RFC 3610 vectors, and CCM agrees with .NET's `AesCcm` on Linux (a test that runs where the platform supports it; 72 cases checked in the SDK container).
- **The packet builder is `GET /api/v1/udp/protocol/build`** (the payload as Base64 in the query), not a POST. It writes nothing, and the endpoint audit requires CanWrite for every non-GET.
- **`Udp:JoinIntervalSeconds`** (default 10) makes the one-JOIN-per-10-seconds rule configurable. An identical JOIN within the retransmission window is a retransmission: it gets the same JOIN_ACCEPT and creates no new session.
- **The sensor API's event code is not refactored.** `PNMTD/Udp/SensorEventWriter.cs` repeats its rules (advisory lock, dedupe, at most 2000 events) for UDP nodes, as §10 allowed. The frozen contract therefore cannot change through UDP code.
- **`GET /sensors/{id}/udp/rejections` is CanRead**, following decision 5 (Viewers see payloads).
- **The firmware upload has no Consumes constraint.** `ApiBehaviorOptions.SuppressConsumesConstraintForFormFileParameters` is on, because routing checked the constraint before authorization and answered other roles with 404 instead of 403. An OpenAPI transformer still documents the body as `multipart/form-data`.
- **Outside changes to the counter** (`pnmtd udp reset-counter` runs in another process) are noticed inside the packet's transaction: a stored counter that differs from the one in memory drops the packet and reloads the node.
- **An OTA GET can promote a pending session**, when a device resumes a download right after a new JOIN. A small transaction promotes it, and the chunk goes out under the new session key.
- **`pnmtd dev seed`** turns four of its fan sensors into UDP nodes, so its pinned shape (50 hosts, 300 sensors, 6 failing) stays. Their heartbeats are daily, so they stay OK while the stack runs.
- **The stack tests** (`tools/e2e.sh`, `deploy-test.sh`, `upgrade-test.sh`) publish their own UDP ports, so they do not clash with a running dev stack on 5028/udp.
- **Random device ids** (a later request): a new node gets a random free id within `Udp:DeviceIdBytes` (default 2), the next ones if it is taken, at most 100, instead of the lowest free id. Ids no longer reveal how many nodes there are, at the price of usually 2 id bytes instead of 1 per packet; `Udp__DeviceIdBytes=1` keeps them at one byte for small fleets.
- **Editing an existing node** goes through `PUT /api/v1/sensors/{id}` with `udp`, as §5 has it, from the node page's *Edit settings…* dialog (`UdpSettingsDialog.vue`).
- **The protocol page downloads as Markdown** (`protocolMarkdown.ts`): the reference sections converted from the rendered page, the examples, vectors and code from the same data.
- **The load test** (`PNMTD.Tests/Performance/UdpFloodTests.cs`) floods about 5,000 junk and forged datagrams per second while 20 nodes report. The checks:
  - every valid packet is answered, with p99 below 250 ms (about 60 ms on a laptop);
  - the flood writes no rows.

  It found one bug: the unknown senders' eviction sorted a concurrent dictionary while other workers changed it. It now sorts a snapshot.

## Appendix: configuration and file changes

| Where | Change |
|---|---|
| `appsettings.json` | The `Udp` section (§4.8): `Enabled: true`, `Port: 5028` |
| `deploy/docker-compose.yml` | The `${PNMTD_UDP_PORT:-5028}:${PNMTD_UDP_PORT:-5028}/udp` port line with `PNMTD_UDP_BIND_ADDRESS`, and `Udp__Port` from `PNMTD_UDP_PORT` (§4.9) |
| `deploy/info.sh`, `deploy/README.md`, `deploy/UPGRADING…` | Show and document the listener, the port and how to change it, firewalls, and backups as credentials. Note the new default-on port in the upgrade notes |
| `Dockerfile` | `EXPOSE 5028/udp` |
| `start.sh` | `-p ${UDP_PORT:-5028}:5028/udp` |
| `PNMTD/Models/Db/SensorEntity.cs` | `Notes` (every sensor type, §4.1) |
| `PNMTD/Services/Domain/SensorService.cs`, `Api/V1/Contracts/Sensors.cs` | `notes` in `SensorRequest` and `SensorDetail`, with the hosts' validation |
| `PNMTD.Lib/Models/Enum/SensorType.cs` | `UDP_NODE = 70` |
| `PNMTD.Lib/Models/PNMTStatusCodes.cs` | `UDP_ALIVE = 230`, `UDP_BOOT = 231`, `UDP_DATA = 232` |
| `PNMTD/Program.cs` | `AddHostedService<UdpListener>()`, on unless `Udp:Enabled=false`, plus `UdpMaintenanceTask` |
| `PNMTD/Data/PnmtdDbContext.cs` | `UdpNodes`, `UdpLastPackets`, `UdpCommands`, `UdpCommandLog`, `UdpFirmware`, `UdpOtaJobs` (commands, the command log and jobs with `ON DELETE SET NULL`), the indexes, the migration `AddSensorNotesAndUdpNodes` |
| `PNMTD/Program.cs`, upload endpoint | The request size limit for the firmware upload only (§4.9) |
| `PNMTD/Tasks/HeartbeatCheckTask.cs` | Includes `UDP_NODE` |
| `PNMTD/SensorApi/SensorApiController.cs` | Uses the extracted `SensorEventWriter`, with no observable change |
| `PNMTD.WebUI/src/layouts/navigation.ts` | "UDP nodes" in the Admin group (`/admin/udp-nodes`, role Admin) |
| `PNMTD.WebUI/src/views/sensors/SensorFormView.vue`, `SensorView.vue` | Notes for every type |
| `PNMTD.WebUI/src/views/admin/UdpNodesView.vue` | The admin page with its tabs (§6.6) |
| `PNMTD.WebUI/src/views/sensors/udp/` | `UdpNodePanel`, `LastPacketsTab`, `PacketInspector`, `CommandsTab`, `FirmwareTab`, `protocol/` (§6.2–§6.5) |
| `PNMTD.WebUI/src/router/index.ts` | `/sensors/:id/{last-packets,commands,firmware,protocol}`, `/admin/udp-nodes`, `/admin/udp-nodes/protocol` |
| `README.md` | A feature line, and "Reporting to PNMT" gets a UDP paragraph |
