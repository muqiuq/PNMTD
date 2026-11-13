<script setup lang="ts">
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { MAX_BACKUP_HOURS, SCHEDULE_PRESETS, type BackupSchedule } from './backupSchedule'

/**
 * A router's scheduled backup (docs/mikrotik-backup-plan.md §2): off, a preset, or a custom interval in hours
 * or days, up to a year. The model is the interval in hours, null for off, undefined while a custom interval
 * is empty.
 */
const model = defineModel<BackupSchedule>({ required: true })
defineProps<{ inputId: string; invalid?: boolean }>()

const CUSTOM = 'custom'
const presetOf = (hours: BackupSchedule) => SCHEDULE_PRESETS.find((p) => p.hours === hours)
const choice = ref<number | null | typeof CUSTOM>(presetOf(model.value) ? (model.value as number | null) : CUSTOM)
const unit = ref<'hours' | 'days'>(typeof model.value === 'number' && model.value % 24 === 0 ? 'days' : 'hours')
const amount = ref<number | null>(
  typeof model.value === 'number' ? (unit.value === 'days' ? model.value / 24 : model.value) : null,
)

// Another router, or a reset: the field follows the model when it changes from outside.
watch(model, (hours) => {
  if (choice.value !== CUSTOM && presetOf(hours)) {
    choice.value = hours as number | null
  } else if (choice.value !== CUSTOM || (typeof hours === 'number' && hours !== customHours())) {
    choice.value = presetOf(hours) ? (hours as number | null) : CUSTOM
    if (typeof hours === 'number' && !presetOf(hours)) {
      unit.value = hours % 24 === 0 ? 'days' : 'hours'
      amount.value = unit.value === 'days' ? hours / 24 : hours
    }
  }
})

function customHours(): number | undefined {
  return amount.value === null ? undefined : unit.value === 'days' ? amount.value * 24 : amount.value
}

const options = computed(() => [
  ...SCHEDULE_PRESETS.map((p) => ({ label: p.label, value: p.hours })),
  { label: 'Custom…', value: CUSTOM },
])

function choose(value: number | null | typeof CUSTOM) {
  choice.value = value
  if (value === CUSTOM) {
    if (amount.value === null && typeof model.value === 'number') {
      unit.value = model.value % 24 === 0 ? 'days' : 'hours'
      amount.value = unit.value === 'days' ? model.value / 24 : model.value
    }
    model.value = customHours()
  } else {
    model.value = value
  }
}

function setAmount(value: number | null) {
  amount.value = value
  model.value = customHours()
}

function setUnit(value: 'hours' | 'days') {
  unit.value = value
  model.value = customHours()
}
</script>

<template>
  <div class="schedule">
    <Select
      :model-value="choice"
      :options="options"
      option-label="label"
      option-value="value"
      :input-id="inputId"
      :invalid="invalid"
      @update:model-value="choose"
    />
    <template v-if="choice === CUSTOM">
      <InputNumber
        :model-value="amount"
        :min="1"
        :max="unit === 'days' ? MAX_BACKUP_HOURS / 24 : MAX_BACKUP_HOURS"
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
.schedule {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  align-items: center;
}
.amount :deep(input) {
  width: 6rem;
}
</style>
