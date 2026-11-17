<script setup lang="ts">
import { computed } from 'vue'
import { formatAbsolute, formatDay } from '@/format'

/**
 * The validity as a bar (docs/ssl-certificate-plan.md §8.3): from NotBefore to the chain's expiry, with today and the start
 * of the warning marked; the part since the warning in warn colours, the time past expiry in fail colours.
 */
const props = defineProps<{
  notBefore: string
  expiresAt: string
  warnDays: number
  checkExpiry: boolean
  now: number
}>()
const DAY = 86_400_000
const start = computed(() => Date.parse(props.notBefore))
const end = computed(() => Date.parse(props.expiresAt))
const span = computed(() => Math.max(1, end.value - start.value))
const at = (t: number) => Math.min(100, Math.max(0, ((t - start.value) / span.value) * 100))
const today = computed(() => at(props.now))
const warnFrom = computed(() => end.value - props.warnDays * DAY)
const warn = computed(() => (props.checkExpiry ? at(warnFrom.value) : 100))
const label = computed(
  () =>
    `Valid from ${formatAbsolute(props.notBefore)} to ${formatAbsolute(props.expiresAt)}; today at ${Math.round(today.value)} %`,
)
</script>

<template>
  <div class="validity" role="img" :aria-label="label">
    <div class="bar">
      <div class="part ok" :style="{ width: `${warn}%` }" />
      <div class="part warn" :style="{ left: `${warn}%`, width: `${100 - warn}%` }" />
      <div v-if="now > end" class="part fail" :style="{ left: '0', width: '100%' }" />
      <div class="mark today" :style="{ left: `${today}%` }" :title="`Today, ${formatDay(new Date(now))}`" />
      <div
        v-if="checkExpiry && warn > 0 && warn < 100"
        class="mark warn-start"
        :style="{ left: `${warn}%` }"
        :title="`The warning starts on ${formatAbsolute(warnFrom)}`"
      />
    </div>
    <div class="scale muted">
      <span>{{ formatDay(new Date(notBefore)) }}</span>
      <span v-if="checkExpiry && warn > 0 && warn < 100" class="warn-label" :style="{ left: `${warn}%` }"
        >warning from {{ formatDay(new Date(warnFrom)) }}</span
      >
      <span>{{ formatDay(new Date(expiresAt)) }}</span>
    </div>
  </div>
</template>

<style scoped>
.validity {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.bar {
  position: relative;
  height: 10px;
  border-radius: 5px;
  background: var(--pnmt-none-soft);
  overflow: visible;
}

.part {
  position: absolute;
  top: 0;
  bottom: 0;
  border-radius: 5px;
}

.part.ok {
  left: 0;
  background: var(--pnmt-ok-soft);
  border: 1px solid var(--pnmt-ok);
}

.part.warn {
  background: var(--pnmt-warn-soft);
  border: 1px solid var(--pnmt-warn);
}

.part.fail {
  background: var(--pnmt-fail-soft);
  border: 1px solid var(--pnmt-fail);
}

.mark {
  position: absolute;
  top: -4px;
  bottom: -4px;
  width: 2px;
  margin-left: -1px;
}

.mark.today {
  background: var(--pnmt-text);
}

.mark.warn-start {
  background: var(--pnmt-warn);
  width: 1px;
}

.scale {
  position: relative;
  display: flex;
  justify-content: space-between;
  font-size: 0.786rem;
}

.warn-label {
  position: absolute;
  transform: translateX(-50%);
  white-space: nowrap;
  color: var(--pnmt-warn);
}

@media (max-width: 599px) {
  .warn-label {
    display: none;
  }
}
</style>
