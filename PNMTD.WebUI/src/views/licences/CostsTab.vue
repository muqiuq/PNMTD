<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import DatePicker from 'primevue/datepicker'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter, type LocationQueryRaw, type RouteLocationRaw } from 'vue-router'
import { licenseKeys, licensesApi, money, todayIso, type LicenseCostLine, type LicenseRow } from '@/api/licenses'
import { monthLabel, monthLong, spansYears, type CostColumn } from '@/components/charts/chartScale'
import CostColumns from '@/components/charts/CostColumns.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useNow } from '@/composables/useNow'
import ChargesView from './ChargesView.vue'
import ChargeDialog from './dialogs/ChargeDialog.vue'
import MoneyFilters from './MoneyFilters.vue'
import {
  addMonths,
  costsQuery,
  maxMonths,
  monthsBetween,
  periodQuery,
  presetWindow,
  readCostsView,
  readPeriod,
  useUrlFilter,
  type CostGroup,
  type CostsShow,
  type PeriodPreset,
} from './moneyFilter'

/**
 * The Costs tab (docs/licences-plan.md §8.5): the costs by category, type, provider, manufacturer, cost centre, location, host,
 * owner or item (§6.5), per month of a period or per year of the last five, spent, forecast or both (§6.4), in one currency
 * (§6.6), with the overview's filters; all of it in the URL. The columns above are the period's totals (§8.12); the table opens
 * each group to its items, shades and marks the forecast columns ▨, and sorts by any column. *Per year now* is the running cost
 * (§6.1) beside what the period cost. CSV of the same; *Record a cost…* (Edit) picks the item first. `?show=charges` is the
 * Charges view (ChargesView.vue).
 */
const props = defineProps<{ edit: boolean }>()
const route = useRoute()
const router = useRouter()
const now = useNow()
const today = computed(() => todayIso(now.value))

const filter = useUrlFilter()
const view = computed(() => readCostsView(route.query))
const period = computed(() => readPeriod(route.query, today.value))
const query = computed(() => costsQuery(period.value, view.value, filter.value))
const costs = useQuery({
  queryKey: computed(() => licenseKeys.costs(query.value)),
  queryFn: ({ queryKey }) => licensesApi.costs(queryKey[2]),
  placeholderData: keepPreviousData,
  enabled: computed(() => !view.value.charges),
})
const data = computed(() => costs.data.value)
const stale = computed(() => costs.isFetching.value && costs.isPlaceholderData.value)

/** Changes the URL's query: a value set, undefined removed. */
function update(patch: Record<string, string | undefined>) {
  const next: LocationQueryRaw = { ...route.query }
  for (const [key, value] of Object.entries(patch)) {
    if (value) next[key] = value
    else delete next[key]
  }
  void router.replace({ query: next })
}

// --- the controls ----------------------------------------------------------------------------------------------------------
const viewOptions = [
  { value: 'costs', label: 'Costs' },
  { value: 'charges', label: 'Charges' },
]
const viewValue = computed({
  get: () => (view.value.charges ? 'charges' : 'costs'),
  set: (value: string) => update({ show: value === 'charges' ? 'charges' : undefined }),
})

const year = computed(() => Number(today.value.slice(0, 4)))
const presetOptions = computed<{ value: PeriodPreset; label: string }[]>(() => [
  { value: 'thisYear', label: `This year (${year.value})` },
  { value: 'lastYear', label: `Last year (${year.value - 1})` },
  { value: 'last12', label: 'The last 12 months' },
  { value: 'next12', label: 'The next 12 months' },
  { value: 'range', label: 'Months from … to …' },
  { value: 'years', label: 'The last five years' },
])
// A range that matches a preset stays a range while it is being picked.
const ranging = ref(period.value.preset === 'range')
watch(
  () => period.value.preset,
  (preset) => {
    if (preset === 'range') ranging.value = true
    if (preset === 'years') ranging.value = false
  },
)
const presetValue = computed<PeriodPreset>(() => (ranging.value && !period.value.years ? 'range' : period.value.preset))
function pickPreset(preset: PeriodPreset) {
  if (preset === 'range') {
    ranging.value = true
    update({ years: undefined, from: period.value.from, to: period.value.to })
    return
  }
  ranging.value = false
  const window = preset === 'years' ? { from: '', to: '' } : presetWindow(preset, today.value)
  const q = periodQuery({ preset, ...window })
  update({ years: q.years, from: q.from, to: q.to })
}

