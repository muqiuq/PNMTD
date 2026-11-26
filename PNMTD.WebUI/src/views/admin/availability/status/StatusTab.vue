<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import CodeChip from '../CodeChip.vue'
import { formatPlanDate } from '../plan'
import MonthColumns from './MonthColumns.vue'
import ShareDeviation, { type ShareRow } from './ShareDeviation.vue'
import {
  awayList,
  byMonth,
  heatStep,
  measureChoices,
  monthShort,
  openWeekdays,
  percent,
  responsibleByWeekday,
  shortDay,
  sum,
  take,
  toCsv,
  typesInUse,
  typesOf,
  weekdayAverages,
  windowOf,
  yearCalendar,
  type Measure,
  type Scope,
  type WindowKey,
} from './stats'
import WeekdayColumns from './WeekdayColumns.vue'
import YearCalendar from './YearCalendar.vue'

/**
 * The plan's Status tab (docs/availability-extensions-plan.md §9, §10), for whoever sees the plan: a row of numbers, the days
 * as responsible against each person's share, the days per person by type (per year or month, with CSV), the team by month,
 * the office day by day and by weekday, and who is away in the next 14 days. Open days only; the people in the plan now; a
 * day as responsible is the day as planned. The year is kept in the URL (?year=).
 */
const route = useRoute()
const router = useRouter()

const asYear = (v: unknown) => (typeof v === 'string' && /^\d{4}$/.test(v) ? Number(v) : null)
const year = ref<number | null>(asYear(route.query.year))
watch(year, (y) => {
  const rest = { ...route.query }
  delete rest.year
  void router.replace({ query: y ? { ...rest, year: String(y) } : rest })
})

const stats = useQuery({
  queryKey: computed(() => [...keys.availability, 'stats', year.value ?? 'this']),
  queryFn: () => api.availability.stats(year.value ?? undefined),
  placeholderData: keepPreviousData,
  staleTime: 60_000,
})
const s = computed(() => stats.data.value)
const label = computed(() => s.value?.responsibleLabel || 'Responsible')
const thisYear = computed(() => Number((s.value?.today ?? '').slice(0, 4)) || new Date().getFullYear())
const shown = computed(() => s.value?.year ?? thisYear.value)
const isCurrent = computed(() => shown.value === thisYear.value)
const yearOptions = computed(() => {
  const first = Math.min(s.value?.firstYear ?? thisYear.value, thisYear.value)
  return Array.from({ length: thisYear.value + 1 - first + 1 }, (_, i) => thisYear.value + 1 - i).map((y) => ({
    label: String(y),
    value: y,
  }))
})
const pickedYear = computed({
  get: () => shown.value,
  set: (y: number) => (year.value = y === thisYear.value ? null : y),
})

/** Until today or the whole year: only this year has both; a past year is done, a year to come planned. */
const scopeChoice = ref<Scope>('done')
const scope = computed<Scope>(() => (isCurrent.value ? scopeChoice.value : 'all'))
const scopes = [
  { label: 'Until today', value: 'done' },
  { label: 'Whole year', value: 'all' },
]

const allowed = computed(() => !!s.value?.ahead)
const people = computed(() => s.value?.people ?? [])
const types = computed(() => (s.value ? typesOf(s.value) : new Map()))
const used = computed(() => (s.value ? typesInUse(s.value, scope.value) : []))

const openPlan = (day: string) => void router.push({ name: 'availability', params: { tab: 'plan' }, query: { day } })

// --- 1. the numbers ----------------------------------------------------------------------------------------------------
const teamSum = (measure: Measure) =>
  people.value.reduce((n, p) => n + (s.value ? sum(s.value, p.id, measure, 'all', 'year', types.value) : 0), 0)
const teamDone = (measure: Measure) =>
  people.value.reduce((n, p) => n + (s.value ? sum(s.value, p.id, measure, 'done', 'year', types.value) : 0), 0)
