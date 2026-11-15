<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import DatePicker from 'primevue/datepicker'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import { computed, ref, watch } from 'vue'
import type { S } from '@/api/resources'

/**
 * *Fill again* with its options (docs/adjustments1.md): the future only, from a day to a day within the next 365; some people
 * or everyone; replacing only cells of some types; and, if asked for, what people set themselves. Empty cells are always
 * filled from each person's default week.
 */
const props = defineProps<{
  visible: boolean
  people: S['AvailabilityPersonItem'][]
  types: S['AvailabilityTypeItem'][]
  /** yyyy-MM-dd, the plan's today. */
  today: string
}>()
const emit = defineEmits<{ 'update:visible': [value: boolean]; fill: [body: S['AvailabilityFillRequest']] }>()

const DAYS = 365
const parse = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y!, (m ?? 1) - 1, d ?? 1)
}
const iso = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const first = computed(() => (props.today ? parse(props.today) : new Date()))
const last = computed(() => {
  const d = new Date(first.value)
  d.setDate(d.getDate() + DAYS - 1)
  return d
})

const range = ref<(Date | null)[] | null>(null)
const pickedPeople = ref<string[]>([])
const pickedTypes = ref<string[]>([])
const overwriteOwn = ref(false)

watch(
  () => props.visible,
  (open) => {
    if (open) {
      range.value = [first.value, last.value]
      pickedPeople.value = []
      pickedTypes.value = []
      overwriteOwn.value = false
    }
  },
)

const from = computed(() => range.value?.[0] ?? null)
const to = computed(() => range.value?.[1] ?? range.value?.[0] ?? null)
const typeOptions = computed(() => props.types.map((t) => ({ label: `${t.code} · ${t.name}`, value: t.id })))
const peopleOptions = computed(() => props.people.map((p) => ({ label: `${p.code} · ${p.name}`, value: p.id })))

function submit() {
  if (!from.value || !to.value) return
  emit('fill', {
    again: true,
    from: iso(from.value),
    to: iso(to.value),
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
      <div class="field">
        <label for="fill-days">Days</label>
        <DatePicker
          v-model="range"
          input-id="fill-days"
          selection-mode="range"
          :manual-input="false"
          :min-date="first"
          :max-date="last"
          date-format="d.m.yy"
          show-icon
          fluid
        />
        <small class="muted">From today on, at most {{ DAYS }} days ahead.</small>
      </div>
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
        <Button type="submit" label="Fill again" :disabled="!from || !to" />
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
