<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { formatDay, formatPercent } from '@/format'
import { dayOf, uptimeTone, visibleCount } from './uptime'

/**
 * Uptime as a strip of bars (§5.7), one per day (or hour), always ending with the newest. It shows as
 * many as fit at ≥ 3 px each, never fewer than 30, so a narrow screen loses old days, not recent ones.
 */
const props = withDefaults(
  defineProps<{
    /** Oldest first; null is a day without data. */
    values: (number | null)[]
    /** The day of values[0] (yyyy-mm-dd); without it the values are hours, the last one this hour. */
    start?: string | null
    height?: number
    minWidth?: number
    minimum?: number
  }>(),
  { start: null, height: 16, minWidth: 3, minimum: 30 },
)

const root = ref<HTMLElement | null>(null)
const width = ref(0)
let observer: ResizeObserver | undefined

onMounted(() => {
  width.value = root.value?.clientWidth ?? 0
  if (typeof ResizeObserver !== 'undefined' && root.value) {
    observer = new ResizeObserver((entries) => (width.value = entries[0]?.contentRect.width ?? width.value))
    observer.observe(root.value)
  }
})
onBeforeUnmount(() => observer?.disconnect())

const count = computed(() => visibleCount(width.value, props.values.length, props.minWidth, props.minimum))

const bars = computed(() => {
  const first = props.values.length - count.value
  return props.values.slice(first).map((value, i) => {
    const index = first + i
    const when = props.start
      ? formatDay(dayOf(props.start, index))
      : index === props.values.length - 1
        ? 'this hour'
        : `${props.values.length - 1 - index} h ago`
    return { value, tone: uptimeTone(value), title: `${when}: ${value === null ? 'no data' : formatPercent(value, 2)}` }
  })
})
</script>

<template>
  <div ref="root" class="uptime-bar" :style="{ height: `${height}px` }">
    <svg
      v-if="bars.length"
      :viewBox="`0 0 ${bars.length} 1`"
      preserveAspectRatio="none"
      width="100%"
      :height="height"
      role="img"
      :aria-label="`Uptime of the last ${bars.length} ${start ? 'days' : 'hours'}`"
    >
      <rect
        v-for="(bar, i) in bars"
        :key="i"
        :x="i + 0.12"
        y="0"
        width="0.76"
        height="1"
        :class="`tone-${bar.tone}`"
        :data-tone="bar.tone"
      >
        <title>{{ bar.title }}</title>
      </rect>
    </svg>
  </div>
</template>

<style scoped>
.uptime-bar {
  width: 100%;
  min-width: 90px;
  line-height: 0;
}

rect.tone-ok {
  fill: var(--pnmt-ok);
}

rect.tone-warn {
  fill: var(--pnmt-warn);
}

rect.tone-fail {
  fill: var(--pnmt-fail);
}

rect.tone-none {
  fill: var(--pnmt-none-soft);
}

rect:hover {
  opacity: 0.7;
}
</style>
