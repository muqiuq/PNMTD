import type { S } from '@/api/resources'
import { parseHexInput, toCArray, toHex } from '@/format/bytes'
import { endpointParts, idBytes, idLength, isWeak, suiteInfo, type SecuritySuite } from '@/format/udp'

/**
 * Code to start from (docs/udp-iot-plan.md §3.14, §6.5): Python with the standard library's HMAC,
 * C (pseudo-code with the platform's crypto), and a shell line, filled in with the node's id,
 * suite, endpoint and key (the key only for User and Admin; otherwise the reference key). The
 * protocol page shows them in tabs, its Markdown download all three.
 */
export interface CodeSnippet {
  id: 'python' | 'c' | 'shell'
  label: string
  /** The fence language in Markdown. */
  lang: string
  code: string
}

type Examples = S['UdpProtocolExamples']

/** The suite the code uses: the first keyed, unencrypted one the node allows, else a strong one. */
export function codeSuite(examples: Examples): SecuritySuite {
  const allowed = examples.allowedSuites
  return (
    allowed.find((s) => suiteInfo(s)?.keyed && !suiteInfo(s)?.encrypted) ??
    allowed.find((s) => !isWeak(s)) ??
    'HmacSha256_32'
  )
}

export function codeSnippets(examples: Examples): CodeSnippet[] {
  const where = endpointParts(examples.endpoint)
  const id = examples.deviceId
  const aes = examples.keyFamily === 'Aes'
  const info = suiteInfo(codeSuite(examples))!
  const keyHex = examples.keyHex ?? '<32 hex digits of the key>'
  const tokenHex = examples.tokenHex ?? '<8 hex digits of the token>'
  const keyBytes = examples.keyHex ? parseHexInput(examples.keyHex).bytes : null
  const keyC = keyBytes ? toCArray(keyBytes) : '{ /* the 16 key bytes */ }'
  const hash = ({ HmacSha256: 'sha256', HmacSha1: 'sha1', HmacMd5: 'md5', Aes: 'aes_cmac' } as const)[
    examples.keyFamily
  ]

  const python = (() => {
    const mac = aes
      ? `from cryptography.hazmat.primitives.cmac import CMAC          # pip install cryptography
from cryptography.hazmat.primitives.ciphers import algorithms

def tag(fcnt, pkt, length):
    c = CMAC(algorithms.AES(KEY))
    c.update(struct.pack('>I', fcnt) + pkt)                       # AES-CMAC(K, FCNT32 || P)
    return c.finalize()[:length]`
      : `def tag(fcnt, pkt, length):
    return hmac.new(KEY, struct.pack('>I', fcnt) + pkt, hashlib.${hash}).digest()[:length]   # HMAC(K, FCNT32 || P)`
    return `# PNMT-UDP v1 for node ${id}: Python 3${aes ? '' : ', standard library only'}.
import hmac, hashlib, socket, struct

SERVER = ('${where.host}', ${where.port})
DEV_ID = ${id}
KEY = bytes.fromhex('${keyHex}')            # 16 bytes
TOKEN = bytes.fromhex('${tokenHex}')        # 4 bytes, for TOKEN32 (suite 1)
SUITE = ${info.code}                                          # ${info.name}
HEARTBEAT, DATA, CMD_RESULT, BOOT = 0x0, 0x1, 0x3, 0x4

${mac}

def packet(ptype, fcnt, payload=b'', suite=SUITE, ack=False):
    idl = max(1, (DEV_ID.bit_length() + 7) // 8)                 # the id in 1-4 bytes
    pkt = bytes([0x40 | (ptype << 2) | (idl - 1), (0x80 if ack else 0) | suite])
    pkt += DEV_ID.to_bytes(idl, 'big') + struct.pack('>H', fcnt & 0xFFFF) + payload
    if suite == 0:
        return pkt                                               # NONE
    if suite == 1:
        return pkt + TOKEN                                       # TOKEN32
    return pkt + tag(fcnt, pkt, 8 if suite % 2 else 4)           # /64 suites have odd codes

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.settimeout(2)
fcnt = 1                                  # persist it: a keyed suite never reuses a counter
sock.sendto(packet(HEARTBEAT, fcnt, ack=True), SERVER)
try:
    answer, _ = sock.recvfrom(512)        # an ACK or a COMMAND: verify its tag before acting
    print(answer.hex(' '))
except socket.timeout:
    print('no answer')`
  })()

  const c = (() => {
    const length = idLength(id)
    return `/* PNMT-UDP v1 for node ${id} (header bytes ${idBytes(id)}), ${info.name}.
 * ${hash === 'aes_cmac' ? 'aes_cmac()' : `hmac_${hash}()`} comes from the platform: mbedTLS, ESP-IDF, the MCU's crypto engine. */
#define DEVICE_ID  ${id}u
#define ID_LEN     ${length}
#define SUITE      ${info.code}        /* ${info.name} */
#define TAG_LEN    ${info.tag}
static const uint8_t KEY[16] = ${keyC};

/* Builds an uplink into out (at most 508 bytes) and returns its length. */
size_t pnmt_uplink(uint8_t *out, uint8_t type, uint32_t fcnt, const uint8_t *payload, size_t len, bool ack)
{
    size_t n = 0;
    out[n++] = 0x40 | (type << 2) | (ID_LEN - 1);        /* VER 01, TYPE, IDL */
    out[n++] = (ack ? 0x80 : 0x00) | SUITE;               /* A, M = 0, RSV = 0, SUITE */
    for (int i = ID_LEN - 1; i >= 0; i--)
        out[n++] = (uint8_t)(DEVICE_ID >> (8 * i));       /* the id, big-endian */
    out[n++] = (uint8_t)(fcnt >> 8);                      /* the low 16 bits of FCNT */
    out[n++] = (uint8_t)fcnt;
    memcpy(out + n, payload, len);
    n += len;

    uint8_t msg[4 + 508], mac[32];
    msg[0] = fcnt >> 24; msg[1] = fcnt >> 16; msg[2] = fcnt >> 8; msg[3] = fcnt;   /* FCNT32 */
    memcpy(msg + 4, out, n);                                                       /* || P */
    ${hash === 'aes_cmac' ? 'aes_cmac' : `hmac_${hash}`}(KEY, sizeof KEY, msg, 4 + n, mac);
    memcpy(out + n, mac, TAG_LEN);
    return n + TAG_LEN;
}

/* After sending: listen ~2 s (up to 10 s on NB-IoT) if A was set or commands are expected.
 * Accept a downlink only if
 *   ((in[0] >> 2) & 0x0F) >= 0x8                      a downlink type,
 *   (in[1] & 0x0F) is your SUITE or the response suite,
 *   its counter equals the uplink's (bytes 2 + ID_LEN, 3 + ID_LEN),
 *   and its tag, over the uplink's FCNT32 || the downlink's bytes, verifies.
 * COMMAND (0x9): in[2 + ID_LEN + 2] is CMD_ID; run each CMD_ID once, answer with CMD_RESULT (0x3). */`
  })()

  const shell = (() => {
    const lines: string[] = []
    if (examples.allowedSuites.includes('Token32')) {
      const token = examples.tokenHex ? toHex(parseHexInput(examples.tokenHex).bytes ?? []) : '<TOKEN>'
      lines.push(
        '# TOKEN32, no crypto: the same bytes every time is fine (the counter is not enforced)',
        `echo ${(0x40 | (idLength(id) - 1)).toString(16).toUpperCase()} 01 ${idBytes(id)} 00 01 ${token} | xxd -r -p | nc -u -w1 ${where.host} ${where.port}`,
        '',
      )
    }
    if (!aes) {
      const idHex = idBytes(id).replace(/ /g, '')
      lines.push(
        `# ${info.name} with openssl: FCNT must increase with every packet`,
        `KEY=${examples.keyHex ?? '<key hex>'}; FCNT=1`,
        `PKT=$(printf '%02x%02x${idHex}%04x' $((0x40 | ${idLength(id) - 1})) $((0x80 | ${info.code})) "$FCNT")`,
        `TAG=$(printf '%08x%s' "$FCNT" "$PKT" | xxd -r -p \\`,
        `  | openssl dgst -${hash} -mac HMAC -macopt "hexkey:$KEY" -binary | head -c ${info.tag} | xxd -p)`,
        `echo "$PKT$TAG" | xxd -r -p | nc -u -w1 ${where.host} ${where.port}`,
      )
    } else {
      lines.push(
        '# AES-CMAC and AES-CCM need more than a shell: use pnmtd udp send (the builder below prints the line),',
        '# or the Python example with the cryptography package.',
      )
    }
    return lines.join('\n')
  })()

  return [
    { id: 'python', label: 'Python', lang: 'python', code: python },
    { id: 'c', label: 'C', lang: 'c', code: c },
    { id: 'shell', label: 'Shell', lang: 'sh', code: shell },
  ]
}