const monthDate = (month: string) => new Date(Number(month.slice(0, 4)), Number(month.slice(5, 7)) - 1, 1, 12)
const monthKey = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
const fromDate = computed(() => monthDate(period.value.from))
const toDate = computed(() => monthDate(period.value.to))
/** A month picked: the other end follows, so that the window runs forward and is at most 60 months. */
function pickMonth(end: 'from' | 'to', value: Date | Date[] | (Date | null)[] | null | undefined) {
  if (!(value instanceof Date)) return
  let from = end === 'from' ? monthKey(value) : period.value.from
  let to = end === 'to' ? monthKey(value) : period.value.to
  if (to < from) {
    if (end === 'from') to = from
    else from = to
  }
  if (monthsBetween(from, to) > maxMonths) {
    if (end === 'from') to = addMonths(from, maxMonths - 1)
    else from = addMonths(to, -(maxMonths - 1))
  }
  update({ years: undefined, from, to })
}

const groupLabels: Record<CostGroup, string> = {
  category: 'Category',
  type: 'Type',
  provider: 'Provider',
  manufacturer: 'Manufacturer',
  costCenter: 'Cost centre',
  location: 'Location',
  host: 'Host',
  owner: 'Owner',
  item: 'Item',
}
const groupOptions = (Object.keys(groupLabels) as CostGroup[]).map((g) => ({ value: g, label: groupLabels[g] }))
const showOptions: { value: CostsShow; label: string }[] = [
  { value: 'both', label: 'Spent and forecast' },
  { value: 'spent', label: 'Spent' },
  { value: 'forecast', label: 'Forecast' },
]

// --- the columns -----------------------------------------------------------------------------------------------------------
const years = computed(
  () => (data.value?.columns ?? []).every((c) => /^\d{4}$/.test(c)) && !!data.value?.columns.length,
)
const current = computed(() => {
  const key = years.value ? today.value.slice(0, 4) : today.value.slice(0, 7)
  return data.value?.columns.indexOf(key) ?? -1
})
const withYear = computed(() => spansYears(data.value?.columns ?? []))
const heads = computed(() =>
  (data.value?.columns ?? []).map((key, i) => ({
    key,
    label: monthLabel(key, withYear.value),
    name: monthLong(key),
    forecast: data.value!.show !== 'spent' && !!data.value!.forecast[i],
  })),
)
/** The period's name: 2026, Mar–Nov 2026, Dec 25–Nov 26, 2022–2026. */
const periodLabel = computed(() => {
  const columns = data.value?.columns ?? []
  const first = columns[0] ?? ''
  const last = columns[columns.length - 1] ?? ''
  if (years.value) return `${first}–${last}`
  if (first === last) return monthLong(first)
  if (first.slice(0, 4) === last.slice(0, 4))
    return first.endsWith('-01') && last.endsWith('-12')
      ? first.slice(0, 4)
      : `${monthLabel(first, false)}–${monthLabel(last, false)} ${first.slice(0, 4)}`
  return `${monthLabel(first, true)}–${monthLabel(last, true)}`
})

/**
 * The chart: what each column holds. Spent and forecast come as one total per column; with both, the past is spent, what is to
 * come forecast, and the current month (or year) both, not told apart.
 */
const chart = computed<CostColumn[]>(() => {
  const d = data.value
  if (!d) return []
  return d.columns.map((key, i) => {
    const total = d.totals[i] ?? 0
    const base = { key, label: heads.value[i]?.label ?? key, name: monthLong(key), current: i === current.value }
    if (d.show === 'spent') return { ...base, spent: total, forecast: 0 }
    if (d.show === 'forecast') return { ...base, spent: 0, forecast: total }
    return d.forecast[i]
      ? { ...base, spent: 0, forecast: total, mixed: i === current.value }
      : { ...base, spent: total, forecast: 0 }
  })
})

