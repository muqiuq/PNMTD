<script setup lang="ts">
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { INTERVAL_PRESETS, MAX_INTERVAL } from './certificate'

/**
 * *Check every* (docs/ssl-certificate-plan.md §8.2, R18): a preset, or a custom number of hours or days, from 1 hour to
 * 30 days, as BackupScheduleField. The model is the interval in seconds, undefined while a custom one is empty.
 */
const model = defineModel<number | undefined>({ required: true })
defineProps<{ inputId: string; invalid?: boolean }>()

const CUSTOM = 'custom'
const HOUR = 3_600
const DAY = 86_400
const isPreset = (seconds: number | undefined) => INTERVAL_PRESETS.some((p) => p.seconds === seconds)
const unitOf = (seconds: number) => (seconds % DAY === 0 ? 'days' : 'hours')
const choice = ref<number | typeof CUSTOM>(isPreset(model.value) ? model.value! : CUSTOM)
const unit = ref<'hours' | 'days'>(typeof model.value === 'number' ? unitOf(model.value) : 'hours')
const amount = ref<number | null>(
  typeof model.value === 'number' ? Math.round(model.value / (unit.value === 'days' ? DAY : HOUR)) : null,
)

// Another sensor, or a reset: the field follows the model when it changes from outside.
watch(model, (seconds) => {
  if (seconds === customSeconds() && choice.value === CUSTOM) return
  if (isPreset(seconds)) {
    choice.value = seconds!
  } else if (typeof seconds === 'number') {
    choice.value = CUSTOM
    unit.value = unitOf(seconds)
    amount.value = Math.round(seconds / (unit.value === 'days' ? DAY : HOUR))
  }
})

function customSeconds(): number | undefined {
  return amount.value === null ? undefined : amount.value * (unit.value === 'days' ? DAY : HOUR)
}

const options = computed(() => [
  ...INTERVAL_PRESETS.map((p) => ({ label: p.label, value: p.seconds })),
  { label: 'Custom…', value: CUSTOM },
])

function choose(value: number | typeof CUSTOM) {
  choice.value = value
  if (value === CUSTOM) {
    if (amount.value === null && typeof model.value === 'number') {
      unit.value = unitOf(model.value)
      amount.value = Math.round(model.value / (unit.value === 'days' ? DAY : HOUR))
    }
    model.value = customSeconds()
  } else {
    model.value = value
  }
}

function setAmount(value: number | null) {
  amount.value = value
  model.value = customSeconds()
}

function setUnit(value: 'hours' | 'days') {
  unit.value = value
  model.value = customSeconds()
}
</script>

<template>
  <div class="check-every">
    <Select
      :model-value="choice"
      :options="options"
      option-label="label"
      option-value="value"
      :input-id="inputId"
      :invalid="invalid"
      aria-label="Check every"
      @update:model-value="choose"
    />
    <template v-if="choice === CUSTOM">
      <InputNumber
        :model-value="amount"
        :min="1"
        :max="unit === 'days' ? MAX_INTERVAL / DAY : MAX_INTERVAL / HOUR"
        :use-grouping="false"
        :invalid="invalid"
        aria-label="Interval"
        class="amount"
        @update:model-value="setAmount"
      />
      <Select
        :model-value="unit"
        :options="[
          { label: 'hours', value: 'hours' },
          { label: 'days', value: 'days' },
        ]"
        option-label="label"
        option-value="value"
        aria-label="Unit"
        @update:model-value="setUnit"
      />
    </template>
  </div>
</template>

<style scoped>
.check-every {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  align-items: center;
}
.amount :deep(input) {
  width: 6rem;
}
</style>
