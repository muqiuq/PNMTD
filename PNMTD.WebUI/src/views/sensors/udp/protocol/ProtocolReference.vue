<script setup lang="ts">
import { computed } from 'vue'
import type { S } from '@/api/resources'
import { familyLabel, idBytes, idLength } from '@/format/udp'

/**
 * The protocol reference for device developers (docs/udp-iot-plan.md §3, normative there): the
 * exchange model, the packet layout, types, suites (this node's allowed ones highlighted), tags and
 * nonces, the counter, JOIN, ACKs, commands, OTA and the device checklist. Text only; the examples
 * next to it come from the server's codec.
 */
const props = defineProps<{ examples: S['UdpProtocolExamples'] }>()

const allowed = computed(() => new Set(props.examples.allowedSuites))
const id = computed(() => props.examples.deviceId)

// What each type carries (§3.4); the server lists the types with their codes and directions.
const payloads: Record<string, string> = {
  Heartbeat: 'Empty, or a few status bytes. "I am alive": event 230.',
  Data: 'Opaque bytes in whatever format the device implements: event 232 with the payload as its message.',
  Event:
    'CHANNEL (1), CODE (2), MESSAGE (UTF-8, the rest): a PNMT event in binary form; alive (230) until channels come.',
  CmdResult: 'CMD_ID (1), STATUS (1), RESULT (the rest): the answer to a COMMAND.',
  Boot: 'REASON (1, optional), INFO (UTF-8, optional, such as the firmware version): a restart, event 231.',
  Join: 'DEV_NONCE (4): starts a session; only the 64-bit MAC suites.',
  Ota: 'UPDATE_ID (1), OP (1), ARG (the rest): fetch a chunk, decline, or report the outcome.',
  Ack: 'Empty: acknowledges the uplink whose counter it echoes.',
  Command: 'CMD_ID (1), COMMAND (the rest, opaque). Acknowledges the uplink too; M = more waiting.',
  JoinAccept: 'SERVER_NONCE (4): answers a JOIN.',
  OtaOffer: 'UPDATE_ID (1), SIZE (4), CHUNK (2), SHA256 (32), VERSION (UTF-8, the rest).',
  OtaChunk: 'UPDATE_ID (1), OFFSET (4), DATA (the rest). OFFSET FFFFFFFF with no data: abort.',
}
const purposes: Record<string, string> = {
  None: 'Labs and closed networks; must be allowed explicitly.',
  Token32: 'Shell scripts and MCUs without crypto. As safe as an http:// sensor URL.',
  HmacSha256_32: 'Signed heartbeats from Linux, scripts and ESP32. The default.',
  HmacSha256_64: 'A stronger signature, and JOIN for the HMAC-SHA256 family.',
  AesCmac32: "MCUs with an AES engine (nRF, STM32, ESP32). LoRaWAN's MIC.",
  AesCmac64: 'A stronger signature, and JOIN for the AES family.',
  AesCcm32: 'Encrypted packets, smallest.',
  AesCcm64: 'Encrypted packets; recommended for encryption.',
  HmacMd5_32: 'Devices and libraries that only have MD5. Legacy.',
  HmacMd5_64: 'JOIN for the HMAC-MD5 family. Legacy.',
  HmacSha1_32: 'Devices and libraries that only have SHA-1. Legacy.',
  HmacSha1_64: 'JOIN for the HMAC-SHA1 family. Legacy.',
}
</script>

<template>
  <div class="reference">
    <section id="exchange" class="panel">
      <h2 class="panel-header">1. Exchange model</h2>
      <div class="panel-body prose">
        <p>
          <strong>Only the device starts an exchange.</strong> Each accepted uplink opens exactly one response slot,
          used for an ACK or a COMMAND. PNMTD answers to the uplink's source address and port, within 100 ms, and never
          sends anything unsolicited: it does not know whether the device listens, and its NAT mapping may be gone. A
          command therefore waits for the node's next packet.
        </p>
        <pre class="diagram">
