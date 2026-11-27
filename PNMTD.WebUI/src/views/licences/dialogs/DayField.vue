<script setup lang="ts">
import DatePicker from 'primevue/datepicker'
import { computed } from 'vue'
import { dayDate, isoDay } from '@/api/licenses'

/**
 * A day as the API has it, yyyy-MM-dd (DateOnly), picked or typed in a DatePicker; null when cleared. The day the picker shows is
 * the day sent: no time zone shift.
 */
const model = defineModel<string | null | undefined>({ required: true })
defineProps<{ inputId: string; invalid?: boolean; placeholder?: string; clearable?: boolean }>()
const date = computed({
  get: () => dayDate(model.value),
  set: (value: Date | Date[] | (Date | null)[] | null | undefined) => {
    model.value = isoDay(value instanceof Date ? value : null)
  },
})
</script>

<template>
  <DatePicker
    v-model="date"
    :input-id="inputId"
    date-format="d M yy"
    show-icon
    icon-display="input"
    :show-button-bar="clearable"
    :invalid="invalid"
    :placeholder="placeholder"
    fluid
  />
</template>
