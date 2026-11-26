<script setup lang="ts">
import DatePicker from 'primevue/datepicker'
import { computed, ref } from 'vue'
import { parseDay } from '@/format'
import { typedDayForms } from './plan'

/**
 * *Go to a day* beside *Today* (docs/availability-extensions-plan.md §5): a day picked in the calendar, or typed as in *Fill
 * again* (3.11.2027, 3.11.27 or 3.11.) and taken with Enter. As there (docs/fill-again-typed-days-plan.md), the calendar opens
 * from its button, not on focus, and DatePicker's blur comes before it writes its own day back, so what was typed is read
 * first.
 */
const props = defineProps<{ /** yyyy-MM-dd, the plan's today: a typed 3.11. is in its year. */ today: string }>()
const emit = defineEmits<{ go: [day: string] }>()

const parse = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y!, (m ?? 1) - 1, d ?? 1)
}
const iso = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const base = computed(() => (props.today ? parse(props.today) : new Date()))

const day = ref<Date | null>(null)
const typedText = ref<string | null>(null)
const unread = ref<string | null>(null)

function typed(event: Event) {
  typedText.value = (event.target as HTMLInputElement).value
  unread.value = null
}

/** Reads what was typed: the day, or the text that isn't one. True when a day is there. */
function read(): boolean {
  if (typedText.value !== null) {
    const text = typedText.value.trim()
    typedText.value = null
    const parsed = text ? parseDay(text, base.value) : null
    day.value = parsed
    unread.value = text && !parsed ? text : null
  }
  return day.value !== null && unread.value === null
}

function enter() {
  if (read() && day.value) emit('go', iso(day.value))
}

function picked() {
  typedText.value = null
  unread.value = null
  if (day.value) emit('go', iso(day.value))
}
</script>

<template>
  <span class="go">
    <DatePicker
      v-model="day"
      input-id="plan-go-to"
      date-format="d.m.yy"
      show-icon
      :show-on-focus="false"
      placeholder="Go to a day"
      size="small"
      :invalid="!!unread"
      :pt="{ pcInputText: { root: { 'aria-label': 'Go to a day', title: `Type ${typedDayForms}, then Enter` } } }"
      class="go-picker"
      @input="typed"
      @blur="read"
      @keydown.enter.prevent="enter"
      @date-select="picked"
    />
    <small v-if="unread" class="field-error" role="alert">"{{ unread }}" isn't a day: type {{ typedDayForms }}</small>
  </span>
</template>

<style scoped>
.go {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.go-picker {
  width: 10.5rem;
}
</style>
