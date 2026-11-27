<script setup lang="ts">
import { computed } from 'vue'
import { verdictIcon, verdictLabel, verdictTone } from '@/api/licenses'

/**
 * An item's state (docs/licences-plan.md §4): its icon and word in the state's tone, never the colour alone. `compact` shows the
 * icon with the word as its title, for the tables' first column.
 */
const props = defineProps<{ code: number | null | undefined; compact?: boolean; label?: string }>()
const tone = computed(() => verdictTone(props.code))
const text = computed(() => props.label ?? verdictLabel(props.code))
</script>

<template>
  <span class="verdict" :class="`tone-${tone}`" :title="compact ? text : undefined" data-test="verdict">
    <i :class="verdictIcon(code)" aria-hidden="true" />
    <span v-if="!compact">{{ text }}</span>
    <span v-else class="sr-only">{{ text }}</span>
  </span>
</template>

<style scoped>
.verdict {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  white-space: nowrap;
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-danger {
  color: var(--pnmt-fail);
}

.tone-none {
  color: var(--pnmt-muted);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
