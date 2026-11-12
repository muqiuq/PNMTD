<script setup lang="ts">
import { computed } from 'vue'
import { useMedia } from '@/composables/useMedia'
import { asciiChar, dumpGap, dumpHexWidth, dumpLines, dumpOffset, hexByte, type Bytes } from '@/format/bytes'

/** A part of the bytes to colour: a packet field (PacketInspector), with its colour slot. */
export interface DumpSpan {
  offset: number
  length: number
  name: string
  slot: number
}

/**
 * A hex dump (docs/udp-iot-plan.md §6.3): the offset, 16 bytes per line in groups of 8, the ASCII
 * rendering with "·" for everything outside 0x20–0x7E. Spans colour the bytes of each field in the
 * hex and the ASCII column alike; on a phone the lines hold 8 bytes, so nothing scrolls sideways.
 */
const props = withDefaults(defineProps<{ bytes: Bytes; spans?: DumpSpan[]; width?: number; label?: string }>(), {
  spans: () => [],
  width: undefined,
  label: 'Hex dump',
})

const phone = useMedia('(max-width: 599px)')
const perLine = computed(() => props.width ?? (phone.value ? 8 : 16))

// The span of every byte, once: datagrams are at most 508 bytes.
const spanOf = computed(() => {
  const of: (DumpSpan | undefined)[] = new Array(props.bytes.length)
  for (const span of props.spans) {
    for (let i = span.offset; i < span.offset + span.length && i < props.bytes.length; i++) {
      of[i] = span
    }
  }
  return of
})

const lines = computed(() => {
  const width = perLine.value
  const full = dumpHexWidth(width)
  return dumpLines(props.bytes, width).map((line) => {
    let used = 0
    const cells = line.bytes.map((b, i) => {
      const index = line.start + i
      const span = spanOf.value[index]
      const last = i === line.bytes.length - 1
      const gap = last ? '' : dumpGap(i, width)
      used += 2 + gap.length
      // The gap takes the field's colour when the next byte belongs to the same field.
      const joined = !last && span !== undefined && spanOf.value[index + 1] === span
      return { index, hex: hexByte(b), char: asciiChar(b), span, gap, joined }
    })
    return { offset: dumpOffset(line.offset), cells, pad: ' '.repeat(full - used) }
  })
})

const slotClass = (span: DumpSpan | undefined) => (span ? `field-slot-${span.slot % 9}` : undefined)
</script>

<template>
  <div class="hexdump" role="figure" :aria-label="label">
    <div v-for="line in lines" :key="line.offset" class="line">
      <span class="off">{{ line.offset }}</span><span class="sep">{{ '  ' }}</span
      ><template v-for="c in line.cells" :key="c.index"
        ><span class="b" :class="slotClass(c.span)" :title="c.span?.name">{{ c.hex }}</span
        ><span v-if="c.gap" :class="c.joined ? ['b', slotClass(c.span)] : undefined">{{ c.gap }}</span></template
      ><span>{{ line.pad }}</span
      ><span class="sep">{{ '  ' }}</span
      ><span class="ascii"
        ><span v-for="c in line.cells" :key="c.index" class="b" :class="slotClass(c.span)" :title="c.span?.name">{{
          c.char
        }}</span></span
      >
    </div>
    <div v-if="!bytes.length" class="muted">(no bytes)</div>
  </div>
</template>

<style scoped>
.hexdump {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  line-height: 1.6;
  white-space: pre;
  overflow-x: auto;
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.off {
  color: var(--pnmt-muted);
}

.ascii {
  color: var(--pnmt-text);
}

.b[class*='field-slot-'] {
  background: color-mix(in srgb, var(--field-colour) 24%, transparent);
}
</style>