// --- the table: sorting and opening ---------------------------------------------------------------------------------------
type SortKey = 'label' | 'total' | 'perYear' | number
const sort = ref<{ key: SortKey; order: 1 | -1 } | null>(null)
watch(
  () => data.value?.columns.join(','),
  () => {
    if (typeof sort.value?.key === 'number') sort.value = null
  },
)
/** A click sorts (names A–Z, figures largest first), a second turns it round, a third goes back to the server's order. */
function sortBy(key: SortKey) {
  const first = key === 'label' ? 1 : -1
  if (!sort.value || sort.value.key !== key) sort.value = { key, order: first }
  else if (sort.value.order === first) sort.value = { key, order: first === 1 ? -1 : 1 }
  else sort.value = null
}
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })
function sorted(lines: readonly LicenseCostLine[]): LicenseCostLine[] {
  const s = sort.value
  if (!s) return [...lines]
  const pick = (l: LicenseCostLine) =>
    s.key === 'label'
      ? l.label
      : s.key === 'total'
        ? l.total
        : s.key === 'perYear'
          ? l.perYearNow
          : (l.values[s.key] ?? 0)
  return [...lines].sort((a, b) => {
    const x = pick(a)
    const y = pick(b)
    const order = typeof x === 'string' && typeof y === 'string' ? collator.compare(x, y) : Number(x) - Number(y)
    return order * s.order
  })
}
const groups = computed(() => (data.value?.groups ?? []).map((g) => ({ ...g, items: sorted(g.items) })))
const rows = computed(() => sorted(groups.value) as (LicenseCostLine & { items: LicenseCostLine[] })[])
const ariaSort = (key: SortKey) =>
  sort.value?.key === key ? (sort.value.order === 1 ? 'ascending' : 'descending') : ('none' as const)
const sortIcon = (key: SortKey) =>
  sort.value?.key === key ? (sort.value.order === 1 ? 'pi pi-sort-up-fill' : 'pi pi-sort-down-fill') : 'pi pi-sort-alt'

const expanded = ref(new Set<string>())
watch(
  () => [view.value.group, view.value.open] as const,
  ([, open]) => (expanded.value = new Set(open ? [open] : [])),
  { immediate: true },
)
function toggle(key: string) {
  const next = new Set(expanded.value)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  expanded.value = next
}
const allOpen = computed(() => rows.value.filter((g) => g.items.length).every((g) => expanded.value.has(g.key)))
function toggleAll() {
  expanded.value = allOpen.value ? new Set() : new Set(rows.value.filter((g) => g.items.length).map((g) => g.key))
}

const whole = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 0 })
const exact = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const cell = (v: number) => (v === 0 ? '–' : whole.format(v))
/** LIC-12 from "LIC-12" or "LIC-12 Microsoft 365": the item's page. */
function itemLink(text: string | null | undefined): RouteLocationRaw | null {
  const m = text ? /^[A-Z]+-(\d+)\b/.exec(text) : null
  return m ? { name: 'licence', params: { number: m[1] } } : null
}
function showCurrency(currency: string) {
  filter.value = { ...filter.value, currency }
}

// --- Record a cost… (Edit): the item first, then the charge -------------------------------------------------------------------
const items = useQuery({
  queryKey: licenseKeys.list(),
  queryFn: () => licensesApi.list(),
  staleTime: 30_000,
  enabled: computed(() => props.edit),
})
const itemOptions = computed(() =>
  (items.data.value ?? [])
    .filter((i) => i.status !== 'Retired')
    .map((i) => ({ value: i.id, label: `${i.display} ${i.name}` })),
)
const picking = ref(false)
const picked = ref<string | null>(null)
const recordFor = ref<LicenseRow | null>(null)
const recording = ref(false)
function startRecording() {
  picked.value = null
  picking.value = true
}
function pickItem() {
  const row = items.data.value?.find((i) => i.id === picked.value)
  if (!row) return
  recordFor.value = row
  picking.value = false
  recording.value = true
}
</script>

