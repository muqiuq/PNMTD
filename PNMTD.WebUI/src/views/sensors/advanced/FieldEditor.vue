<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, reactive, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import { useNotify } from '@/composables/useNotify'
import { FIELD_FORMATS, type FieldFormat } from './advanced'

/**
 * The field editor (docs/advanced-sensor-plan.md §5.3, §10.3), a dialog: a new field (with `initial` from a value on the
 * Data tab) or `field` to change. A field is one value, so its path has no `*`. The server checks again and its messages
 * show under the inputs; `saved` carries the sensor's new detail.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  sensor: S['SensorDetail']
  field?: S['AdvancedFieldItem'] | null
  initial?: Partial<S['AdvancedFieldRequest']> | null
}>()
const emit = defineEmits<{ saved: [detail: S['AdvancedSensorDetail']] }>()
const notify = useNotify()

const MAX_CHARTED = 10

interface Form {
  name: string
  path: string
  format: FieldFormat
  unit: string
  decimals: number | null
  pinned: boolean
  chart: boolean
}
const form = reactive<Form>({
  name: '',
  path: '',
  format: 'Auto',
  unit: '',
  decimals: null,
  pinned: false,
  chart: false,
})
const errors = ref<Partial<Record<keyof Form, string>>>({})
const busy = ref(false)

function reset() {
  const f = props.field
  const i = props.initial ?? {}
  Object.assign(
    form,
    f
      ? {
          name: f.name,
          path: f.path,
          format: f.format,
          unit: f.unit ?? '',
          decimals: f.decimals,
          pinned: f.pinned,
          chart: f.chart,
        }
      : {
          name: i.name ?? '',
          path: i.path ?? '',
          format: i.format ?? 'Auto',
          unit: i.unit ?? '',
          decimals: i.decimals ?? null,
          pinned: i.pinned ?? false,
          chart: i.chart ?? false,
        },
  )
  errors.value = {}
}
watch(visible, (open) => open && reset(), { immediate: true })

const id = computed(() => props.sensor.id)
const editing = computed(() => !!props.field)
/** The unit follows a plain number; decimals a number or percent. */
const takesUnit = computed(() => form.format === 'Auto' || form.format === 'Number')
const takesDecimals = computed(() => takesUnit.value || form.format === 'Percent')
const chartedElsewhere = computed(
  () => (props.sensor.advanced?.fields ?? []).filter((f) => f.chart && f.id !== props.field?.id).length,
)
const chartFull = computed(() => !form.chart && chartedElsewhere.value >= MAX_CHARTED)
const dropsSamples = computed(() => !!props.field?.chart && (!form.chart || form.path.trim() !== props.field.path))

function check(): boolean {
  const e: Partial<Record<keyof Form, string>> = {}
  if (!form.name.trim()) e.name = 'A name, such as Free space.'
  else if (form.name.trim().length > 100) e.name = 'At most 100 characters.'
  if (!form.path.trim()) e.path = 'A path into the report, such as disk.free_gb.'
  else if (form.path.includes('*')) e.path = 'A field is one value: a path without *.'
  else if (form.path.trim().length > 500) e.path = 'At most 500 characters.'
  if (form.unit.trim().length > 20) e.unit = 'At most 20 characters.'
  errors.value = e
  return !Object.keys(e).length
}

