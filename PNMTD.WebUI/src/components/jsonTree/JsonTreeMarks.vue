<script setup lang="ts">
import { computed, inject } from 'vue'
import { formatPath, outcomeLabel, outcomeMark, outcomeTone, type Segment } from '@/views/sensors/advanced/advanced'
import { jsonTreeKey, type JsonMark } from './jsonTree'

/**
 * What follows a value in JsonTree (docs/advanced-sensor-plan.md §10.3): the marks of the rules that look at it (✓ ✗ !,
 * with the rule's name and outcome on hover) and the button of its menu.
 */
const props = defineProps<{ segments: Segment[]; value: unknown }>()
const ctx = inject(jsonTreeKey)!
const path = computed(() => formatPath(props.segments))
const marks = computed(() => ctx.marks.value.get(path.value) ?? [])

function title(m: JsonMark): string {
  const what = m.outcome ? outcomeLabel(m.outcome) : 'looks at this value; decided by others'
  return `${m.name}: ${what}${m.detail ? ` (${m.detail})` : ''}`
}
</script>

<template>
  <span
    v-for="(m, i) in marks"
    :key="i"
    class="jt-mark"
    :class="`tone-${m.outcome ? outcomeTone(m.outcome) : 'muted'}`"
    role="img"
    :aria-label="title(m)"
    :title="title(m)"
    >{{ m.outcome ? outcomeMark(m.outcome) : '•' }}</span
  >
  <button
    type="button"
    class="jt-act"
    :aria-label="`Actions for ${path || 'the whole report'}`"
    aria-haspopup="true"
    @click="ctx.openMenu($event, segments, value)"
  >
    <i class="pi pi-ellipsis-v" aria-hidden="true" />
  </button>
</template>
