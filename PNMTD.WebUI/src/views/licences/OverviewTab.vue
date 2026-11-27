<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed } from 'vue'
import { useRouter, type RouteLocationRaw } from 'vue-router'
import {
  licenseKeys,
  licensesApi,
  money,
  moneyLines,
  typeTabs,
  type LicenseOverview,
  type MoneyAmount,
} from '@/api/licenses'
import { monthLabel, monthLong, type CostColumn } from '@/components/charts/chartScale'
import CostColumns from '@/components/charts/CostColumns.vue'
import MeterList from '@/components/charts/MeterList.vue'
import RankedBars from '@/components/charts/RankedBars.vue'
import StatTile from '@/components/charts/StatTile.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { formatDay } from '@/format'
import { useSession } from '@/stores/session'
import MoneyFilters from './MoneyFilters.vue'
import { activeFilters, filterQuery, useUrlFilter } from './moneyFilter'
import VerdictTag from './VerdictTag.vue'

/**
 * The cost and licence overview (docs/licences-plan.md §8.2), from one call: the filter row above it all, kept in the URL; the
 * tiles (per month, per year, the next 12 months, spent this year, needs attention, cancel by), each a link to the list it sums;
 * the cost per month, 24 columns, the last 12 spent and the next 12 forecast, a click opening that month's costs; the running
 * cost per year by category and by provider, a click opening the costs so grouped; what needs attention, the seats counted on
 * hosts, *Worth a look* (§6.7) and the ends of life within a year. One currency per chart (§6.6); the charts follow §8.12.
 */
type LicenseBar = LicenseOverview['byCategory'][number]

const router = useRouter()
const session = useSession()
const edit = computed(() => session.can('Licenses', 'Edit'))

const filter = useUrlFilter()
const overview = useQuery({
  queryKey: computed(() => licenseKeys.overview(filter.value)),
  queryFn: ({ queryKey }) => licensesApi.overview(queryKey[2]),
  placeholderData: keepPreviousData,
})
const data = computed(() => overview.data.value)
// While other filters load, the last answer stays, dimmed: no flash, no jump.
const stale = computed(() => overview.isFetching.value && overview.isPlaceholderData.value)
const filtered = computed(() => activeFilters(filter.value) > 0)

// --- links -------------------------------------------------------------------------------------------------------------------
const kept = computed(() => filterQuery(filter.value))
const costs = (extra: Record<string, string> = {}): RouteLocationRaw => ({
  name: 'licences',
  params: { tab: 'costs' },
  query: { ...kept.value, ...extra },
})
/** The list of the type filtered by, else the licences. */
const list = (query: Record<string, string>): RouteLocationRaw => ({
  name: 'licences',
  params: { tab: filter.value.type ? typeTabs[filter.value.type] : 'licences' },
  query,
})
const item = (number: number): RouteLocationRaw => ({ name: 'licence', params: { number: String(number) } })

// --- the tiles ---------------------------------------------------------------------------------------------------------------
const main = (amounts: readonly MoneyAmount[], currency: string) =>
  amounts.find((a) => a.currency === currency)?.amount ?? 0
const tiles = computed(() => {
  const d = data.value
  if (!d) return null
  const c = d.currency
  const perMonth = moneyLines(d.perMonth, c)
  const perYear = moneyLines(d.perYear, c)
  const next = moneyLines(d.next12Months, c)
  const renewed = main(d.next12MonthsIfRenewed, c)
  const spent = moneyLines(d.spentThisYear, c)
  return {
    perMonth,
    perYear,
    next: { ...next, lines: [...(renewed ? [`of it if renewed ${money(renewed, c)}`] : []), ...next.others] },
    spent: { ...spent, lines: [`${d.year - 1}: ${money(main(d.spentLastYear, c), c)}`, ...spent.others] },
  }
})

// --- the charts ---------------------------------------------------------------------------------------------------------------
/** 24 months: the 12th is this one (the server counts from it), spent so far and forecast for its rest. */
const months = computed<CostColumn[]>(() =>
  (data.value?.months ?? []).map((m, i) => ({
    key: m.month,
    label: monthLabel(m.month, true),
    name: monthLong(m.month),
    spent: m.spent,
    forecast: m.forecast,
    ifRenewed: m.ifRenewed,
    // The server names a month's categories in the amount's currency field.
    top: m.topCategories.map((t) => ({ name: t.currency, amount: t.amount })),
    current: i === 11,
  })),
)
function openMonth(i: number) {
  const m = data.value?.months[i]
  if (m) void router.push(costs({ from: m.month, to: m.month }))
}

const isOther = (bar: LicenseBar) => bar.key === '' && bar.label.startsWith('Other providers')
const categoryLink = (bar: LicenseBar) => costs({ group: 'category', category: bar.key, open: bar.key })
const providerLink = (bar: LicenseBar) =>
  costs(isOther(bar) || !bar.key ? { group: 'provider' } : { group: 'provider', open: bar.key })

