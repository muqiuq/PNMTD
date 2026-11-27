<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { licenseKeys, licensesApi, typeLabels, type LicenseType, type MoneyFilter } from '@/api/licenses'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { filterNames, type FilterName } from './moneyFilter'

/**
 * The filter row of the money views (docs/licences-plan.md §8.2, §8.5): one row above everything it scopes, kept in the URL by
 * the tab (moneyFilter.ts). Currency (the response's, the main one first: one currency per chart, §6.6), type, category, cost
 * centre, location and owner. The location needs Locations: View to pick one; `fields` leaves out what a view can't filter by.
 */
const model = defineModel<MoneyFilter>({ required: true })
const props = withDefaults(
  defineProps<{
    /** The currencies there are, the main one first; the picker shows while there is a choice. */
    currencies?: readonly string[]
    /** The currency shown (the response's), for the picker's value without ?currency. */
    currency?: string
    fields?: readonly FilterName[]
  }>(),
  { currencies: () => [], currency: '', fields: () => filterNames },
)
const session = useSession()
const has = (name: FilterName) => props.fields.includes(name)

const categories = useQuery({
  queryKey: licenseKeys.categories,
  queryFn: () => licensesApi.categories(),
  staleTime: 60_000,
  enabled: computed(() => has('category')),
})
const people = useQuery({
  queryKey: licenseKeys.people,
  queryFn: () => licensesApi.people(),
  staleTime: 60_000,
  enabled: computed(() => has('owner')),
})

const typeOptions = (Object.keys(typeLabels) as LicenseType[]).map((t) => ({ value: t, label: typeLabels[t] }))
const categoryOptions = computed(() => (categories.data.value ?? []).map((c) => ({ value: c.id, label: c.name })))
const ownerOptions = computed(() => (people.data.value ?? []).map((p) => ({ value: p.id, label: p.name })))
const currencyOptions = computed(() => {
  const list = [...props.currencies]
  const asked = model.value.currency
  if (asked && !list.includes(asked)) list.push(asked)
  return list
})
const showCurrency = computed(() => has('currency') && (currencyOptions.value.length > 1 || !!model.value.currency))
const locations = computed(() => has('location') && session.can('Locations', 'View'))

function set<K extends keyof MoneyFilter>(name: K, value: MoneyFilter[K] | null | undefined) {
  model.value = { ...model.value, [name]: value ?? undefined }
}

// The cost centre is typed: written to the URL once the typing pauses.
const costCenter = ref(model.value.costCenter ?? '')
watch(
  () => model.value.costCenter,
  (value) => {
    if ((value ?? '') !== costCenter.value.trim()) costCenter.value = value ?? ''
  },
)
let timer: ReturnType<typeof setTimeout> | undefined
watch(costCenter, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    const text = value.trim() || undefined
    if (text !== model.value.costCenter) set('costCenter', text)
  }, 400)
})
onBeforeUnmount(() => clearTimeout(timer))

// Clearing clears what this row filters by; the currency stays (one is always shown).
const clearable = computed(() => props.fields.filter((n) => n !== 'currency'))
const active = computed(() => clearable.value.filter((n) => !!model.value[n]).length)
function clear() {
  const next = { ...model.value }
  for (const name of clearable.value) delete next[name]
  costCenter.value = ''
  model.value = next
}
</script>

<template>
  <div class="money-filters" role="group" aria-label="Filters" data-test="money-filters">
    <Select
      v-if="showCurrency"
      :model-value="model.currency ?? currency"
      :options="currencyOptions"
      aria-label="Currency"
      title="Currency: one per chart and table, never converted"
      size="small"
      class="currency"
      data-test="filter-currency"
      @update:model-value="(v: string) => set('currency', v)"
    >
      <template #value="{ value }">
        <span><span class="muted">Currency</span> {{ value }}</span>
      </template>
    </Select>
    <Select
      v-if="has('type')"
      :model-value="model.type ?? null"
      :options="typeOptions"
      option-label="label"
      option-value="value"
      placeholder="All types"
      aria-label="Type"
      show-clear
      size="small"
      data-test="filter-type"
      @update:model-value="(v: LicenseType | null) => set('type', v)"
    />
    <Select
      v-if="has('category')"
      :model-value="model.category ?? null"
      :options="categoryOptions"
      option-label="label"
      option-value="value"
      placeholder="All categories"
      aria-label="Category"
      show-clear
      :filter="categoryOptions.length > 8"
      :loading="categories.isPending.value"
      size="small"
      data-test="filter-category"
      @update:model-value="(v: string | null) => set('category', v)"
    />
    <InputText
      v-if="has('costCenter')"
      v-model="costCenter"
      placeholder="Cost centre"
      aria-label="Cost centre"
      size="small"
      class="cost-center"
      data-test="filter-cost-center"
    />
    <LocationPicker
      v-if="locations"
      :model-value="model.location ?? null"
      none-label="All locations"
      aria-label="Location"
      size="small"
      @update:model-value="(v: string | null) => set('location', v)"
    />
    <Select
      v-if="has('owner')"
      :model-value="model.owner ?? null"
      :options="ownerOptions"
      option-label="label"
      option-value="value"
      placeholder="All owners"
      aria-label="Owner"
      show-clear
      filter
      :loading="people.isPending.value"
      size="small"
      data-test="filter-owner"
      @update:model-value="(v: string | null) => set('owner', v)"
    />
    <Button
      v-if="active"
      :label="active === 1 ? 'Clear the filter' : `Clear ${active} filters`"
      icon="pi pi-filter-slash"
      size="small"
      text
      @click="clear"
    />
    <slot />
  </div>
</template>

<style scoped>
.money-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.money-filters > :deep(.p-select) {
  min-width: 9rem;
}

.cost-center {
  width: 9rem;
}
</style>
