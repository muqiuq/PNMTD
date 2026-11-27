<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import { licenseKeys, licensesApi, typePlurals, type LicenseRow, type LicenseType } from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useSession } from '@/stores/session'
import CancelDialog from './dialogs/CancelDialog.vue'
import RenewDialog, { type RenewTarget } from './dialogs/RenewDialog.vue'
import ReviewDialog from './dialogs/ReviewDialog.vue'
import { collator, moneyList } from './licenceList'
import UpcomingEntry from './UpcomingEntry.vue'

/**
 * *Upcoming* (docs/licences-plan.md §8.4): what comes up in the next 3, 6, 12 or 24 months, overdue first, then month by month with
 * the month's forecast per currency; the header has the window's cost and its *if renewed* part. Billing days aren't listed. The
 * window, the type and the owner are in the URL (?months, ?type, ?owner). Edit renews, marks reviewed and cancels in place.
 */
const props = defineProps<{ edit: boolean }>()
const route = useRoute()
const router = useRouter()
const session = useSession()

const windows = [3, 6, 12, 24]
const types: LicenseType[] = ['License', 'Service', 'Hardware']
const text = (query: LocationQuery, key: string) => (typeof query[key] === 'string' ? (query[key] as string) : '')
const monthsOf = (query: LocationQuery) =>
  windows.includes(Number(text(query, 'months'))) ? Number(text(query, 'months')) : 6
const typeOf = (query: LocationQuery) => (types.find((t) => t === text(query, 'type')) ?? null) as LicenseType | null

const months = ref(monthsOf(route.query))
const type = ref<LicenseType | null>(typeOf(route.query))
const owner = ref<string | null>(text(route.query, 'owner') || null)
watch([months, type, owner], () => {
  const query = { ...route.query, months: months.value === 6 ? undefined : String(months.value) }
  Object.assign(query, { type: type.value ?? undefined, owner: owner.value ?? undefined })
  void router.replace({ query })
})
// The tab's link (or a link here) sets the URL: the filters follow.
watch(
  () => route.query,
  (query) => {
    if (route.params.tab !== 'upcoming') return
    if (monthsOf(query) !== months.value) months.value = monthsOf(query)
    if (typeOf(query) !== type.value) type.value = typeOf(query)
    if ((text(query, 'owner') || null) !== owner.value) owner.value = text(query, 'owner') || null
  },
)

const upcoming = useQuery({
  queryKey: computed(() => licenseKeys.upcoming(months.value, type.value ?? undefined, owner.value ?? undefined)),
  queryFn: () =>
    licensesApi.upcoming({ months: months.value, type: type.value ?? undefined, owner: owner.value ?? undefined }),
  placeholderData: keepPreviousData,
})
const people = useQuery({ queryKey: licenseKeys.people, queryFn: licensesApi.people, staleTime: 60_000 })
// The rows give the dialogs what they need (the term, the price, the status): the same query as the tabs' badges.
const rows = useQuery({
  queryKey: licenseKeys.list(),
  queryFn: () => licensesApi.list(),
  staleTime: 30_000,
  enabled: computed(() => props.edit),
})
const byId = computed(() => new Map((rows.data.value ?? []).map((r) => [r.id, r])))

const windowOptions = windows.map((m) => ({ value: m, label: String(m) }))
const typeOptions = types.map((t) => ({ value: t, label: typePlurals[t] }))
const ownerOptions = computed(() => {
  const me = session.user?.id
  return [...(people.data.value ?? [])]
    .sort((a, b) => (a.id === me ? -1 : b.id === me ? 1 : collator.compare(a.name, b.name)))
    .map((p) => ({ value: p.id, label: p.id === me ? `${p.name} (me)` : p.name }))
})

const data = computed(() => upcoming.data.value)
const nothing = computed(
  () => !!data.value && !data.value.overdue.length && !data.value.byMonth.some((m) => m.entries.length),
)
const monthFormat = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' })
const monthLabel = (month: string) => {
  const [y, m] = month.split('-').map(Number)
  return monthFormat.format(new Date(y, m - 1, 1, 12))
}