const seats = computed(() =>
  (data.value?.seats ?? []).map((s) => ({
    key: s.id,
    label: `${s.display} ${s.name}`,
    used: s.used,
    total: s.seats,
    unit: s.unit,
    to: item(s.number),
  })),
)

// --- Worth a look (§6.7) -------------------------------------------------------------------------------------------------------
const plural = (n: number, one: string, many: string) => (n === 1 ? one : many)
const worth = computed(() => {
  const d = data.value
  if (!d) return []
  const w = d.worthALook
  return [
    {
      key: 'unused',
      count: w.unusedSeats,
      text:
        `${w.unusedSeats} unused ${plural(w.unusedSeats, 'seat', 'seats')}` +
        (w.unusedSeatsPerYear ? ` · ${money(w.unusedSeatsPerYear, d.currency)} a year` : ''),
      to: list({ unusedSeats: '1' }),
    },
    {
      key: 'eol',
      count: w.paidPastEndOfLife,
      text: `${w.paidPastEndOfLife} paid past ${plural(w.paidPastEndOfLife, 'its', 'their')} end of life`,
      to: list({ pastEol: '1' }),
    },
    {
      key: 'nobody',
      count: w.nobodyNotified,
      text: `${w.nobodyNotified} nobody is notified about`,
      to: list({ nobody: '1' }),
    },
    {
      key: 'estimates',
      count: w.estimatesWithoutInvoice,
      text: `${w.estimatesWithoutInvoice} ${plural(w.estimatesWithoutInvoice, 'estimate', 'estimates')} without an invoice`,
      to: { name: 'licences', params: { tab: 'costs' }, query: { show: 'charges', estimated: 'true' } },
    },
    {
      key: 'price',
      count: w.withoutPrice,
      text: `${w.withoutPrice} without a price`,
      to: list({ noPrice: '1' }),
    },
    {
      key: 'reviews',
      count: w.reviewsOverdue,
      text: `${w.reviewsOverdue} ${plural(w.reviewsOverdue, 'review', 'reviews')} overdue`,
      to: list({ reviewDue: '1' }),
    },
  ]
})

const hostsText = (n: number) => (n === 0 ? 'on no host' : n === 1 ? '1 host' : `${n} hosts`)
</script>

