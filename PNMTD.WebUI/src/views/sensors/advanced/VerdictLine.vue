<script setup lang="ts">
import { computed } from 'vue'
import { codeTone } from './advanced'

/**
 * A verdict in a line (docs/advanced-sensor-plan.md §3, §3.1): its code coloured as the outcome (296 amber, though the
 * sensor stays OK), the state in a word when known, and the message, which names the values as they were.
 */
const props = defineProps<{
  code: number | null | undefined
  message: string | null | undefined
  state?: string | null
}>()
const tone = computed(() => codeTone(props.code))
</script>

<template>
  <span class="verdict">
    <span class="code" :class="`tone-${tone}`">{{ code ?? '–' }}</span>
    <span v-if="state" class="state" :class="`tone-${tone}`">{{ state }}</span>
    <span v-if="message" class="message">{{ message }}</span>
  </span>
</template>

<style scoped>
.verdict {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
  min-width: 0;
}

.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  font-weight: 600;
  padding: 0.05rem 0.35rem;
  border-radius: var(--p-border-radius-sm);
  line-height: 1.4;
}

.code.tone-ok {
  color: var(--pnmt-ok);
  background: var(--pnmt-ok-soft);
}

.code.tone-warn {
  color: var(--pnmt-warn);
  background: var(--pnmt-warn-soft);
}

.code.tone-fail {
  color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}

.code.tone-muted {
  color: var(--pnmt-muted);
  background: var(--pnmt-none-soft);
}

.state {
  font-weight: 600;
}

.state.tone-ok {
  color: var(--pnmt-ok);
}

.state.tone-warn {
  color: var(--pnmt-warn);
}

.state.tone-fail {
  color: var(--pnmt-fail);
}

.message {
  overflow-wrap: anywhere;
  min-width: 0;
}
</style>