const vacationDone = computed(() => teamDone({ kind: 'flag', flag: 'Vacation' }))
const vacationPlanned = computed(() => teamSum({ kind: 'flag', flag: 'Vacation' }) - vacationDone.value)
const onSiteShare = computed(() => {
  if (!s.value) return null
  let onSite = 0
  let working = 0
  for (const m of s.value.months)
    for (const t of m.types) {
      const type = types.value.get(t.typeId)
      if (!type?.works) continue
      working += take(t, scope.value)
      if (type.flags.includes('OnSite')) onSite += take(t, scope.value)
    }
  return working ? onSite / working : null
})
const tiles = computed(() => {
  const a = s.value?.ahead
  if (!a) return []
  return [
    {
      key: 'without',
      value: a.withoutResponsible,
      text: `Days without a working ${label.value}, next 30 days`,
      first: a.firstWithoutResponsible,
      warn: a.withoutResponsible > 0,
    },
    {
      key: 'below',
      value: a.belowMinimum,
      text: 'Days below a minimum, next 30 days',
      first: a.firstBelowMinimum,
      warn: a.belowMinimum > 0,
    },
    {
      key: 'empty',
      value: a.emptyCells,
      text: 'Empty cells to set, next 30 days',
      first: a.firstEmpty,
      warn: a.emptyCells > 0,
    },
    {
      key: 'vacation',
      value: vacationDone.value,
      text: `Vacation days ${shown.value}, the team's${vacationPlanned.value ? `, and ${vacationPlanned.value} planned` : ''}`,
      first: null,
      warn: false,
    },
    {
      key: 'onsite',
      value: onSiteShare.value === null ? '–' : percent(onSiteShare.value),
      text: `Of the working days ${shown.value} on site${scope.value === 'done' ? ', until today' : ''}`,
      first: null,
      warn: false,
    },
  ]
})

// --- 2. days as responsible against the share --------------------------------------------------------------------------
const windowKey = ref<WindowKey>('year')
const windowChoices = computed(() => [
  { label: `${shown.value}${isCurrent.value && scope.value === 'done' ? ' until today' : ''}`, value: 'year' },
  { label: 'Last 365 days', value: 'last365' },
  { label: 'Last 30 days', value: 'last30' },
])
const shareWindow = computed(() => (s.value ? windowOf(s.value, windowKey.value, scope.value) : undefined))
const shareRows = computed<ShareRow[]>(() => {
  const w = shareWindow.value
  if (!w) return []
  const byId = new Map(people.value.map((p) => [p.id, p]))
  return w.people.flatMap((r) => {
    const p = byId.get(r.userId)
    return p ? [{ ...r, name: p.name, code: p.code, color: p.color ?? null }] : []
  })
})

// --- 3. by weekday -----------------------------------------------------------------------------------------------------
const weekdays = computed(() => (s.value ? openWeekdays(s.value) : []))
const byWeekday = computed(() =>
  s.value ? responsibleByWeekday(s.value, scope.value) : new Map<string, Map<number, number>>(),
)
const weekdayMax = computed(() => Math.max(0, ...[...byWeekday.value.values()].flatMap((m) => [...m.values()])))

// --- 4. days per person ------------------------------------------------------------------------------------------------
const period = ref<number | 'year'>('year')
const periods = computed(() => [
  { label: `${shown.value}`, value: 'year' as const },
  ...monthShort.map((m, i) => ({ label: `${m} ${shown.value}`, value: i + 1 })),
])
const months = computed(() => (period.value === 'year' ? 'year' : [period.value]) as number[] | 'year')
const extra = computed<{ key: string; label: string; measure: Measure }[]>(() => [
  { key: 'works', label: 'Works', measure: { kind: 'works' } },
  { key: 'responsible', label: label.value, measure: { kind: 'responsible' } },
  { key: 'fallback', label: 'Fallback', measure: { kind: 'fallback' } },
  { key: 'empty', label: 'Empty', measure: { kind: 'empty' } },
])
const cell = (userId: string, measure: Measure) =>
  s.value ? sum(s.value, userId, measure, scope.value, months.value, types.value) : 0
const total = (measure: Measure) => people.value.reduce((n, p) => n + cell(p.id, measure), 0)
/** The thin bar beside a person's figures: their days by type, each as a share of their typed days. */
function split(userId: string) {
  const parts = used.value
    .map((t) => ({ t, days: cell(userId, { kind: 'type', typeId: t.id }) }))
    .filter((p) => p.days > 0)
  const all = parts.reduce((n, p) => n + p.days, 0)
  return parts.map((p) => ({ ...p, share: all ? (p.days / all) * 100 : 0 }))
}

