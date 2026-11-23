<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import DatePicker from 'primevue/datepicker'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import { computed, ref, watch } from 'vue'
import type { S } from '@/api/resources'
import { parseDay } from '@/format'
import { fillDays, fillDaysErrors, formatPlanDate, typedDayForms, type FillDay } from './plan'

/**
 * *Fill again* with its options (docs/adjustments1.md): the future only, from a day to a day within the next 365; some people
 * or everyone; replacing only cells of some types; and, if asked for, what people set themselves. Empty cells are always
 * filled from each person's default week. The days can be picked or typed (docs/fill-again-typed-days-plan.md).
 */
const props = defineProps<{
  visible: boolean
  people: S['AvailabilityPersonItem'][]
  types: S['AvailabilityTypeItem'][]
  /** yyyy-MM-dd, the plan's today. */
  today: string
}>()
const emit = defineEmits<{ 'update:visible': [value: boolean]; fill: [body: S['AvailabilityFillRequest']] }>()

const parse = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y!, (m ?? 1) - 1, d ?? 1)
}
const iso = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const first = computed(() => (props.today ? parse(props.today) : new Date()))
const last = computed(() => {
  const d = new Date(first.value)
  d.setDate(d.getDate() + fillDays - 1)
  return d
})

/**
 * One day field: the day (the DatePicker's model), the text typed since the last read (null when the field shows the
 * day), and the text that could not be read as a day at the last read (§3.3).
 */
interface DayField {
  day: Date | null
  typed: string | null
  unread: string | null
}
const blank = (day: Date): DayField => ({ day, typed: null, unread: null })
const from = ref<DayField>(blank(new Date()))
const to = ref<DayField>(blank(new Date()))
const pickedPeople = ref<string[]>([])
const pickedTypes = ref<string[]>([])
const overwriteOwn = ref(false)

watch(
  () => props.visible,
  (open) => {
    if (open) {
      from.value = blank(first.value)
      to.value = blank(last.value)
      pickedPeople.value = []
      pickedTypes.value = []
      overwriteOwn.value = false
    }
  },
)

function typed(field: DayField, event: Event) {
  field.typed = (event.target as HTMLInputElement).value
}

function picked(field: DayField) {
  field.typed = null
  field.unread = null
}

/**
 * Reads what was typed (§3.3), on blur and before sending. DatePicker emits blur before it writes its last day back
 * into the field; setting the day here makes its watcher write the day read, in the normal form.
 */
function read(field: DayField) {
  if (field.typed === null) {
    return
  }
  const text = field.typed.trim()
  field.typed = null
  const day = text ? parseDay(text, first.value) : null
  field.day = day
  field.unread = text && !day ? text : null
}

const checked = (f: DayField): FillDay => ({ day: f.day ? iso(f.day) : null, unread: f.unread })
// The defaults are valid, so a message shows only after a day was typed and read, or picked.
const errors = computed(() => fillDaysErrors(checked(from.value), checked(to.value), iso(first.value)))
const hint = computed(() => `From today on, at most ${fillDays} days ahead: until ${formatPlanDate(iso(last.value))}.`)
const typeOptions = computed(() => props.types.map((t) => ({ label: `${t.code} · ${t.name}`, value: t.id })))
const peopleOptions = computed(() => props.people.map((p) => ({ label: `${p.code} · ${p.name}`, value: p.id })))

function submit() {
  read(from.value)
  read(to.value)
  if (errors.value.from || errors.value.to || !from.value.day || !to.value.day) {
    return
  }
  emit('fill', {
    again: true,
    from: iso(from.value.day),
    to: iso(to.value.day),
    people: pickedPeople.value.length ? pickedPeople.value : null,
    onlyTypes: pickedTypes.value.length ? pickedTypes.value : null,
    overwriteOwn: overwriteOwn.value,
  })
  emit('update:visible', false)
}
</script>

<template>
  <Dialog
    :visible="visible"
    modal
    header="Fill again"
    :style="{ width: '34rem' }"
    @update:visible="(v: boolean) => emit('update:visible', v)"
  >
    <form class="form" novalidate @submit.prevent="submit">
      <p class="muted">
        Writes the chosen days again from each person's default week. Empty working days are always filled; the past
        never.
      </p>
      <fieldset class="days">
        <legend>Days</legend>
        <div class="days-row">
          <div class="field">
            <label for="fill-from">From</label>
            <DatePicker
              v-model="from.day"
              input-id="fill-from"
              :min-date="first"
              :max-date="last"
              date-format="d.m.yy"
              show-icon
              fluid
              :invalid="!!errors.from"
              @input="typed(from, $event)"
              @blur="read(from)"
              @date-select="picked(from)"
            />
            <small v-if="errors.from" class="field-error">{{ errors.from }}</small>
          </div>
          <div class="field">
            <label for="fill-to">To</label>
            <DatePicker
              v-model="to.day"
              input-id="fill-to"
              :min-date="from.day && from.day > first ? from.day : first"
              :max-date="last"
              date-format="d.m.yy"
              show-icon
              fluid
              :invalid="!!errors.to"
              @input="typed(to, $event)"
              @blur="read(to)"
              @date-select="picked(to)"
            />
            <small v-if="errors.to" class="field-error">{{ errors.to }}</small>
          </div>
        </div>
        <small class="muted">{{ hint }} Type {{ typedDayForms }}</small>
      </fieldset>
      <div class="field">
        <label for="fill-people">People</label>
        <MultiSelect
          v-model="pickedPeople"
          input-id="fill-people"
          :options="peopleOptions"
          option-label="label"
          option-value="value"
          filter
          display="chip"
          placeholder="Everyone"
          fluid
        />
      </div>
      <div class="field">
        <label for="fill-types">Replace only these types</label>
        <MultiSelect
          v-model="pickedTypes"
          input-id="fill-types"
          :options="typeOptions"
          option-label="label"
          option-value="value"
          display="chip"
          placeholder="Every type"
          fluid
        />
        <small class="muted">For example only the office days, and leave holidays and illness as they are.</small>
      </div>
      <div class="check">
        <Checkbox v-model="overwriteOwn" input-id="fill-own" binary />
        <label for="fill-own">Also replace what people set themselves</label>
      </div>
      <Message v-if="overwriteOwn" severity="warn" :closable="false" size="small">
        People's own entries in these days are replaced by their default week.
      </Message>
      <div class="actions">
        <Button label="Cancel" text severity="secondary" type="button" @click="emit('update:visible', false)" />
        <Button type="submit" label="Fill again" />
      </div>
    </form>
  </Dialog>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.field {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.days {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  margin: 0;
  padding: 0;
  border: 0;
  min-width: 0;
}

.days legend {
  padding: 0;
  margin-bottom: var(--pnmt-space-1);
}

.days-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-2);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--pnmt-space-2);
}

p {
  margin: 0;
}
</style>
