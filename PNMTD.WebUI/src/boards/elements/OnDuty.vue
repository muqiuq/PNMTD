<script setup lang="ts">
import { computed } from 'vue'
import type { BoardTime } from '../time'
import type { BoardElement, BoardElementData } from '../types'

/**
 * Who is on duty today (§7.1, docs/board-messages-plan.md §9.5): the chain's first step, then the deputies the element shows, each with
 * the label for how they are on duty and where they work today; the day's comment, and who is on duty on the next open day. Names as
 * the board names people: with names hidden, the labels alone.
 */
const props = defineProps<{ element: BoardElement; data: BoardElementData; time: BoardTime }>()
const s = computed(() => props.element.settings)
const duty = computed(() => props.data.onDuty)

const capital = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)
const people = computed(() =>
  (duty.value?.people ?? []).map((p, i) => {
    // The first step as the availability plan's settings call the responsible (docs/availability-extensions-plan.md §7).
    const own = p.as === 'responsible' && duty.value?.responsibleLabel ? duty.value.responsibleLabel : capital(p.as)
    const label = p.deputy ? (s.value.deputyLabel ?? 'Deputy') : (s.value.dutyLabel ?? own)
    const parts = [s.value.role || !p.name ? label : null, p.where].filter((x): x is string => !!x)
    return { key: `${i}-${p.name ?? ''}`, name: p.name, deputy: p.deputy, line: parts.join(' · ') }
  }),
)
const empty = computed(() =>
  duty.value?.closed ? (s.value.closedText ?? 'Closed today.') : (s.value.nobodyText ?? 'Nobody is on duty today.'),
)
const size = computed(() => s.value.textSize ?? 'Large')

/** "Tomorrow: Ben Keller", or the next open day's name: "Monday: Ben Keller". */
const next = computed(() => {
  const n = duty.value?.next
  if (!n) return null
  const today = props.time.with({ dateFormat: 'Iso' }).date(new Date())
  const [y, m, d] = today.split('-').map(Number)
  const tomorrow = new Date(Date.UTC(y!, m! - 1, d! + 1)).toISOString().slice(0, 10)
  const day = n.day === tomorrow ? 'Tomorrow' : capital(props.time.weekday(`${n.day}T12:00:00Z`))
  return `${day}: ${n.people.length ? n.people.join(', ') : 'nobody'}`
})
</script>

<template>
  <div v-if="duty" class="on-duty" :data-size="size" :data-one-line="s.oneLine ? 'true' : undefined">
    <p v-if="!people.length" class="board-muted">{{ empty }}</p>
    <p v-else-if="s.oneLine" class="line">
      <template v-for="(p, i) in people" :key="p.key"
        ><span v-if="i > 0" class="board-muted"> · </span
        ><span class="person" :data-deputy="p.deputy ? 'true' : undefined">{{ p.name ?? p.line }}</span
        ><span v-if="p.name && p.line" class="board-muted label">{{ p.line }}</span></template
      >
    </p>
    <ul v-else class="stacked">
      <li v-for="p in people" :key="p.key" :data-deputy="p.deputy ? 'true' : undefined">
        <span v-if="p.name" class="person">{{ p.name }}</span>
        <span v-if="p.line" class="board-muted">{{ p.line }}</span>
      </li>
    </ul>
    <p v-if="duty.comment" class="comment">“{{ duty.comment }}”</p>
    <p v-if="next" class="board-muted">{{ next }}</p>
  </div>
</template>

<style scoped>
.on-duty {
  display: flex;
  flex-direction: column;
  gap: 0.4em;
  --duty-name: 1.6em;
  --duty-deputy: 1.3em;
}

.on-duty[data-size='Medium'] {
  --duty-name: 1.3em;
  --duty-deputy: 1.1em;
}

.on-duty[data-size='Huge'] {
  --duty-name: 2.4em;
  --duty-deputy: 1.6em;
}

p {
  margin: 0;
}

.stacked {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.3em;
}

.stacked li {
  display: flex;
  flex-direction: column;
  gap: 0.05em;
}

.stacked li[data-deputy='true'] {
  flex-direction: row;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0 0.6em;
}

.person {
  font-size: var(--duty-name);
  font-weight: 700;
}

[data-deputy='true'] .person,
.person[data-deputy='true'] {
  font-size: var(--duty-deputy);
  font-weight: 600;
}

.line .label {
  font-size: 0.9em;
  margin-left: 0.4em;
}

.comment {
  font-style: italic;
}
</style>