function download(name: string, rows: (string | number)[][]) {
  const url = URL.createObjectURL(new Blob([toCsv(rows)], { type: 'text/csv;charset=utf-8' }))
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1_000)
}
function downloadPeople() {
  const head = ['Person', 'Code', ...used.value.map((t) => t.code), ...extra.value.map((e) => e.label)]
  const rows = people.value.map((p) => [
    p.name,
    p.code,
    ...used.value.map((t) => cell(p.id, { kind: 'type', typeId: t.id })),
    ...extra.value.map((e) => cell(p.id, e.measure)),
  ])
  const when = period.value === 'year' ? `${shown.value}` : `${shown.value}-${String(period.value).padStart(2, '0')}`
  download(`availability-${when}${scope.value === 'done' && isCurrent.value ? '-until-today' : ''}.csv`, [
    head,
    ...rows,
  ])
}

// --- 5. the team by month ------------------------------------------------------------------------------------------------
const monthPerson = ref<string | null>(null)
const personChoices = computed(() => [
  { label: 'Everyone', value: null as string | null },
  ...people.value.map((p) => ({ label: `${p.code} · ${p.name}`, value: p.id as string | null })),
])
const monthData = computed(() => (s.value ? byMonth(s.value, scope.value, monthPerson.value) : []))
const currentMonth = computed(() => (isCurrent.value && s.value ? Number(s.value.today.slice(5, 7)) : null))

// --- 6. by month ---------------------------------------------------------------------------------------------------------
const choices = computed(() => (s.value ? measureChoices(s.value) : []))
const measureKey = ref<string>('')
watch(
  choices,
  (list) => {
    if (!list.some((c) => c.key === measureKey.value))
      measureKey.value =
        list.find((c) => c.measure.kind === 'flag' && c.measure.flag === 'Vacation')?.key ?? list[0]?.key ?? ''
  },
  { immediate: true },
)
const measure = computed(() => choices.value.find((c) => c.key === measureKey.value))
const monthCell = (userId: string, month: number) =>
  s.value && measure.value ? sum(s.value, userId, measure.value.measure, scope.value, [month], types.value) : 0
const monthTotal = (userId: string) =>
  s.value && measure.value ? sum(s.value, userId, measure.value.measure, scope.value, 'year', types.value) : 0
function downloadMonths() {
  if (!measure.value) return
  const head = ['Person', 'Code', ...monthShort, 'Total']
  const rows = people.value.map((p) => [
    p.name,
    p.code,
    ...monthShort.map((_, i) => monthCell(p.id, i + 1)),
    monthTotal(p.id),
  ])
  download(`availability-${shown.value}-${measure.value.label.replace(/[^a-z0-9]+/gi, '-').toLowerCase()}.csv`, [
    head,
    ...rows,
  ])
}

// --- 7, 8. the office ----------------------------------------------------------------------------------------------------
const columnIndex = ref(0)
const columnChoices = computed(() => (s.value?.columns ?? []).map((c, i) => ({ label: c.name, value: i })))
const column = computed(() => s.value?.columns[columnIndex.value])
const calendar = computed(() => (s.value && column.value ? yearCalendar(s.value, columnIndex.value) : null))
const averages = computed(() =>
  s.value && column.value ? weekdayAverages(s.value, columnIndex.value, scope.value) : [],
)

// --- 9. who is away ------------------------------------------------------------------------------------------------------
const away = computed(() => (s.value ? awayList(s.value) : []))
const personOf = (id: string) => people.value.find((p) => p.id === id)
</script>

