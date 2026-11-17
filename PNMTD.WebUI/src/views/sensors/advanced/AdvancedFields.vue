<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { formatDuration } from '@/format'
import { defaultGrace, EXPECT_EVERY } from './advanced'

/**
 * An Advanced sensor's fields in *Add sensor* (docs/advanced-sensor-plan.md §10.2): how often a report is expected (with a
 * grace preset to a fifth of it, at least a minute), the starter rule, and whether the data names people. They replace the
 * generic interval and grace for the type; the rules and fields come later, on the sensor's own tabs.
 */
export interface AdvancedDraft {
  addStatusRule: boolean
  namesPeople: boolean
}

const draft = defineModel<AdvancedDraft>({ required: true })
const interval = defineModel<number | null>('interval', { required: true })
const grace = defineModel<number | null>('grace', { required: true })
defineProps<{ errors: Record<string, string | undefined> }>()

const CUSTOM = -1
const preset = ref<number>(EXPECT_EVERY.some((e) => e.value === (interval.value ?? 0)) ? (interval.value ?? 0) : CUSTOM)
const options = [...EXPECT_EVERY, { label: 'Custom…', value: CUSTOM }]
watch(preset, (value) => {
  if (value === CUSTOM) {
    interval.value = interval.value || 600
  } else {
    interval.value = value
  }
  grace.value = defaultGrace(interval.value ?? 0)
})
const graceHint = computed(() =>
  interval.value
    ? `460 after ${formatDuration(((interval.value ?? 0) + (grace.value ?? 0)) * 1000)} without a report.`
    : 'Without an expectation, a silent app never fails the sensor.',
)
</script>

<template>
  <div class="field">
    <label for="advanced-every">Expect a report</label>
    <Select
      v-model="preset"
      input-id="advanced-every"
      :options="options"
      option-label="label"
      option-value="value"
      :invalid="!!errors.interval"
    />
    <small v-if="errors.interval" class="field-error">{{ errors.interval }}</small>
    <small v-else class="field-hint">How often the app sends its JSON. Rules on ages still apply without it.</small>
  </div>
  <div v-if="preset === CUSTOM" class="field">
    <label for="advanced-interval">Every (seconds)</label>
    <InputNumber
      v-model="interval"
      input-id="advanced-interval"
      :min="30"
      :use-grouping="false"
      :invalid="!!errors.interval"
      @update:model-value="(v: number | null) => (grace = defaultGrace(v ?? 0))"
    />
    <small class="field-hint">At least 30.</small>
  </div>
  <div v-if="interval" class="field">
    <label for="advanced-grace">Grace (seconds)</label>
    <InputNumber
      v-model="grace"
      input-id="advanced-grace"
      :min="0"
      :use-grouping="false"
      :invalid="!!errors.gracePeriod"
    />
    <small v-if="errors.gracePeriod" class="field-error">{{ errors.gracePeriod }}</small>
    <small v-else class="field-hint">{{ graceHint }}</small>
  </div>
  <div class="checks wide">
    <label class="check">
      <Checkbox v-model="draft.addStatusRule" binary input-id="advanced-status-rule" />
      <span>
        Add a rule for the app's own status
        <small class="field-hint block"
          >Its <code>status</code> must be a healthy word (ok, pass, up, healthy); warn and degraded warn. Apps without
          a status key aren't affected.</small
        >
      </span>
    </label>
    <label class="check">
      <ToggleSwitch v-model="draft.namesPeople" input-id="advanced-names-people" />
      <span>
        Its data names people
        <small class="field-hint block">Only people with Monitoring: Details see the reports and their values.</small>
      </span>
    </label>
  </div>
</template>

<style scoped>
.checks {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.check {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.block {
  display: block;
}
</style>
