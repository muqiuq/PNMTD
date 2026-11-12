<script setup lang="ts">
import Button from 'primevue/button'
import { computed } from 'vue'
import { useCopy } from '@/composables/useCopy'
import { fromBase64, printableText, toCArray, toHex, type Bytes } from '@/format/bytes'
import type { PacketField } from '@/format/udp'
import HexDump, { type DumpSpan } from './HexDump.vue'

/**
 * A packet, inspected (docs/udp-iot-plan.md §6.3): its fields as the server's codec named them,
 * the datagram as a hex dump with each field's bytes in the field's colour, and the payload as hex
 * and ASCII side by side. For AES-CCM the wire bytes stay visible and the payload shown is the
 * decrypted one PNMTD kept. Shared by Last packets, the command log, rejections and the Protocol page.
 */
const props = withDefaults(
  defineProps<{
    /** Base64 (as the API sends it) or bytes. */
    datagram: string | Bytes | null
    fields?: PacketField[] | null
    /** The plaintext payload when the API keeps it separately (decrypted for AES-CCM, a command's bytes). */
    payload?: string | Bytes | null
    payloadLabel?: string
    /** The datagram's payload is ciphertext (AES-CCM). */
    encrypted?: boolean
    title?: string
    /** Without the fields table, for lists of many packets. */
    compact?: boolean
  }>(),
  { fields: null, payload: null, payloadLabel: undefined, encrypted: false, title: undefined, compact: false },
)

const copy = useCopy()
const asBytes = (v: string | Bytes | null | undefined): Bytes =>
  v === null || v === undefined ? new Uint8Array(0) : typeof v === 'string' ? fromBase64(v) : v

const bytes = computed(() => asBytes(props.datagram))
const fieldList = computed(() => props.fields ?? [])

// The same field always gets the same colour; the payload's own fields take the rest in turn.
const fixed: Record<string, number> = {
  'VER · TYPE · IDL': 0,
  'A · M · SUITE': 1,
  'DEVICE ID': 2,
  COUNTER: 3,
  TAG: 4,
  TOKEN: 4,
}
const spans = computed<DumpSpan[]>(() => {
  let next = 5
  return fieldList.value.map((f) => ({
    offset: f.offset,
    length: f.length,
    name: f.name,
    slot: fixed[f.name] ?? next++,
  }))
})

// The payload: the bytes between the counter and the tag, unless the API kept a plaintext one.
const range = computed(() => {
  const counter = fieldList.value.find((f) => f.name === 'COUNTER')
  if (!counter) return null
  const tag = fieldList.value.find((f) => f.name === 'TAG' || f.name === 'TOKEN')
  return { start: counter.offset + counter.length, end: tag ? tag.offset : bytes.value.length }
})
// The codec names an AES-CCM payload "encrypted": then the wire bytes are ciphertext.
const isEncrypted = computed(
  () => props.encrypted || fieldList.value.some((f) => f.name === 'PAYLOAD' && f.value === 'encrypted'),
)
const given = computed(() => (props.payload === null || props.payload === undefined ? null : asBytes(props.payload)))
const payloadBytes = computed<Bytes | null>(() => {
  if (given.value) return given.value
  if (!range.value || isEncrypted.value) return null
  return bytes.value.subarray(range.value.start, range.value.end)
})
const payloadSpans = computed<DumpSpan[]>(() => {
  if (given.value || !range.value) return []
  const start = range.value.start
  return spans.value
    .filter((s) => s.offset >= start && s.offset < range.value!.end)
    .map((s) => ({ ...s, offset: s.offset - start }))
})
const payloadTitle = computed(
  () => props.payloadLabel ?? (isEncrypted.value && given.value ? 'Payload (decrypted)' : 'Payload'),
)
const text = computed(() => (payloadBytes.value ? printableText(payloadBytes.value) : null))
const encryptedHidden = computed(
  () => isEncrypted.value && !given.value && !!range.value && range.value.end > range.value.start,
)

const fieldHex = (f: PacketField) => {
  const part = bytes.value.subarray(f.offset, f.offset + f.length)
  return part.length > 12 ? `${toHex(part.subarray(0, 12))} …` : toHex(part)
}
</script>

