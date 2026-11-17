<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import { fieldTone, formatFieldValue } from './advanced'
import FieldListDialog from './FieldListDialog.vue'
import SampleChart from './SampleChart.vue'

/**
 * An Advanced sensor's fields as cards (docs/advanced-sensor-plan.md §10.3): each with its label and its value with the
 * unit, red or amber when a rule on its path doesn't hold, and a chart when it is charted. On the overview (`pinnedOnly`)
 * only the pinned ones with a 24 h sparkline, and nothing at all without any; on the Data tab all of them with their
 * charts and, for Edit, *Edit fields*. Without the right to the sensor's data (it names people) there are no values.
 */
const props = withDefaults(defineProps<{ sensor: S['SensorDetail']; pinnedOnly?: boolean; canWrite?: boolean }>(), {
  pinnedOnly: false,
  canWrite: false,
})
const a = computed(() => props.sensor.advanced ?? null)
const fields = computed(() => (a.value?.fields ?? []).filter((f) => !props.pinnedOnly || f.pinned))
const shown = computed(() => !!a.value && a.value.canSeeData && (!props.pinnedOnly || fields.value.length > 0))
const editing = ref(false)
const titleId = computed(() => `fields-${props.pinnedOnly ? 'pinned' : 'all'}-${props.sensor.id}`)

/** The tone shown: red and amber for a rule that doesn't hold; green only for a status word. */
function tone(f: S['AdvancedFieldItem']): string {
  const t = fieldTone(f)
  return t === 'fail' || t === 'warn' || (t === 'ok' && f.format === 'Status') ? t : 'plain'
}
const toneLabel = (f: S['AdvancedFieldItem']) =>
  f.outcome === 'Fail' ? 'a rule fails' : f.outcome === 'Warn' ? 'a rule warns' : null
</script>

<template>
  <section v-if="shown" class="panel field-cards" :class="{ pinned: pinnedOnly }" :aria-labelledby="titleId">
    <div class="panel-header">
      <span :id="titleId">Fields</span>
      <span v-if="!pinnedOnly && fields.length" class="muted count">{{ fields.length }}</span>
      <span class="spacer" />
      <Button
        v-if="!pinnedOnly && canWrite"
        label="Edit fields"
        icon="pi pi-pencil"
        size="small"
        text
        @click="editing = true"
      />
    </div>
    <div class="panel-body">
      <EmptyState
        v-if="!fields.length"
        icon="pi pi-th-large"
        title="No fields yet."
        :text="
          canWrite
            ? 'Pick a value in the report and choose Add field…, or use Edit fields.'
            : 'Someone with Monitoring: Edit can pick the values to show here.'
        "
      />
      <ul v-else class="cards">
        <li v-for="f in fields" :key="f.id" class="card" :class="`tone-${tone(f)}`">
          <div class="label">
            <span class="ellipsis" :title="f.name">{{ f.name }}</span>
            <i
              v-if="f.pinned && !pinnedOnly"
              class="pi pi-thumbtack muted"
              title="On the overview"
              aria-hidden="true"
            />
          </div>
          <div class="value" :title="f.path">
            {{ formatFieldValue(f.value, f) }}
            <span v-if="toneLabel(f)" class="sr">({{ toneLabel(f) }})</span>
          </div>
          <div v-if="!pinnedOnly" class="path mono muted ellipsis" :title="f.path">{{ f.path }}</div>
          <SampleChart v-if="f.chart" :sensor-id="sensor.id" :field="f" :compact="pinnedOnly" />
        </li>
      </ul>
    </div>
    <FieldListDialog v-if="!pinnedOnly && canWrite" v-model:visible="editing" :sensor="sensor" />
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.count {
  font-weight: 400;
}

.cards {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 20rem), 1fr));
  gap: var(--pnmt-space-3);
}

.pinned .cards {
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 9rem), 1fr));
  gap: var(--pnmt-space-2);
}

.card {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-left: 3px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.label {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  font-size: 0.857rem;
  color: var(--pnmt-muted);
  font-weight: 500;
  min-width: 0;
}

.label .pi {
  font-size: 0.75rem;
}

.value {
  font-size: 1.286rem;
  font-weight: 600;
  overflow-wrap: anywhere;
}

.pinned .value {
  font-size: 1.071rem;
}

.path {
  font-size: 0.786rem;
}

.card.tone-fail {
  border-left-color: var(--pnmt-fail);
}

.card.tone-fail .value {
  color: var(--pnmt-fail);
}

.card.tone-warn {
  border-left-color: var(--pnmt-warn);
}

.card.tone-warn .value {
  color: var(--pnmt-warn);
}

.card.tone-ok .value {
  color: var(--pnmt-ok);
}

.sr {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>