<template>
  <ErrorState v-if="stats.isError.value && !s" :error="stats.error.value" what="the status" @retry="stats.refetch()" />
  <div v-else-if="!s" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <section v-else-if="!allowed" class="panel">
    <EmptyState
      icon="pi pi-chart-bar"
      title="The plan isn't yours to see."
      text="You aren't in the availability plan, and seeing it needs Availability: View."
    />
  </section>
  <div v-else class="status">
    <div class="filters">
      <label class="filter">
        <span class="muted small">Year</span>
        <Select
          v-model="pickedYear"
          :options="yearOptions"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Year"
        />
      </label>
      <SelectButton
        v-if="isCurrent"
        v-model="scopeChoice"
        :options="scopes"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Until today, or the whole year with what is planned"
      />
      <span v-else class="muted small">{{
        shown < thisYear ? 'A past year: all of it done.' : 'A year to come: all of it planned.'
      }}</span>
      <i v-if="stats.isFetching.value" class="pi pi-spin pi-spinner muted" aria-label="Loading" />
      <span class="muted small end">Open days only, in {{ s.timeZone }}. Today is {{ formatPlanDate(s.today) }}.</span>
    </div>

    <p v-if="!people.length" class="muted">Nobody is in the plan yet.</p>

    <!-- 1. the numbers -->
    <div class="tiles">
      <component
        :is="t.first ? 'button' : 'div'"
        v-for="t in tiles"
        :key="t.key"
        class="tile"
        :class="{ warn: t.warn, link: !!t.first }"
        :type="t.first ? 'button' : undefined"
        :title="t.first ? `The first: ${formatPlanDate(t.first)}. A click opens it in the plan.` : undefined"
        :data-testid="`tile-${t.key}`"
        @click="t.first && openPlan(t.first)"
      >
        <span class="tile-value">
          <i v-if="t.warn" class="pi pi-exclamation-triangle" aria-hidden="true" />
          {{ t.value }}
        </span>
        <span class="tile-text muted small">{{ t.text }}</span>
      </component>
    </div>

    <!-- 2. days as responsible against the share -->
    <section class="panel" aria-labelledby="share-title">
      <div id="share-title" class="panel-header">
        Days as {{ label }}, against each share
        <SelectButton
          v-model="windowKey"
          :options="windowChoices"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="end"
          aria-label="Which days"
        />
      </div>
      <div class="panel-body body">
        <p class="muted small intro">
          A day as {{ label }} is an open day the plan names the person for, as planned. Each person's target is their
          share (People tab) of everyone's; the bar shows how many days they are ahead of it or behind.
        </p>
        <p v-if="!shareWindow || shareWindow.total === 0" class="muted">Nobody was {{ label }} in this window yet.</p>
        <ShareDeviation v-else :rows="shareRows" :label="label" />
        <p v-if="shareWindow" class="muted small foot">
          {{ shareWindow.total }} of {{ shareWindow.openDays }} open days with a {{ label }} in the plan;
          {{ shareWindow.withoutResponsible }} without.
          <template v-if="shareWindow.others">
            {{ shareWindow.others }} {{ shareWindow.others === 1 ? 'day' : 'days' }} of people no longer in the plan,
            not compared.
          </template>
          {{ formatPlanDate(shareWindow.from) }} to {{ formatPlanDate(shareWindow.to) }}.
        </p>
      </div>
    </section>

    <!-- 3. by weekday -->
    <section class="panel" aria-labelledby="weekday-title">
      <div id="weekday-title" class="panel-header">Days as {{ label }} by weekday, {{ shown }}</div>
      <div class="panel-body">
        <table class="table compact">
          <thead>
            <tr>
              <th scope="col">Person</th>
              <th v-for="w in weekdays" :key="w.day" scope="col" class="num">{{ w.label }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="p in people" :key="p.id">
              <th scope="row" class="who"><CodeChip :code="p.code" :colour="p.color" /> {{ p.name }}</th>
              <td
                v-for="w in weekdays"
                :key="w.day"
                class="num heat"
                :class="`step-${heatStep(byWeekday.get(p.id)?.get(w.day) ?? 0, weekdayMax)}`"
              >
                {{ byWeekday.get(p.id)?.get(w.day) ?? 0 }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <!-- 4. days per person -->
    <section class="panel" aria-labelledby="people-title">
      <div id="people-title" class="panel-header">
        Days per person
        <span class="header-tools end">
          <Select
            v-model="period"
            :options="periods"
            option-label="label"
            option-value="value"
            size="small"
            aria-label="The year or a month"
          />
          <Button label="CSV" icon="pi pi-download" size="small" severity="secondary" text @click="downloadPeople" />
        </span>
      </div>
      <div class="panel-body wide">
        <table class="table">
          <thead>
            <tr>
              <th scope="col">Person</th>
              <th v-for="t in used" :key="t.id" scope="col" class="num" :title="t.name">
                <CodeChip :code="t.code" :colour="t.color" />
              </th>
              <th v-for="e in extra" :key="e.key" scope="col" class="num">{{ e.label }}</th>
              <th scope="col" class="split-head"><span class="sr-only">By type</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="p in people" :key="p.id">
              <th scope="row" class="who"><CodeChip :code="p.code" :colour="p.color" /> {{ p.name }}</th>
              <td v-for="t in used" :key="t.id" class="num">{{ cell(p.id, { kind: 'type', typeId: t.id }) }}</td>
              <td v-for="e in extra" :key="e.key" class="num">{{ cell(p.id, e.measure) }}</td>
              <td class="split-cell">
                <span class="split" aria-hidden="true">
                  <span
                    v-for="part in split(p.id)"
                    :key="part.t.id"
                    class="split-part"
                    :style="{ width: `${part.share}%`, background: part.t.color }"
                    :title="`${part.t.code}: ${part.days}`"
                  />
                </span>
              </td>
            </tr>
          </tbody>
          <tfoot>
            <tr>
              <th scope="row">Everyone</th>
              <td v-for="t in used" :key="t.id" class="num">{{ total({ kind: 'type', typeId: t.id }) }}</td>
              <td v-for="e in extra" :key="e.key" class="num">{{ total(e.measure) }}</td>
              <td />
            </tr>
          </tfoot>
        </table>
        <p v-if="!used.length" class="muted small">No days set in this period.</p>
      </div>
    </section>

    <!-- 5. the team by month -->
    <section class="panel" aria-labelledby="months-title">
      <div id="months-title" class="panel-header">
        The team by month, {{ shown }}
        <Select
          v-model="monthPerson"
          :options="personChoices"
          option-label="label"
          option-value="value"
          size="small"
          class="end"
          aria-label="Everyone or one person"
        />
      </div>
      <div class="panel-body">
        <MonthColumns :months="monthData" :types="used" :current="currentMonth" />
      </div>
    </section>

    <!-- 6. by month -->
    <section class="panel" aria-labelledby="bymonth-title">
      <div id="bymonth-title" class="panel-header">
        By month, {{ shown }}
        <span class="header-tools end">
          <Select
            v-model="measureKey"
            :options="choices"
            option-label="label"
            option-value="key"
            size="small"
            filter
            aria-label="What to count"
          />
          <Button label="CSV" icon="pi pi-download" size="small" severity="secondary" text @click="downloadMonths" />
        </span>
      </div>
      <div class="panel-body wide">
        <table class="table">
          <thead>
            <tr>
              <th scope="col">Person</th>
              <th v-for="m in monthShort" :key="m" scope="col" class="num">{{ m }}</th>
              <th scope="col" class="num">Total</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="p in people" :key="p.id">
              <th scope="row" class="who"><CodeChip :code="p.code" :colour="p.color" /> {{ p.name }}</th>
              <td v-for="(m, i) in monthShort" :key="m" class="num" :class="{ zero: !monthCell(p.id, i + 1) }">
                {{ monthCell(p.id, i + 1) }}
              </td>
              <td class="num strong">{{ monthTotal(p.id) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <!-- 7, 8. the office -->
    <section v-if="column" class="panel" aria-labelledby="office-title">
      <div id="office-title" class="panel-header">
        The office, {{ shown }}
        <Select
          v-model="columnIndex"
          :options="columnChoices"
          option-label="label"
          option-value="value"
          size="small"
          class="end"
          aria-label="Which count column"
        />
      </div>
      <div class="panel-body office">
        <div>
          <h3 class="sub">Day by day: {{ column.name }}</h3>
          <YearCalendar
            v-if="calendar"
            :weekdays="calendar.weekdays"
            :weeks="calendar.weeks"
            :column="column.name"
            :minimum="column.minimum"
            @open="openPlan"
          />
        </div>
        <div>
          <h3 class="sub">By weekday, on average</h3>
          <WeekdayColumns :values="averages" :column="column.name" :minimum="column.minimum" />
        </div>
      </div>
    </section>

    <!-- 9. who is away -->
    <section class="panel" aria-labelledby="away-title">
      <div id="away-title" class="panel-header">Away in the next 14 days</div>
      <div class="panel-body">
        <p v-if="!away.length" class="muted">Nobody: no vacation, holiday or compensation in the plan.</p>
        <ul v-else class="away">
          <li v-for="a in away" :key="a.userId" class="away-row">
            <span class="who">
              <CodeChip :code="personOf(a.userId)?.code" :colour="personOf(a.userId)?.color" />
              {{ personOf(a.userId)?.name }}
            </span>
            <span class="away-days">
              <button
                v-for="d in a.days"
                :key="d.date"
                type="button"
                class="away-day"
                :title="`${types.get(d.typeId)?.name ?? ''} on ${formatPlanDate(d.date)}`"
                @click="openPlan(d.date)"
              >
                <CodeChip :code="types.get(d.typeId)?.code" :colour="types.get(d.typeId)?.color" />
                <span class="small">{{ shortDay(d.date) }}</span>
              </button>
            </span>
          </li>
        </ul>
      </div>
    </section>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.status {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 1400px;
}

.filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-3);
}

.filter {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.end {
  margin-left: auto;
}

.tiles {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr));
  gap: var(--pnmt-space-2);
}

.tile {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  color: inherit;
  font: inherit;
  text-align: left;
}

.tile.link {
  cursor: pointer;
}

.tile.link:hover {
  border-color: var(--p-primary-color);
}

.tile-value {
  font-size: 1.857rem;
  font-weight: 600;
  line-height: 1.1;
  font-variant-numeric: tabular-nums;
}

.tile.warn .tile-value i {
  color: var(--pnmt-warn);
  font-size: 1.1rem;
  vertical-align: middle;
}

.panel-header {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.header-tools {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.intro,
.foot {
  margin: 0;
  max-width: 100ch;
}

.wide {
  overflow-x: auto;
}

.table {
  border-collapse: collapse;
  font-size: 0.929rem;
}

.table th,
.table td {
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  text-align: left;
  white-space: nowrap;
}

.table thead th {
  font-size: 0.857rem;
  font-weight: 600;
}

.table tfoot th,
.table tfoot td {
  font-weight: 600;
  border-bottom: none;
}

.num {
  text-align: right !important;
  font-variant-numeric: tabular-nums;
}

.zero {
  color: var(--pnmt-muted);
}

.strong {
  font-weight: 600;
}

.who {
  font-weight: 500;
}

.heat {
  min-width: 2.6rem;
}

.heat.step-1 {
  background: color-mix(in srgb, var(--pnmt-chart-seq-1) 70%, transparent);
}

.heat.step-2 {
  background: color-mix(in srgb, var(--pnmt-chart-seq-2) 70%, transparent);
}

.heat.step-3 {
  background: color-mix(in srgb, var(--pnmt-chart-seq-3) 70%, transparent);
}

.heat.step-4 {
  background: color-mix(in srgb, var(--pnmt-chart-seq-4) 70%, transparent);
}

.heat.step-5,
.heat.step-6,
.heat.step-7 {
  background: color-mix(in srgb, var(--pnmt-chart-seq-5) 75%, transparent);
  color: #ffffff;
}

.split-head,
.split-cell {
  width: 12rem;
}

.split {
  display: flex;
  gap: 2px;
  width: 12rem;
  height: 10px;
}

.split-part {
  display: block;
  height: 100%;
  border-radius: 2px;
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--pnmt-text) 30%, transparent);
}

.office {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);
  gap: var(--pnmt-space-4);
}

.sub {
  margin: 0 0 var(--pnmt-space-2);
  font-size: 0.929rem;
  font-weight: 600;
}

.away {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.away-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
}

.away-row .who {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 14rem;
}

.away-days {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.away-day {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 4px;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  background: transparent;
  color: inherit;
  cursor: pointer;
}

.small {
  font-size: 0.857rem;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

@media (max-width: 1000px) {
  .office {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