<template>
  <div class="packet-inspector">
    <div class="head">
      <span v-if="title" class="title">{{ title }}</span>
      <span class="muted">{{ bytes.length }} byte{{ bytes.length === 1 ? '' : 's' }}</span>
      <span class="copy">
        <Button
          label="Hex"
          icon="pi pi-copy"
          size="small"
          text
          severity="secondary"
          :aria-label="`Copy ${title ?? 'the packet'} as hex`"
          @click="copy(toHex(bytes), 'Hex')"
        />
        <Button
          label="C array"
          icon="pi pi-copy"
          size="small"
          text
          severity="secondary"
          :aria-label="`Copy ${title ?? 'the packet'} as a C array`"
          @click="copy(toCArray(bytes), 'C array')"
        />
      </span>
    </div>

    <ul v-if="spans.length" class="legend" aria-label="Fields">
      <li v-for="s in spans" :key="`${s.offset}-${s.name}`" :class="`field-slot-${s.slot % 9}`">
        <span class="swatch" aria-hidden="true" />{{ s.name }}
      </li>
    </ul>
    <HexDump :bytes="bytes" :spans="spans" :label="`${title ?? 'Datagram'}: hex dump`" />

    <!-- Never wider than the inspector: bytes wrap between bytes, values between words; the
         wrapper scrolls only if even that is not enough. -->
    <div v-if="fieldList.length && !compact" class="fields-wrap">
      <table class="fields">
        <thead>
          <tr>
            <th scope="col">Field</th>
            <th scope="col" class="num">Offset</th>
            <th scope="col" class="hex">Bytes</th>
            <th scope="col">Value</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(f, i) in fieldList" :key="`${f.offset}-${f.name}`">
            <td class="name" :class="`field-slot-${spans[i].slot % 9}`">
              <span class="named"><span class="swatch" aria-hidden="true" />{{ f.name }}</span>
            </td>
            <td class="num mono">
              {{ f.offset }}<span v-if="f.length > 1" class="muted">+{{ f.length }}</span>
            </td>
            <td class="mono hex">
              <div>{{ fieldHex(f) }}</div>
            </td>
            <td class="value">
              <div>{{ f.value }}</div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="payloadBytes && payloadBytes.length" class="payload">
      <div class="sub">
        {{ payloadTitle }} · {{ payloadBytes.length }} byte{{ payloadBytes.length === 1 ? '' : 's' }}
      </div>
      <HexDump :bytes="payloadBytes" :spans="payloadSpans" :label="payloadTitle" />
      <div v-if="text" class="text">
        <span class="muted">UTF-8</span> <code>{{ text }}</code>
      </div>
    </div>
    <p v-else-if="encryptedHidden" class="muted note">
      The payload is encrypted on the wire; PNMTD keeps the decrypted bytes only where it accepted the packet.
    </p>
  </div>
</template>

<style scoped>
.packet-inspector {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.title {
  font-weight: 600;
}

.copy {
  margin-left: auto;
  display: inline-flex;
}

.legend {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  list-style: none;
  margin: 0;
  padding: 0;
  font-size: 0.857rem;
}

/* On a span, not the cell: a td with display: inline-flex is no table cell any more. */
.legend li,
.fields .named {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  white-space: nowrap;
}

.swatch {
  display: inline-block;
  width: 0.7rem;
  height: 0.7rem;
  border-radius: 2px;
  background: color-mix(in srgb, var(--field-colour, var(--pnmt-muted)) 60%, transparent);
  flex: none;
}

.fields {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.929rem;
}

.fields th {
  text-align: left;
  font-weight: 500;
  color: var(--pnmt-muted);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
}

.fields td {
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.fields .num {
  text-align: right;
  white-space: nowrap;
}

.fields-wrap {
  overflow-x: auto;
  max-width: 100%;
}

/* The bytes wrap between bytes, at least four to a line ("00 01 02 03"). */
.fields .hex div {
  min-width: 11ch;
}

/* A block's min-width counts for the column's minimum, so the value keeps a readable width
   ("versi on 1" was the column squeezed to five characters); long words still break. */
.fields .value div {
  min-width: 9rem;
  overflow-wrap: anywhere;
}

.sub {
  font-weight: 500;
  margin-bottom: var(--pnmt-space-1);
}

.text {
  margin-top: var(--pnmt-space-1);
  overflow-wrap: anywhere;
}

.note {
  margin: 0;
  font-size: 0.929rem;
}

@media (max-width: 599px) {
  .fields .hex {
    display: none;
  }
}
</style>
