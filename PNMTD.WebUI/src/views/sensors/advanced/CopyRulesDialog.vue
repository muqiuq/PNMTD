<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useApplyAdvanced } from './useAdvanced'

/**
 * *Copy rules from another sensor* (docs/advanced-sensor-plan.md §8.3), for an app deployed many times: another Advanced
 * sensor's rules and fields, appended to these or replacing them. Replacing asks first, naming what goes. The answer is the
 * sensor's detail once re-judged, which goes into the page.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const { ask, notify } = useActions()
const apply = useApplyAdvanced()

const id = computed(() => props.sensor.id)
const a = computed(() => props.sensor.advanced ?? null)
const others = useQuery({
  queryKey: computed(() => [...keys.sensors, { type: 'ADVANCED', pageSize: 500 }]),
  queryFn: () => api.sensors.list({ type: 'ADVANCED', pageSize: 500, sort: 'host' }),
  enabled: visible,
})
const options = computed(() =>
  (others.data.value?.items ?? [])
    .filter((s) => s.id !== id.value)
    .map((s) => ({ value: s.id, label: `${s.host.name} / ${s.name}` })),
)

const from = ref<string | null>(null)
const replace = ref(false)
const error = ref<string | null>(null)
const busy = ref(false)
watch(visible, (open) => {
  if (!open) return
  from.value = null
  replace.value = false
  error.value = null
})
watch(from, () => (error.value = null))

const ruleCount = computed(() => a.value?.rules.length ?? 0)
const fieldCount = computed(() => a.value?.fields.length ?? 0)
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

async function copy() {
  const source = options.value.find((o) => o.value === from.value)
  if (!source || busy.value) return
  if (
    replace.value &&
    (ruleCount.value || fieldCount.value) &&
    !(await ask(
      'Replace the rules and fields?',
      `This sensor's ${plural(ruleCount.value, 'rule')} and ${plural(fieldCount.value, 'field')} are deleted and replaced by those of ${source.label}. This cannot be undone.`,
      'Replace',
    ))
  ) {
    return
  }
  busy.value = true
  error.value = null
  try {
    const detail = await api.advanced.rules.copy(id.value, { fromSensorId: source.value, replace: replace.value })
    void apply(id.value, detail)
    const rules = detail.rules.length - (replace.value ? 0 : ruleCount.value)
    const fields = detail.fields.length - (replace.value ? 0 : fieldCount.value)
    notify.success('Rules copied', `${plural(rules, 'rule')} and ${plural(fields, 'field')} from ${source.label}`)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      error.value = e.field('fromSensorId') ?? e.message
    } else {
      notify.error(e, 'Not copied')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Copy rules from another sensor"
    modal
    :style="{ width: '34rem' }"
    :breakpoints="{ '600px': '96vw' }"
  >
    <form class="form" @submit.prevent="copy">
      <p class="muted intro">
        For an app that runs in several places: the rules and the fields of another Advanced sensor, in their order.
      </p>
      <div class="field">
        <label for="copy-from">From</label>
        <Select
          v-model="from"
          input-id="copy-from"
          :options="options"
          option-label="label"
          option-value="value"
          filter
          filter-placeholder="Find a sensor"
          :loading="others.isFetching.value"
          placeholder="An Advanced sensor"
          empty-message="No other Advanced sensor"
          :invalid="!!error"
        />
        <small v-if="error" class="field-error" role="alert">{{ error }}</small>
        <small v-else-if="others.isError.value" class="field-error"
          >The sensors can't be loaded: {{ (others.error.value as Error | null)?.message }}</small
        >
      </div>
      <fieldset class="choices">
        <legend>This sensor's own</legend>
        <div class="choice">
          <RadioButton v-model="replace" input-id="copy-append" :value="false" />
          <label for="copy-append"
            ><strong>Keep them</strong>
            <span class="muted small">the copies come after its {{ plural(ruleCount, 'rule') }}</span></label
          >
        </div>
        <div class="choice">
          <RadioButton v-model="replace" input-id="copy-replace" :value="true" />
          <label for="copy-replace"
            ><strong>Replace them</strong>
            <span class="muted small"
              >its {{ plural(ruleCount, 'rule') }} and {{ plural(fieldCount, 'field') }} are deleted</span
            ></label
          >
        </div>
      </fieldset>
      <Message v-if="replace && (ruleCount || fieldCount)" severity="warn" :closable="false" size="small">
        Replacing deletes this sensor's rules and fields first.
      </Message>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="replace ? 'Replace' : 'Copy'"
        :severity="replace ? 'danger' : undefined"
        icon="pi pi-copy"
        :loading="busy"
        :disabled="!from || busy"
        @click="copy"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin: 0;
}

.choices {
  border: 0;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.choices legend {
  padding: 0;
  margin-bottom: var(--pnmt-space-1);
  font-weight: 500;
}

.choice {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