<template>
  <div class="costs-tab" data-test="licences-costs">
    <ChargesView v-if="view.charges" :edit="edit">
      <SelectButton
        v-model="viewValue"
        :options="viewOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Costs or charges"
        data-test="costs-view"
      />
      <Button
        v-if="edit"
        label="Record a cost…"
        icon="pi pi-plus"
        size="small"
        data-test="record-cost"
        @click="startRecording"
      />
    </ChargesView>

    <template v-else>
      <MoneyFilters v-model="filter" :currencies="data?.currencies ?? []" :currency="data?.currency ?? ''" />
      <div class="controls" role="group" aria-label="What the costs show">
        <Select
          :model-value="presetValue"
          :options="presetOptions"
          option-label="label"
          option-value="value"
          aria-label="Period"
          size="small"
          data-test="costs-period"
          @update:model-value="pickPreset"
        />
        <template v-if="presetValue === 'range'">
          <DatePicker
            :model-value="fromDate"
            view="month"
            date-format="M yy"
            input-id="costs-from"
            aria-label="From the month"
            size="small"
            class="month"
            @update:model-value="(v) => pickMonth('from', v)"
          />
          <span class="muted">to</span>
          <DatePicker
            :model-value="toDate"
            view="month"
            date-format="M yy"
            input-id="costs-to"
            aria-label="To the month"
            size="small"
            class="month"
            @update:model-value="(v) => pickMonth('to', v)"
          />
        </template>
        <Select
          :model-value="view.group"
          :options="groupOptions"
          option-label="label"
          option-value="value"
          aria-label="Group by"
          size="small"
          data-test="costs-group"
          @update:model-value="(v: CostGroup) => update({ group: v === 'category' ? undefined : v, open: undefined })"
        >
          <template #value="{ value }">
            <span><span class="muted">Group by</span> {{ groupLabels[value as CostGroup] }}</span>
          </template>
        </Select>
        <Select
          :model-value="view.show"
          :options="showOptions"
          option-label="label"
          option-value="value"
          aria-label="Show"
          size="small"
          data-test="costs-show"
          @update:model-value="(v: CostsShow) => update({ costs: v === 'both' ? undefined : v })"
        />
        <div class="actions">
          <SelectButton
            v-model="viewValue"
            :options="viewOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-label="Costs or charges"
            data-test="costs-view"
          />
          <Button
            as="a"
            :href="licensesApi.costsExportUrl(query)"
            download
            label="CSV"
            icon="pi pi-download"
            size="small"
            text
            data-test="costs-csv"
          />
          <Button
            v-if="edit"
            label="Record a cost…"
            icon="pi pi-plus"
            size="small"
            data-test="record-cost"
            @click="startRecording"
          />
        </div>
      </div>

      <ErrorState v-if="costs.isError.value" :error="costs.error.value" what="the costs" @retry="costs.refetch()" />
      <p v-else-if="!data" class="muted">Loading…</p>
      <div v-else class="content" :class="{ stale }" :aria-busy="stale">
        <section class="panel panel-body">
          <CostColumns
            :columns="chart"
            :currency="data.currency"
            :title="`Cost per ${years ? 'year' : 'month'} · ${data.currency}`"
            :unit="years ? 'Year' : 'Month'"
          />
        </section>

        <EmptyState
          v-if="!rows.length"
          icon="pi pi-wallet"
          :title="`No costs in ${data.currency} in this period.`"
          text="Pick another period, currency or filter, or record a cost."
        />
        <template v-else>
          <div class="table-wrap panel">
            <table class="costs-table" data-test="costs-table">
              <caption class="sr-only">
                {{
                  `Costs by ${groupLabels[view.group].toLowerCase()} in ${data.currency}, ${periodLabel}`
                }}
              </caption>
              <thead>
                <tr>
                  <th scope="col" class="name" :aria-sort="ariaSort('label')">
                    <button v-if="rows.some((g) => g.items.length)" type="button" class="expand-all" @click="toggleAll">
                      <i :class="allOpen ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
                      <span class="sr-only">{{ allOpen ? 'Close all' : 'Open all' }}</span>
                    </button>
                    <button type="button" class="sort" @click="sortBy('label')">
                      {{ groupLabels[view.group] }} <i :class="sortIcon('label')" aria-hidden="true" />
                    </button>
                  </th>
                  <th
                    v-for="(h, i) in heads"
                    :key="h.key"
                    scope="col"
                    class="num"
                    :class="{ fc: h.forecast, now: i === current }"
                    :aria-sort="ariaSort(i)"
                  >
                    <button
                      type="button"
                      class="sort"
                      :title="h.forecast ? `${h.name}: forecast` : h.name"
                      @click="sortBy(i)"
                    >
                      {{ h.label }}<span v-if="h.forecast" aria-hidden="true"> ▨</span
                      ><span v-if="h.forecast" class="sr-only">, forecast</span>
                      <i :class="sortIcon(i)" aria-hidden="true" />
                    </button>
                  </th>
                  <th scope="col" class="num" :aria-sort="ariaSort('total')">
                    <button type="button" class="sort" title="What the period cost" @click="sortBy('total')">
                      {{ periodLabel }} <i :class="sortIcon('total')" aria-hidden="true" />
                    </button>
                  </th>
                  <th scope="col" class="num" :aria-sort="ariaSort('perYear')">
                    <button
                      type="button"
                      class="sort"
                      title="The running cost of what is active today, a year"
                      @click="sortBy('perYear')"
                    >
                      Per year now <i :class="sortIcon('perYear')" aria-hidden="true" />
                    </button>
                  </th>
                </tr>
              </thead>
              <tbody>
                <template v-for="g in rows" :key="`g:${g.key}`">
                  <tr class="group" data-test="costs-group-row">
                    <th scope="row" class="name">
                      <button
                        v-if="g.items.length"
                        type="button"
                        class="expand"
                        :aria-expanded="expanded.has(g.key)"
                        :aria-label="`${expanded.has(g.key) ? 'Close' : 'Open'} ${g.label}`"
                        @click="toggle(g.key)"
                      >
                        <i
                          :class="expanded.has(g.key) ? 'pi pi-chevron-down' : 'pi pi-chevron-right'"
                          aria-hidden="true"
                        />
                      </button>
                      <span v-else class="expand-space" />
                      <RouterLink v-if="view.group === 'item' && itemLink(g.label)" :to="itemLink(g.label)!">{{
                        g.label
                      }}</RouterLink>
                      <span v-else>{{ g.label }}</span>
                      <span
                        v-if="g.items.length"
                        class="muted count"
                        :title="g.items.length === 1 ? '1 item' : `${g.items.length} items`"
                        >{{ g.items.length
                        }}<span class="sr-only"> {{ g.items.length === 1 ? 'item' : 'items' }}</span></span
                      >
                    </th>
                    <td
                      v-for="(v, i) in g.values"
                      :key="i"
                      class="num"
                      :class="{ fc: heads[i]?.forecast, zero: v === 0 }"
                      :title="money(v, data.currency)"
                    >
                      {{ cell(v) }}
                    </td>
                    <td class="num strong">{{ exact.format(g.total) }}</td>
                    <td class="num">{{ exact.format(g.perYearNow) }}</td>
                  </tr>
                  <template v-if="expanded.has(g.key)">
                    <tr v-for="it in g.items" :key="`i:${g.key}:${it.key}`" class="child">
                      <th scope="row" class="name">
                        <RouterLink v-if="itemLink(it.display)" :to="itemLink(it.display)!"
                          ><span class="muted">{{ it.display }}</span> {{ it.label }}</RouterLink
                        >
                        <span v-else>{{ it.label }}</span>
                      </th>
                      <td
                        v-for="(v, i) in it.values"
                        :key="i"
                        class="num"
                        :class="{ fc: heads[i]?.forecast, zero: v === 0 }"
                        :title="money(v, data.currency)"
                      >
                        {{ cell(v) }}
                      </td>
                      <td class="num">{{ exact.format(it.total) }}</td>
                      <td class="num">{{ exact.format(it.perYearNow) }}</td>
                    </tr>
                  </template>
                </template>
              </tbody>
              <tfoot>
                <tr>
                  <th scope="row" class="name">Total</th>
                  <td
                    v-for="(v, i) in data.totals"
                    :key="i"
                    class="num"
                    :class="{ fc: heads[i]?.forecast }"
                    :title="money(v, data.currency)"
                  >
                    {{ cell(v) }}
                  </td>
                  <td class="num">{{ exact.format(data.total) }}</td>
                  <td class="num">{{ exact.format(data.perYearNow) }}</td>
                </tr>
              </tfoot>
            </table>
          </div>
          <p class="note muted" data-test="costs-note">
            <span v-if="heads.some((h) => h.forecast)"><span aria-hidden="true">▨ </span>forecast</span>
            <span v-if="data.ifRenewed">of it if renewed: {{ money(data.ifRenewed, data.currency) }}</span>
            <span v-for="o in data.otherCurrencies" :key="o.currency">
              Also in {{ o.currency }}: {{ exact.format(o.amount) }} in {{ periodLabel }}
              <Button
                label="Show"
                link
                size="small"
                class="show"
                :aria-label="`Show the costs in ${o.currency}`"
                @click="showCurrency(o.currency)"
              />
            </span>
            <span v-if="data.sharedByHosts">shared by the hosts it is on today</span>
          </p>
        </template>
      </div>
    </template>

    <Dialog v-model:visible="picking" header="Record a cost" modal :style="{ width: '28rem' }">
      <form id="record-cost-pick" class="form" @submit.prevent="pickItem">
        <div class="field">
          <label for="record-cost-item">For which licence or service?</label>
          <Select
            v-model="picked"
            input-id="record-cost-item"
            :options="itemOptions"
            option-label="label"
            option-value="value"
            filter
            auto-filter-focus
            placeholder="Pick one"
            :loading="items.isPending.value"
            fluid
            data-test="record-cost-item"
          />
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="picking = false" />
        <Button type="submit" form="record-cost-pick" label="Next" :disabled="!picked" data-test="record-cost-next" />
      </template>
    </Dialog>
    <!-- Mounted before it opens: the dialog fills its fields when it becomes visible. -->
    <ChargeDialog
      v-if="edit"
      v-model:visible="recording"
      :license-id="recordFor?.id ?? ''"
      :display="recordFor?.display ?? ''"
      :currency="recordFor?.currency ?? ''"
    />
  </div>