async function save() {
  if (!check()) return
  busy.value = true
  const body: S['AdvancedFieldRequest'] = {
    name: form.name.trim(),
    path: form.path.trim(),
    format: form.format,
    unit: takesUnit.value ? form.unit.trim() || null : null,
    decimals: takesDecimals.value ? form.decimals : null,
    pinned: form.pinned,
    chart: form.chart,
  }
  try {
    const detail = props.field
      ? await api.advanced.fields.update(id.value, props.field.id, body)
      : await api.advanced.fields.create(id.value, body)
    notify.success(props.field ? 'Field changed' : 'Field added', body.name ?? undefined)
    visible.value = false
    emit('saved', detail)
  } catch (e) {
    const known = ['name', 'path', 'format', 'unit', 'decimals', 'pinned', 'chart'] as const
    if (e instanceof ApiError && known.some((k) => e.field(k))) {
      const found: Partial<Record<keyof Form, string>> = {}
      for (const k of known) {
        const message = e.field(k)
        if (message) found[k] = message
      }
      errors.value = found
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="editing ? 'Change the field' : 'Add a field'"
    modal
    :style="{ width: 'min(34rem, 95vw)' }"
  >
    <form id="advanced-field-form" class="form" novalidate @submit.prevent="save">
      <p class="intro muted">
        A field gives a value of the report a label and a unit, for the Data tab, the overview and a chart.
      </p>
      <div class="field">
        <label for="field-name">Name</label>
        <InputText id="field-name" v-model="form.name" maxlength="100" autofocus :invalid="!!errors.name" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="field-path">Path</label>
        <InputText
          id="field-path"
          v-model="form.path"
          class="mono"
          maxlength="500"
          spellcheck="false"
          autocomplete="off"
          :invalid="!!errors.path"
        />
        <small v-if="errors.path" class="field-error">{{ errors.path }}</small>
        <small v-else class="field-hint"
          >One value, such as <code>disk.free_gb</code> or <code>jobs[0].state</code>; no <code>*</code>: a field is one
          value.</small
        >
      </div>
      <div class="form-grid">
        <div class="field">
          <label for="field-format">Format</label>
          <Select
            v-model="form.format"
            input-id="field-format"
            :options="[...FIELD_FORMATS]"
            option-label="label"
            option-value="value"
            :invalid="!!errors.format"
          />
          <small v-if="errors.format" class="field-error">{{ errors.format }}</small>
        </div>
        <div class="field">
          <label for="field-unit">Unit</label>
          <InputText
            id="field-unit"
            v-model="form.unit"
            maxlength="20"
            :disabled="!takesUnit"
            :placeholder="takesUnit ? 'such as GB or ms' : 'from the format'"
            :invalid="!!errors.unit"
          />
          <small v-if="errors.unit" class="field-error">{{ errors.unit }}</small>
        </div>
        <div class="field">
          <label for="field-decimals">Decimals</label>
          <InputNumber
            v-model="form.decimals"
            input-id="field-decimals"
            :min="0"
            :max="6"
            :use-grouping="false"
            :disabled="!takesDecimals"
            placeholder="as needed"
            :invalid="!!errors.decimals"
          />
          <small v-if="errors.decimals" class="field-error">{{ errors.decimals }}</small>
          <small v-else class="field-hint">0 to 6, or empty for as needed.</small>
        </div>
      </div>
      <div class="checks">
        <div class="check">
          <Checkbox v-model="form.pinned" input-id="field-pinned" binary />
          <label for="field-pinned">Show on the overview</label>
        </div>
        <div class="check">
          <Checkbox v-model="form.chart" input-id="field-chart" binary :disabled="chartFull" />
          <label for="field-chart">Chart it</label>
        </div>
        <small v-if="errors.chart" class="field-error">{{ errors.chart }}</small>
        <small v-else-if="chartFull" class="field-hint">At most {{ MAX_CHARTED }} charted fields per sensor.</small>
        <small v-else class="field-hint">A chart keeps a value per minute for 30 days, for numbers.</small>
        <small v-if="dropsSamples" class="field-hint warn">
          {{ form.chart ? 'Another path' : 'Without the chart' }}, the charted values so far are deleted.
        </small>
      </div>
      <small v-if="errors.pinned" class="field-error">{{ errors.pinned }}</small>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="advanced-field-form"
        :label="editing ? 'Save' : 'Add field'"
        icon="pi pi-check"
        :loading="busy"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin: 0;
}

.checks {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.warn {
  color: var(--pnmt-warn);
}

@media (max-width: 599px) {
  .form-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