<template>
  <div class="overview" data-test="licences-overview">
    <MoneyFilters v-model="filter" :currencies="data?.currencies ?? []" :currency="data?.currency ?? ''" />

    <ErrorState
      v-if="overview.isError.value"
      :error="overview.error.value"
      what="the overview"
      @retry="overview.refetch()"
    />
    <p v-else-if="!data" class="muted">Loading…</p>
    <EmptyState
      v-else-if="!data.items && filtered"
      icon="pi pi-filter"
      title="Nothing matches these filters."
      text="Change or clear the filters above."
    />
    <EmptyState
      v-else-if="!data.items"
      icon="pi pi-id-card"
      title="Nothing here yet: add the first licence or service, or make templates for the ones you buy most."
    >
      <div v-if="edit" class="empty-actions">
        <Button
          label="Add a licence"
          icon="pi pi-plus"
          size="small"
          @click="router.push({ name: 'licence-new', query: { type: 'License' } })"
        />
        <Button
          label="Templates"
          icon="pi pi-clone"
          size="small"
          severity="secondary"
          outlined
          @click="router.push({ name: 'licences', params: { tab: 'templates' } })"
        />
      </div>
    </EmptyState>

    <div v-else class="content" :class="{ stale }" :aria-busy="stale">
      <div v-if="tiles" class="tiles">
        <StatTile
          label="Per month"
          :value="tiles.perMonth.main"
          :lines="tiles.perMonth.others"
          :to="costs()"
          :link-label="`Per month: ${tiles.perMonth.main}. Open the costs`"
        />
        <StatTile
          label="Per year"
          :value="tiles.perYear.main"
          :lines="tiles.perYear.others"
          :to="costs()"
          :link-label="`Per year: ${tiles.perYear.main}. Open the costs`"
        />
        <StatTile
          label="Next 12 months"
          :value="tiles.next.main"
          :lines="tiles.next.lines"
          :to="costs({ from: data.months[12]?.month ?? '', to: data.months[23]?.month ?? '' })"
          :link-label="`Next 12 months: ${tiles.next.main}. Open the forecast`"
        />
        <StatTile
          :label="`Spent in ${data.year}`"
          :value="tiles.spent.main"
          :lines="tiles.spent.lines"
          :to="costs({ costs: 'spent' })"
          :link-label="`Spent in ${data.year}: ${tiles.spent.main}. Open the costs`"
        />
        <StatTile
          label="Needs attention"
          :value="String(data.needsAttention)"
          :to="list({ state: 'attention' })"
          :link-label="`Needs attention: ${data.needsAttention}, ${data.failing} failing. Open the list`"
        >
          <template #lines>
            <span v-if="data.failing" class="line fail">
              <i class="pi pi-times-circle" aria-hidden="true" /> {{ data.failing }} failing
            </span>
            <span v-else class="line muted">none failing</span>
          </template>
        </StatTile>
        <StatTile
          label="Cancel by"
          :value="String(data.cancelWithin90Days)"
          :lines="['within 90 days', ...(data.nextCancelBy ? [`the next on ${formatDay(data.nextCancelBy)}`] : [])]"
          :to="{ name: 'licences', params: { tab: 'upcoming' } }"
          :link-label="`Cancellation deadlines within 90 days: ${data.cancelWithin90Days}. Open Upcoming`"
        />
      </div>

      <section class="panel panel-body">
        <CostColumns
          :columns="months"
          :currency="data.currency"
          :title="`Cost per month · ${data.currency}`"
          selectable
          select-hint="Open its costs"
          @select="openMonth"
        />
      </section>

      <div class="pair">
        <section class="panel panel-body">
          <RankedBars
            :rows="data.byCategory"
            :currency="data.currency"
            :title="`Per year by category · ${data.currency}`"
            name-header="Category"
            per="a year"
            :link="categoryLink"
            link-hint="Open its costs"
            empty-text="Nothing running in this currency."
          />
        </section>
        <section class="panel panel-body">
          <RankedBars
            :rows="data.byProvider"
            :currency="data.currency"
            :title="`Per year by provider · ${data.currency}`"
            name-header="Provider"
            per="a year"
            :pinned="isOther"
            :link="providerLink"
            link-hint="Open the costs by provider"
            empty-text="Nothing running in this currency."
          />
        </section>
      </div>

      <div class="pair">
        <section class="panel panel-body" aria-labelledby="ov-attention">
          <h2 id="ov-attention" class="title">Needs attention</h2>
          <p v-if="!data.attention.length" class="muted none">Nothing needs attention.</p>
          <ul v-else class="rows">
            <li v-for="a in data.attention" :key="a.id" class="attention" data-test="attention-row">
              <VerdictTag :code="a.code" compact />
              <RouterLink :to="item(a.number)" class="ellipsis name"
                ><span class="muted">{{ a.display }}</span> {{ a.name }}</RouterLink
              >
              <span class="ellipsis muted finding" :title="a.text">{{ a.text }}</span>
              <span v-if="a.on" class="nowrap day">{{ formatDay(a.on) }}</span>
            </li>
          </ul>
          <RouterLink v-if="data.attentionTotal > data.attention.length" :to="list({ state: 'attention' })" class="all">
            All {{ data.attentionTotal }}
          </RouterLink>
        </section>
        <section class="panel panel-body" aria-labelledby="ov-seats">
          <h2 id="ov-seats" class="title">Seats counted on hosts</h2>
          <MeterList :rows="seats" empty-text="No item counts its seats on hosts." />
        </section>
      </div>

      <div class="pair">
        <section class="panel panel-body" aria-labelledby="ov-worth">
          <h2 id="ov-worth" class="title">Worth a look</h2>
          <ul class="rows">
            <li v-for="w in worth" :key="w.key" data-test="worth-a-look">
              <RouterLink v-if="w.count" :to="w.to">{{ w.text }}</RouterLink>
              <span v-else class="muted">{{ w.text }}</span>
            </li>
          </ul>
        </section>
        <section class="panel panel-body" aria-labelledby="ov-eol">
          <h2 id="ov-eol" class="title">Ends of life within a year</h2>
          <p v-if="!data.endsOfLife.length" class="muted none">No end of life within a year.</p>
          <ul v-else class="rows">
            <li v-for="e in data.endsOfLife" :key="e.id" class="eol" data-test="end-of-life">
              <RouterLink :to="item(e.number)" class="ellipsis name"
                ><span class="muted">{{ e.display }}</span> {{ e.name }}</RouterLink
              >
              <span class="nowrap">{{ formatDay(e.on) }}</span>
              <span class="nowrap muted">{{ hostsText(e.hosts) }}</span>
            </li>
          </ul>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overview,
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

.tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(10.5rem, 1fr));
  gap: var(--pnmt-space-3);
}

.line {
  font-size: 0.857rem;
}

.fail {
  color: var(--pnmt-fail);
}

.pair {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 26rem), 1fr));
  gap: var(--pnmt-space-3);
  align-items: start;
}

.title {
  font-size: 1rem;
  margin-bottom: var(--pnmt-space-2);
}

.none {
  margin: 0;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.attention,
.eol {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.attention .name {
  flex: 0 1 auto;
  max-width: 45%;
}

.attention .finding {
  flex: 1 1 0;
}

.attention .day {
  margin-left: auto;
  font-size: 0.857rem;
}

.eol .name {
  flex: 1 1 auto;
}

.all {
  display: inline-block;
  margin-top: var(--pnmt-space-2);
}

.empty-actions {
  display: flex;
  gap: var(--pnmt-space-2);
}
</style>