</template>

<style scoped>
.costs-tab,
.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content {
  transition: opacity 0.15s;
}

.content.stale {
  opacity: 0.6;
}

.controls {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.month {
  width: 8.5rem;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-left: auto;
}

.table-wrap {
  overflow-x: auto;
}

.costs-table {
  border-collapse: separate;
  border-spacing: 0;
  width: 100%;
  font-size: 0.929rem;
}

.costs-table th,
.costs-table td {
  padding: 4px var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  white-space: nowrap;
}

.costs-table thead th {
  color: var(--pnmt-muted);
  font-weight: 500;
  text-align: left;
}

.costs-table .num {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.costs-table thead th.num .sort {
  justify-content: flex-end;
}

/* The names stay while the months scroll. */
.costs-table .name {
  position: sticky;
  left: 0;
  z-index: 1;
  background: var(--pnmt-panel);
  text-align: left;
  font-weight: 400;
  min-width: 14rem;
  max-width: 24rem;
  white-space: normal;
}

.costs-table thead th.now .sort {
  color: var(--pnmt-text);
  font-weight: 600;
}

/* The forecast columns: a wash of the forecast's own step, and ▨ in the header. */
.costs-table .fc {
  background: color-mix(in srgb, var(--pnmt-chart-seq-3) 12%, transparent);
}

.costs-table .zero {
  color: var(--pnmt-muted);
}

.group .name {
  font-weight: 500;
}

.child .name {
  padding-left: calc(var(--pnmt-space-2) + 1.75rem);
}

.child th,
.child td {
  font-size: 0.893rem;
}

.strong {
  font-weight: 600;
}

.costs-table tfoot th,
.costs-table tfoot td {
  font-weight: 600;
  border-bottom: none;
  border-top: 2px solid var(--pnmt-border);
}

.sort {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  width: 100%;
  padding: 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.sort i {
  font-size: 0.75rem;
  opacity: 0.6;
}

.name .sort {
  width: auto;
}

.expand,
.expand-all {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.5rem;
  height: 1.5rem;
  margin-right: var(--pnmt-space-1);
  padding: 0;
  border: 0;
  border-radius: var(--p-border-radius-sm);
  background: none;
  color: var(--pnmt-muted);
  cursor: pointer;
  vertical-align: middle;
}

.expand:hover,
.expand-all:hover {
  background: color-mix(in srgb, var(--pnmt-text) 8%, transparent);
}

.expand i,
.expand-all i {
  font-size: 0.75rem;
}

.expand-space {
  display: inline-block;
  width: 1.5rem;
  margin-right: var(--pnmt-space-1);
}

.count {
  margin-left: var(--pnmt-space-2);
  font-size: 0.857rem;
}

.note {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0 var(--pnmt-space-1);
  margin: 0;
  font-size: 0.929rem;
}

.note > span + span::before {
  content: '· ';
}

.show {
  padding: 0 var(--pnmt-space-1);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