// The dialogs stay mounted, so each opening presets its fields from the item it gets.
const renewing = ref(false)
const reviewing = ref(false)
const cancelling = ref(false)
const target = ref<LicenseRow | null>(null)
const renewItem = computed<RenewTarget>(() => {
  const r = target.value
  return {
    id: r?.id ?? '',
    display: r?.display ?? '',
    name: r?.name ?? '',
    expiresOn: r?.expiresOn ?? null,
    termMonths: r?.termMonths ?? null,
    priceAmount: r?.priceAmount ?? null,
    currency: r?.currency ?? '',
    status: r?.status ?? 'Active',
  }
})
const cancelItem = computed(() => ({
  id: target.value?.id ?? '',
  display: target.value?.display ?? '',
  name: target.value?.name ?? '',
  expiresOn: target.value?.expiresOn ?? null,
}))
const reviewIds = computed(() => (target.value ? [target.value.id] : []))
const reviewLabel = computed(() => (target.value ? `${target.value.display} ${target.value.name}` : ''))
function act(row: LicenseRow, dialog: 'renew' | 'review' | 'cancel') {
  target.value = row
  if (dialog === 'renew') renewing.value = true
  else if (dialog === 'review') reviewing.value = true
  else cancelling.value = true
}
</script>

<template>
  <div class="upcoming">
    <div class="toolbar">
      <span class="muted">Next</span>
      <SelectButton
        v-model="months"
        :options="windowOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Months"
        data-test="upcoming-months"
      />
      <span class="muted">months</span>
      <Select
        v-model="type"
        :options="typeOptions"
        option-label="label"
        option-value="value"
        placeholder="Any type"
        show-clear
        size="small"
        aria-label="Type"
      />
      <Select
        v-model="owner"
        :options="ownerOptions"
        option-label="label"
        option-value="value"
        placeholder="Any owner"
        show-clear
        filter
        size="small"
        aria-label="Owner"
      />
      <div v-if="data" class="cost" data-test="upcoming-cost">
        <div>
          Cost in this time: <strong>{{ data.cost.length ? moneyList(data.cost) : 'nothing forecast' }}</strong>
        </div>
        <div v-if="data.ifRenewed.length" class="muted small">of it if renewed: {{ moneyList(data.ifRenewed) }}</div>
      </div>
    </div>

    <ErrorState
      v-if="upcoming.isError.value && !data"
      :error="upcoming.error.value"
      what="what comes up"
      @retry="upcoming.refetch()"
    />
    <p v-else-if="!data" class="muted">Loading…</p>
    <template v-else>
      <section v-if="data.overdue.length" class="panel" aria-labelledby="upcoming-overdue" data-test="upcoming-overdue">
        <h3 id="upcoming-overdue" class="panel-header">Overdue</h3>
        <ul class="entries">
          <UpcomingEntry
            v-for="(e, i) in data.overdue"
            :key="`${e.id}-${e.what}-${i}`"
            :entry="e"
            :row="byId.get(e.id)"
            overdue
            :edit="edit"
            @renew="(r) => act(r, 'renew')"
            @review="(r) => act(r, 'review')"
            @cancel="(r) => act(r, 'cancel')"
          />
        </ul>
      </section>
      <EmptyState
        v-if="nothing"
        icon="pi pi-calendar"
        :title="`Nothing comes up in the next ${months} months.`"
        text="Expiries, renewals, cancellation deadlines, warranty and lease ends, ends of life and reviews show here."
      />
      <section v-for="m in data.byMonth" :key="m.month" class="panel month" :aria-label="monthLabel(m.month)">
        <h3 class="panel-header">
          <span>{{ monthLabel(m.month) }}</span>
          <span v-if="!m.entries.length" class="muted quiet">nothing comes up</span>
          <span class="month-cost">{{ m.cost.length ? moneyList(m.cost) : '' }}</span>
        </h3>
        <ul v-if="m.entries.length" class="entries">
          <UpcomingEntry
            v-for="(e, i) in m.entries"
            :key="`${e.id}-${e.what}-${e.day}-${i}`"
            :entry="e"
            :row="byId.get(e.id)"
            :edit="edit"
            @renew="(r) => act(r, 'renew')"
            @review="(r) => act(r, 'review')"
            @cancel="(r) => act(r, 'cancel')"
          />
        </ul>
      </section>
    </template>

    <RenewDialog v-model:visible="renewing" :item="renewItem" />
    <ReviewDialog v-model:visible="reviewing" :ids="reviewIds" :label="reviewLabel" />
    <CancelDialog v-model:visible="cancelling" :item="cancelItem" />
  </div>
</template>

<style scoped>
.upcoming {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.cost {
  margin-left: auto;
  text-align: right;
}

.small {
  font-size: 0.857rem;
}

.panel-header {
  margin: 0;
  font-size: 1rem;
}

.quiet {
  font-weight: 400;
}

.month-cost {
  margin-left: auto;
  font-weight: 500;
}

.entries {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-1) 0;
}
</style>