node                                              server
 │  uplink (HEARTBEAT, DATA, EVENT, …) ─────────────►│  validate, store, decide
 │◄─────────────── at most one downlink (ACK|COMMAND)│  within 100 ms
 │  (the receive window closes; the radio sleeps)    │
 ⋮  10 minutes later                                 ⋮
 │  uplink ─────────────────────────────────────────►│  a command was queued meanwhile
 │◄──────────────────────────────────── COMMAND      │
 │  CMD_RESULT ─────────────────────────────────────►│  the result is stored
 │◄──────────────────────────────────── ACK          │  (if asked for)</pre>
        <p>
          <strong>No answer to anything invalid:</strong> a packet that fails any check gets no response and causes no
          database write. There are no error responses, so there is nothing to probe ids or keys with, and nothing to
          reflect. A datagram is 5 to {{ examples.maxDatagram }} bytes; larger ones are dropped.
        </p>
      </div>
    </section>

    <section id="layout" class="panel">
      <h2 class="panel-header">2. Packet layout</h2>
      <div class="panel-body prose">
        <p>All multi-byte fields are big-endian.</p>
        <pre class="diagram">
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
└───────────────────────────────────────────────────────────────┘</pre>
        <div class="table-wrap">
          <table class="doc">
            <thead>
              <tr>
                <th>Field</th>
                <th>Bits</th>
                <th>Meaning</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td><code>VER</code></td>
                <td>2</td>
                <td>
                  <code>01</code> = v1. <code>00</code> is invalid (it rejects zero-filled junk), 10 and 11 are
                  reserved.
                </td>
              </tr>
              <tr>
                <td><code>TYPE</code></td>
                <td>4</td>
                <td>The packet type. The top bit is the direction: 0x0–0x7 uplink, 0x8–0xF downlink.</td>
              </tr>
              <tr>
                <td><code>IDL</code></td>
                <td>2</td>
                <td>
                  The device id's length minus one: 00 = 1 byte, 01 = 2, 10 = 3, 11 = 4. This node, id {{ id }}:
                  <code>IDL {{ idLength(id) - 1 }}</code
                  >, id bytes <code>{{ idBytes(id) }}</code>. The server answers with the length the device used.
                </td>
              </tr>
              <tr>
                <td><code>A</code></td>
                <td>1</td>
                <td>Uplink: an acknowledgement is requested. Downlink: 0.</td>
              </tr>
              <tr>
                <td><code>M</code></td>
                <td>1</td>
                <td>Downlink: more commands (or an update offer) are waiting. Uplink: 0.</td>
              </tr>
              <tr>
                <td><code>RSV</code></td>
                <td>2</td>
                <td>Reserved, must be 0; a packet with them set is dropped.</td>
              </tr>
              <tr>
                <td><code>SUITE</code></td>
                <td>4</td>
                <td>The security suite (section 4).</td>
              </tr>
              <tr>
                <td><code>DEVICE ID</code></td>
                <td>8–32</td>
                <td>Assigned by PNMT, from 1; 0 is invalid.</td>
              </tr>
              <tr>
                <td><code>COUNTER</code></td>
                <td>16</td>
                <td>
                  Uplink: the low 16 bits of the frame counter. Downlink: the counter of the uplink it answers, echoed.
                </td>
              </tr>
              <tr>
                <td><code>PAYLOAD</code></td>
                <td>–</td>
                <td>What remains after the header and the tag: there is no length field, UDP carries it.</td>
              </tr>
              <tr>
                <td><code>TAG</code></td>
                <td>–</td>
                <td>The token or the truncated MAC.</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section id="types" class="panel">
      <h2 class="panel-header">3. Packet types</h2>
      <div class="panel-body prose">
        <div class="table-wrap">
          <table class="doc">
            <thead>
              <tr>
                <th>TYPE</th>
                <th>Name</th>
                <th>Dir</th>
                <th>Payload</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="t in examples.types" :key="t.type">
                <td class="mono">0x{{ t.code.toString(16).toUpperCase() }}</td>
                <td>
                  <code>{{ t.name }}</code>
                </td>
                <td :title="t.downlink ? 'downlink' : 'uplink'">{{ t.downlink ? '↓' : '↑' }}</td>
                <td>{{ payloads[t.type] ?? '' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p>
          EVENT needs at least 3 payload bytes, CMD_RESULT 2, JOIN exactly 4, OTA 2 (a GET exactly 6 or 8); shorter is
          malformed. Every type may use every suite the node allows: "encrypted DATA" is DATA with AES-CCM.
        </p>
        <p>
          <strong>BOOT REASON:</strong> 0 unknown, 1 power on, 2 watchdog, 3 brown-out, 4 software reset or update, 5
          reset by a command, 128–255 device-specific. <strong>CMD_RESULT STATUS:</strong> 0 OK, 1 unknown command, 2
          invalid argument, 3 failed, 4 busy, 128–255 device-specific.
        </p>
      </div>
    </section>

    <section id="suites" class="panel">
      <h2 class="panel-header">4. Security suites</h2>
      <div class="panel-body prose">
        <p>
          The suite is chosen <strong>per packet</strong>, from the suites the node allows. A node has one 16-byte key,
          used with one family only ({{ familyLabel(examples.keyFamily) }} here), and a separate 4-byte token for
          TOKEN32.
          <span data-md="The suites this node allows are marked **allowed**."
            ><span class="allowed-mark">Highlighted</span>: allowed for this node.</span
          >
        </p>
        <div class="table-wrap">
          <table class="doc suites">
            <thead>
              <tr>
                <th>SUITE</th>
                <th>Name</th>
                <th>Tag</th>
                <th>Payload</th>
                <th>Replay protection</th>
                <th>Intended for</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="s in examples.suites" :key="s.suite" :class="allowed.has(s.suite) ? 'allowed' : 'off'">
                <td class="mono">{{ s.code }}</td>
                <td>
                  <code>{{ s.name }}</code>
                  <span v-if="s.legacy" class="mark" data-md=" (legacy)">legacy</span>
                  <span v-if="!s.keyed" class="mark weak" data-md=" (weak)">weak</span>
                  <span v-if="allowed.has(s.suite)" hidden data-md=" · **allowed**" />
                </td>
                <td>{{ s.tagLength }} B</td>
                <td>{{ s.encrypted ? 'encrypted' : 'clear' }}</td>
                <td>{{ s.keyed ? 'yes' : 'no' }}</td>
                <td>{{ purposes[s.suite] ?? '' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <ul>
          <li>
            <strong>The response suite:</strong> a downlink uses the suite its uplink came with, unless the node (or a
            single command) is set to another suite. JOIN_ACCEPT always uses the JOIN's suite; OTA downlinks use the
            response suite only if it is keyed.
          </li>
          <li>
            <strong>MD5 and SHA-1 only as HMAC:</strong> never <code>MD5(key ‖ packet)</code>, which could be extended
            without the key. Collisions do not break HMAC; at 32 or 64 bits the tag length bounds the strength.
          </li>
          <li>
            <strong>32-bit tags:</strong> a forgery succeeds with 2<sup>−32</sup> per attempt, and every attempt is a
            packet; rate limits and the rejected-packets warning make brute force impractical. Use 64-bit suites when
            commands matter.
          </li>
        </ul>
      </div>
    </section>

    <section id="tags" class="panel">
      <h2 class="panel-header">5. Tags, encryption and nonces</h2>
      <div class="panel-body prose">
        <p>
          <code>P</code> is every byte before the tag, <code>H</code> the header (bytes 0–1, id and counter),
          <code>FCNT32</code> the full 32-bit frame counter, big-endian (for a downlink: the uplink's), <code>K</code>
          the key or, after a JOIN, the session key.
        </p>
        <div class="table-wrap">
          <table class="doc">
            <thead>
              <tr>
                <th>Suite</th>
                <th>Tag</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>NONE</td>
                <td>No tag</td>
              </tr>
              <tr>
                <td>TOKEN32</td>
                <td><code>TAG = TOKEN</code></td>
              </tr>
              <tr>
                <td>HMAC-SHA256/n, HMAC-SHA1/n, HMAC-MD5/n</td>
                <td><code>TAG = first n/8 bytes of HMAC-hash(K, FCNT32 ‖ P)</code></td>
              </tr>
              <tr>
                <td>AES-CMAC/n</td>
                <td><code>TAG = first n/8 bytes of AES-CMAC(K, FCNT32 ‖ P)</code></td>
              </tr>
              <tr>
                <td>AES-CCM/n</td>
                <td>
                  <code>(C, TAG) = AES-128-CCM(K, nonce N, aad H, PAYLOAD, tag n/8)</code>; the packet is
                  <code>H ‖ C ‖ TAG</code>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p>
          <strong>The CCM nonce</strong> (13 bytes):
          <code>DIR(1) ‖ DEVICE_ID(4, padded) ‖ FCNT32(4) ‖ 00 00 00 00</code>, DIR <code>00</code> up and
          <code>01</code> down. It is never reused: an uplink's FCNT32 is unique per key, each uplink gets at most one
          downlink, and a retransmission gets the stored response bytes, not a new encryption. The tag covers the
          header, so a changed type, flag, suite, id or counter fails as well.
        </p>
      </div>
    </section>

    <section id="counter" class="panel">
      <h2 class="panel-header">6. Counter, replays, JOIN, ACKs</h2>
      <div class="panel-body prose">
        <h3>Frame counter</h3>
        <ul>
          <li>
            FCNT is 32 bits per node (or session), starts at 1, and increases for every <em>new</em> uplink; only the
            low 16 bits travel. The server rebuilds the full value as the one closest to the highest accepted one.
          </li>
          <li>
            Accepted: a counter up to 16,384 above the highest, or one of the 63 below it not seen yet (a 64-packet
            window, as in IPsec). Everything else is a replay.
          </li>
          <li>
            For NONE and TOKEN32 the counter only detects losses and restarts, unless the node has a keyed response
            suite. Replay protection can be switched off per node, for bench testing.
          </li>
          <li>
            <strong>A device with keyed suites must never reuse a counter.</strong> It persists FCNT (store
            <code>FCNT + 16</code> at start and every 16 packets, and continue from there) or uses JOIN at every start.
            An admin can reset the counter of a node that lost it.
          </li>
        </ul>
        <h3>JOIN (session mode "JOIN required")</h3>
        <pre class="diagram">
node                                                     server
 │ JOIN (suite 3/5/9/11, FCNT 0, DEV_NONCE = r1, root K) ─►│ verify, at most 1 JOIN per node per 10 s
 │◄──── JOIN_ACCEPT (SERVER_NONCE = r2, MAC with K)        │ SK = KDF(K, r1, r2), stored as pending
 │ HEARTBEAT (FCNT 1, tag with SK) ───────────────────────►│ pending verifies → active; HIGH = 1</pre>
        <ul>
          <li>
            JOIN_ACCEPT's MAC takes DEV_NONCE in the place of FCNT32, which binds it to that JOIN.
            <code>SK = first 16 bytes of HMAC-hash(K, "PNMT-UDP1 SK" ‖ DEV_NONCE ‖ SERVER_NONCE)</code>, or for AES
            <code>AES-128-ECB(K, 01 ‖ DEV_NONCE ‖ SERVER_NONCE ‖ 00×7)</code>.
          </li>
          <li>
            The server keeps the active session and at most one pending one; a replayed JOIN only creates a pending
            session nobody can use. Without an answer, retry with a new DEV_NONCE after 10 s, 30 s, 60 s.
          </li>
        </ul>
        <h3>Acknowledgements and retransmission</h3>
        <ul>
          <li>ACK mode per node: never, on request (A = 1, the default), or always. A COMMAND replaces the ACK.</li>
          <li>
            With A = 1 and no valid answer within its window (about 2 s on Wi-Fi or LTE-M, up to 10 s on NB-IoT), a
            device may send the identical datagram again, at most twice. Within 5 minutes the server answers it with the
            stored response and does not process it twice; the same counter with other bytes is a replay.
          </li>
        </ul>
      </div>
    </section>

    <section id="commands" class="panel">
      <h2 class="panel-header">7. Commands</h2>
      <div class="panel-body prose">
        <ul>
          <li>
            Every accepted uplink except JOIN and an OTA fetch is a delivery slot: one command per response, in the
            order they were queued. <code>M = 1</code>: another is waiting; the device may send a HEARTBEAT at once, and
            its CMD_RESULT opens the next slot too.
          </li>
          <li>
            <code>CMD_ID</code> is 8 bits per node and increases; at most 32 commands are open, so it is never
            ambiguous. <strong>The device executes a CMD_ID at most once</strong> and answers a repeat with its stored
            result.
          </li>
          <li>
            "Until a result arrives" re-sends the same bytes on later uplinks, at most the command's attempts; "fire and
            forget" sends once. A command not delivered by its expiry expires.
          </li>
          <li>
            A command goes out in the response suite: the uplink's by default, the node's or the command's own if set.
            At most 480 bytes, so the datagram stays at or below 508; under 64 is better for NB-IoT. The bytes are
            opaque to PNMT.
          </li>
        </ul>
      </div>
    </section>

    <section id="ota" class="panel">
      <h2 class="panel-header">8. Firmware updates (OTA)</h2>
      <div class="panel-body prose">
        <pre class="diagram">
node                                                        server
 │ HEARTBEAT ────────────────────────────────────────────────►│ an update job waits for this node
 │◄──── OTA_OFFER (UPDATE_ID 3, SIZE 1,048,576, CHUNK 448, SHA256, "1.5.0")
 │ OTA GET (3, OFFSET 0) ────────────────────────────────────►│ state: downloading
 │◄────────────────────────────── OTA_CHUNK (3, 0, 448 bytes) │
 ⋮   2,341 chunks in all; a lost one is simply fetched again   ⋮
 │ OTA VERIFIED (3) ─────────────────────────────────────────►│ the SHA-256 matched on the device
 │ (installs, restarts)                                        │
 │ BOOT (REASON 4, "1.5.0") ─────────────────────────────────►│
 │ OTA APPLIED (3, "1.5.0") ─────────────────────────────────►│ state: applied</pre>
        <div class="table-wrap">
          <table class="doc">
            <thead>
              <tr>
                <th>OP</th>
                <th>Name</th>
                <th>ARG</th>
                <th>Meaning</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td class="mono">0x01</td>
                <td><code>GET</code></td>
                <td>OFFSET (4), LENGTH (2, optional)</td>
                <td>Fetch bytes at OFFSET; answered with OTA_CHUNK. The first GET accepts the offer.</td>
              </tr>
              <tr>
                <td class="mono">0x02</td>
                <td><code>DECLINE</code></td>
                <td>REASON (1)</td>
                <td>1 no space, 2 battery low, 3 busy, 4 already installed, 5 wrong target.</td>
              </tr>
              <tr>
                <td class="mono">0x03</td>
                <td><code>VERIFIED</code></td>
                <td>–</td>
                <td>All bytes are in and the SHA-256 matches; the device installs next.</td>
              </tr>
              <tr>
                <td class="mono">0x04</td>
                <td><code>FAILED</code></td>
                <td>REASON (1)</td>
                <td>1 hash mismatch, 2 write error, 3 image rejected, 4 install failed, 5 rolled back.</td>
              </tr>
              <tr>
                <td class="mono">0x05</td>
                <td><code>APPLIED</code></td>
                <td>VERSION (UTF-8)</td>
                <td>The new image runs after the restart and passed its self-test.</td>
              </tr>
            </tbody>
          </table>
        </div>
        <ul>
          <li>
            OTA uplinks must use a keyed suite: a chunk is up to 25 times larger than its GET. Every chunk is
            authenticated with the node's key; over AES-CCM it is encrypted.
          </li>
          <li>
            The device fetches in any order, resumes after a restart at its first missing offset, and
            <strong>must verify the SHA-256 of the whole image</strong> before installing (and should let its bootloader
            verify the signature: PNMT checks neither content nor signature).
          </li>
          <li>
            A GET at or past SIZE gets an empty chunk; a GET for an update that is no longer open gets OFFSET FFFFFFFF:
            abort, discard the partial image.
          </li>
        </ul>
      </div>
    </section>

    <section id="checklist" class="panel">
      <h2 class="panel-header">9. Device checklist</h2>
      <div class="panel-body prose">
        <ul class="checklist" data-md-task>
          <li>Persist FCNT (block reservation), or JOIN at every start.</li>
          <li>Never reuse a counter with a keyed suite, except for an identical retransmission.</li>
          <li>
            Listen after an uplink when an ACK was requested or commands are expected: a device that never listens gets
            no commands.
          </li>
          <li>
            Accept a downlink only if its type is a downlink type, its suite is the uplink's or the configured response
            suite, its echoed counter is the uplink's, and its tag verifies. Ignore everything else.
          </li>
          <li>Execute each CMD_ID once, and answer a repeat with the stored result.</li>
          <li>Send BOOT after a restart, with a reason (231 in PNMT) and the firmware version as INFO.</li>
          <li>Keep datagrams at or below 508 bytes, preferably below 100.</li>
          <li>Use the 64-bit suites if commands can do harm.</li>
          <li>
            OTA: verify the SHA-256 before installing, keep the old image until the new one passed its self-test, report
            VERIFIED, FAILED or APPLIED, resume at the first missing offset, and reserve larger counter blocks (256).
          </li>
        </ul>
      </div>
    </section>
  </div>
</template>

<style scoped>
.reference {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

h2.panel-header {
  font-size: 1rem;
  margin: 0;
}

.prose p,
.prose ul {
  margin: 0 0 var(--pnmt-space-2);
}

.prose ul {
  padding-left: 1.25rem;
}

.prose li + li {
  margin-top: var(--pnmt-space-1);
}

.prose h3 {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-1);
}

.prose h3:first-child {
  margin-top: 0;
}

.diagram {
  font-family: var(--pnmt-mono);
  font-size: 0.786rem;
  line-height: 1.35;
  overflow-x: auto;
  padding: var(--pnmt-space-2);
  margin: 0 0 var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.table-wrap {
  overflow-x: auto;
  margin-bottom: var(--pnmt-space-2);
}

.doc {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.929rem;
}

.doc th {
  text-align: left;
  font-weight: 500;
  color: var(--pnmt-muted);
  border-bottom: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  white-space: nowrap;
}

.doc td {
  border-bottom: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  vertical-align: top;
}

.suites tr.off td {
  color: var(--pnmt-muted);
}

.suites tr.allowed td,
.allowed-mark {
  background: color-mix(in srgb, var(--p-primary-color) 10%, transparent);
}

.allowed-mark {
  padding: 0 var(--pnmt-space-1);
  border-radius: var(--p-border-radius-sm);
}

.mark {
  margin-left: var(--pnmt-space-1);
  font-size: 0.75rem;
  font-weight: 600;
  padding: 0 0.35rem;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
  color: var(--pnmt-muted);
}

.mark.weak {
  background: var(--pnmt-warn-soft);
  color: var(--pnmt-warn);
}

.checklist li::marker {
  content: '☐  ';
}

.mono {
  font-family: var(--pnmt-mono);
}
</style>
