<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { computed, ref } from 'vue'
import { api, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import { useActions } from '@/composables/useActions'
import { FIELD_FORMATS } from './advanced'
import FieldEditor from './FieldEditor.vue'
import { useApplyAdvanced } from './useAdvanced'

/**
 * *Edit fields* (docs/advanced-sensor-plan.md §10.3): the sensor's fields in their order, to add, move, change and remove.
 * Each change answers with the sensor's detail, which goes into its page at once.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const { run, ask, busy } = useActions()
const applyAdvanced = useApplyAdvanced()

const MAX_FIELDS = 30
const id = computed(() => props.sensor.id)
const fields = computed(() => props.sensor.advanced?.fields ?? [])
const formatLabel = (f: S['AdvancedFieldItem']) => FIELD_FORMATS.find((x) => x.value === f.format)?.label ?? f.format

const editorOpen = ref(false)
const editing = ref<S['AdvancedFieldItem'] | null>(null)
function add() {
  editing.value = null
  editorOpen.value = true
}
function change(field: S['AdvancedFieldItem']) {
  editing.value = field
  editorOpen.value = true
}

async function move(index: number, by: -1 | 1) {
  const ids = fields.value.map((f) => f.id)
  const target = index + by
  if (target < 0 || target >= ids.length) return
  ;[ids[index], ids[target]] = [ids[target]!, ids[index]!]
  const detail = await run(() => api.advanced.fields.order(id.value, ids), { failure: 'Not moved' })
  if (detail) await applyAdvanced(id.value, detail)
}

async function remove(field: S['AdvancedFieldItem']) {
  const charted = field.chart ? ' Its charted values go with it.' : ''
  if (!(await ask(`Remove the field “${field.name}”?`, `The report keeps the value.${charted}`, 'Remove'))) return
  const detail = await run(() => api.advanced.fields.remove(id.value, field.id), {
    success: 'Field removed',
    failure: 'Not removed',
  })
  if (detail) await applyAdvanced(id.value, detail)
}
</script>

<template>
  <Dialog v-model:visible="visible" header="Fields" modal :style="{ width: 'min(42rem, 95vw)' }">
    <p class="intro muted">
      A field gives a value of the report a label and a unit, for this tab, the overview (pinned) and a chart. At most
      {{ MAX_FIELDS }}, 10 of them charted.
    </p>
    <EmptyState
      v-if="!fields.length"
      icon="pi pi-th-large"
      title="No fields yet."
      text="Add one here, or pick a value in the report and choose Add field…"
    />
    <ol v-else class="list" aria-label="The fields, in their order">
      <li v-for="(f, i) in fields" :key="f.id">
        <div class="info">
          <div class="name">
            {{ f.name }}
            <i v-if="f.pinned" class="pi pi-thumbtack muted" title="On the overview" aria-label="on the overview" />
            <i v-if="f.chart" class="pi pi-chart-line muted" title="Charted" aria-label="charted" />
          </div>
          <div class="muted small">
            <span class="mono">{{ f.path }}</span> · {{ formatLabel(f)
            }}<template v-if="f.unit"> · {{ f.unit }}</template>
          </div>
        </div>
        <div class="actions">
          <Button
            icon="pi pi-arrow-up"
            text
            rounded
            size="small"
            severity="secondary"
            :disabled="i === 0 || busy"
            :aria-label="`Move ${f.name} up`"
            @click="move(i, -1)"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            rounded
            size="small"
            severity="secondary"
            :disabled="i === fields.length - 1 || busy"
            :aria-label="`Move ${f.name} down`"
            @click="move(i, 1)"
          />
          <Button
            icon="pi pi-pencil"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Change ${f.name}`"
            @click="change(f)"
          />
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="danger"
            :disabled="busy"
            :aria-label="`Remove ${f.name}`"
            @click="remove(f)"
          />
        </div>
      </li>
    </ol>
    <template #footer>
      <Button label="Close" severity="secondary" text @click="visible = false" />
      <Button label="Add field" icon="pi pi-plus" :disabled="fields.length >= MAX_FIELDS" @click="add" />
    </template>
  </Dialog>
  <FieldEditor
    v-model:visible="editorOpen"
    :sensor="sensor"
    :field="editing"
    @saved="(detail) => applyAdvanced(sensor.id, detail)"
  />
</template>

<style scoped>
.intro {
  margin-top: 0;
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.list li:last-child {
  border-bottom: 0;
}

.info {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.name {
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}

.actions {
  display: flex;
  flex: none;
}
</style>
