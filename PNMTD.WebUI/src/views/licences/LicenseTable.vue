<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import MultiSelect from 'primevue/multiselect'
import Tag from 'primevue/tag'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  daysLeftText,
  licenseKeys,
  licensesApi,
  todayIso,
  typeLabels,
  typePlurals,
  type LicenseRow,
  type LicenseStatus,
  type LicenseType,
} from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import { useMedia } from '@/composables/useMedia'
import { useNow } from '@/composables/useNow'
import { formatDay } from '@/format'
import { useSession } from '@/stores/session'
import ReviewDialog from './dialogs/ReviewDialog.vue'
import ListCell from './ListCell.vue'
import ListChips from './ListChips.vue'
import ListFilters from './ListFilters.vue'
import VerdictTag from './VerdictTag.vue'
import {
  cleared,
  columnLabel,
  columnsOf,
  countText,
  filtering,
  listFromQuery,
  listKeys,
  listToQuery,
  listTotals,
  loadColumns,
  moreSet,
  ordered,
  parseSort,
  periodPrice,
  saveColumns,
  shownRows,
  sortFields,
  stateFilters,
  stateLabels,
  totalsText,
  type ColumnKey,
  type ListState,
} from './licenceList'

/**
 * The *Licences*, *Services* and *Hardware* tabs (docs/licences-plan.md §8.3): one table, the columns each type needs, and more
 * through the column chooser (kept per type in the browser). All rows of the type come at once; the search, the filters and the
 * sort happen here and stay in the URL, both ways, so the overview's links open the list they count. The totals under it are for
 * the rows shown, per currency. Edit selects rows to mark reviewed or to export.
 */
const props = defineProps<{ type: LicenseType; edit: boolean }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const now = useNow()
const today = computed(() => todayIso(now.value))
const phone = useMedia('(max-width: 599px)')

// The state and the URL, both ways: a link from the overview changes the query while this tab is open.
const tab = route.params.tab
const state = ref<ListState>(listFromQuery(route.query))
const search = ref(state.value.q)
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    if (value.trim() !== state.value.q.trim()) state.value = { ...state.value, q: value }
  }, 250)
})
onBeforeUnmount(() => clearTimeout(timer))
const canonical = (s: ListState) => JSON.stringify(listToQuery(s))
watch(state, (s) => {
  if (route.params.tab !== tab || canonical(s) === canonical(listFromQuery(route.query))) return
  const query = { ...route.query }
  for (const key of listKeys) delete query[key]
  for (const [key, value] of Object.entries(listToQuery(s))) if (value !== undefined) query[key] = value
  void router.replace({ query })
})
watch(
  () => route.query,
  (query) => {
    if (route.params.tab !== tab) return
    const next = listFromQuery(query)
    if (canonical(next) === canonical(state.value)) return
    state.value = next
    search.value = next.q
  },
)

/** A filter as a v-model: each change is a new state, so the URL follows. */
function field<K extends keyof ListState>(key: K) {
  return computed({
    get: () => state.value[key],
    set: (value: ListState[K]) => (state.value = { ...state.value, [key]: value }),
  })
}
const stateFilter = field('state')
const retired = field('retired')
const stateOptions = stateFilters.map((s) => ({ value: s, label: stateLabels[s] }))

// The rows: active and cancelled, the retired too on request.
const status = computed<LicenseStatus[] | undefined>(() =>
  state.value.retired ? ['Active', 'Cancelled', 'Retired'] : undefined,
)
const list = useQuery({
  queryKey: computed(() => licenseKeys.list(props.type, status.value)),
  queryFn: () => licensesApi.list({ type: props.type, status: status.value }),
  placeholderData: keepPreviousData,
})
const rows = computed(() => list.data.value ?? [])

// ?host: the items on that host, from its panel's call.
const host = useQuery({
  queryKey: computed(() => licenseKeys.linked({ host: state.value.host ?? '' })),
  queryFn: () => licensesApi.linked({ host: state.value.host ?? '' }),
  enabled: computed(() => !!state.value.host),
})
const hostItems = computed(() => (host.data.value ? new Set(host.data.value.map((i) => i.id)) : null))
const pickedHost = ref<{ id: string; name: string } | null>(null)
/** The host's name: as picked, else the one every row on it shares. */
const hostName = computed(() => {
  if (pickedHost.value && pickedHost.value.id === state.value.host) return pickedHost.value.name
  const on = rows.value.filter((r) => hostItems.value?.has(r.id))
  const names = on.length ? on.map((r) => new Set(r.hostNames)) : []
  const common = names.length ? [...names[0]].filter((n) => names.every((s) => s.has(n))) : []
  return common.length === 1 ? common[0] : null
})

const shown = computed(() =>
  shownRows(rows.value, state.value, { today: today.value, userId: session.user?.id, hostItems: hostItems.value }),
)
const totals = computed(() => listTotals(shown.value, today.value))
const narrowed = computed(() => filtering(state.value))
const active = computed(
  () => (state.value.state.length ? 1 : 0) + (state.value.retired ? 1 : 0) + moreSet(state.value).length,
)

function onSort(event: { sortField?: unknown; sortOrder?: number | null }) {
  const name = typeof event.sortField === 'string' ? event.sortField : ''
  const sort = name && event.sortOrder ? parseSort(`${event.sortOrder === -1 ? '-' : ''}${name}`, sortFields) : null
  state.value = { ...state.value, sort }
}
function clearAll() {
  search.value = ''
  state.value = cleared(state.value)
}

// The columns, chosen per type and kept in the browser.
const chosen = ref<ColumnKey[]>(loadColumns(props.type))
watch(chosen, (keys) => saveColumns(props.type, keys))
const columnChoices = computed(() => columnsOf(props.type))
const columns = computed(() =>
  ordered(props.type, chosen.value).map((key) => ({ key, label: columnLabel(key, props.type) })),
)

// The selection (Edit): Mark reviewed… and Export CSV; rows that leave the list leave it too.
const selection = ref<LicenseRow[]>([])
watch(shown, (list) => {
  const ids = new Set(list.map((r) => r.id))
  if (selection.value.some((r) => !ids.has(r.id))) selection.value = selection.value.filter((r) => ids.has(r.id))
})
const reviewing = ref(false)
const reviewIds = computed(() => selection.value.map((r) => r.id))
const reviewLabel = computed(() =>
  selection.value.length === 1
    ? `${selection.value[0].display} ${selection.value[0].name}`
    : countText(selection.value.length, props.type),
)
const exportAll = computed(() => licensesApi.exportUrl({ type: props.type, status: status.value }))
const exportSelected = computed(() =>
  licensesApi.exportUrl({ type: props.type, status: status.value, ids: selection.value.map((r) => r.id) }),
)

const open = (row: LicenseRow) => router.push({ name: 'licence', params: { number: String(row.number) } })
const add = () => router.push({ name: 'licence-new', query: { type: props.type } })
const emptyText: Record<LicenseType, string> = {
  License: 'No licences yet: add the first, or make a template for the products you buy most.',
  Service: 'No services yet: add the first, or make a template for the services you buy most.',
  Hardware: 'No hardware yet: add the first, or make a template for the devices you buy most.',
}
const searchWords: Record<LicenseType, string> = {
  License: 'Number, name, product, vendor, key, host…',
  Service: 'Number, name, provider, customer no., host…',
  Hardware: 'Number, name, model, serial, host…',
}
const expiry = (r: LicenseRow) =>
  r.expiresOn ? `${formatDay(r.expiresOn)} · ${daysLeftText(r.expiresOn, now.value)}` : "doesn't expire"
</script>

<template>
  <ErrorState
    v-if="list.isError.value && !list.data.value"
    :error="list.error.value"
    :what="typePlurals[type].toLowerCase()"
    @retry="list.refetch()"
  />
  <div v-else class="list">
    <FilterBar v-model:search="search" :placeholder="searchWords[type]" :active="active">
      <MultiSelect
        v-model="stateFilter"
        :options="stateOptions"
        option-label="label"
        option-value="value"
        placeholder="Any state"
        size="small"
        :max-selected-labels="1"
        selected-items-label="{0} states"
        :show-toggle-all="false"
        aria-label="State"
        data-test="state-filter"
      />
      <label class="check">
        <Checkbox v-model="retired" binary :input-id="`retired-${type}`" />
        <span>Retired too</span>
      </label>
      <ListFilters
        v-model:state="state"
        :type="type"
        :rows="rows"
        :edit="edit"
        @host="(id, name) => (pickedHost = { id, name })"
      />
      <Button
        v-if="narrowed"
        label="Clear"
        icon="pi pi-filter-slash"
        size="small"
        severity="secondary"
        text
        @click="clearAll"
      />
      <template #end>
        <MultiSelect
          v-model="chosen"
          :options="columnChoices"
          option-label="label"
          option-value="key"
          placeholder="Columns"
          :max-selected-labels="0"
          selected-items-label="Columns"
          :show-toggle-all="false"
          size="small"
          aria-label="Columns"
          data-test="column-chooser"
        />
        <Button
          as="a"
          :href="exportAll"
          download
          label="CSV"
          icon="pi pi-download"
          size="small"
          severity="secondary"
          outlined
          :title="`All ${typePlurals[type].toLowerCase()} as CSV${state.retired ? ', the retired too' : ''}; select rows for only those`"
        />
      </template>
    </FilterBar>
    <ListChips v-model:state="state" :type="type" :rows="rows" :host-name="hostName" />
    <div v-if="edit && selection.length" class="selected" role="status" data-test="licences-selected">
      <span>{{ countText(selection.length, type) }} selected</span>
      <Button label="Mark reviewed…" icon="pi pi-check-square" size="small" text @click="reviewing = true" />
      <Button as="a" :href="exportSelected" download label="Export CSV" icon="pi pi-download" size="small" text />
      <Button label="Clear" size="small" severity="secondary" text @click="selection = []" />
    </div>

    <div v-if="phone" class="mobile panel">
      <EmptyState
        v-if="!shown.length && !list.isLoading.value"
        :title="rows.length || narrowed ? 'Nothing matches the filters.' : emptyText[type]"
      />
      <ul v-else>
        <li v-for="r in shown" :key="r.id" tabindex="0" @click="open(r)" @keydown.enter="open(r)">
          <div class="name">
            <VerdictTag :code="r.verdictCode" compact />
            <span class="mono">{{ r.display }}</span>
            <span class="strong">{{ r.name }}</span>
          </div>
          <div class="small muted">{{ expiry(r) }} · {{ periodPrice(r, type === 'Service' ? 'month' : 'year') }}</div>
        </li>
      </ul>
    </div>
    <DataTable
      v-else
      v-model:selection="selection"
      :value="shown"
      :loading="list.isLoading.value || (!!state.host && host.isLoading.value)"
      size="small"
      class="panel table"
      data-key="id"
      row-hover
      lazy
      removable-sort
      :sort-field="state.sort?.field"
      :sort-order="state.sort?.order ?? 0"
      :row-class="(r: LicenseRow) => (r.status === 'Retired' ? 'row-muted' : undefined)"
      :data-test="`licences-table-${type}`"
      @sort="onSort"
      @row-click="(e) => open(e.data)"
    >
      <Column v-if="edit" selection-mode="multiple" style="width: 3rem" />
      <Column header="No." field="number" sortable style="width: 6rem">
        <template #body="{ data }"
          ><span class="mono nowrap">{{ data.display }}</span></template
        >
      </Column>
      <Column :header="typeLabels[type]" field="name" sortable>
        <template #body="{ data }">
          <span class="name">
            <VerdictTag :code="data.verdictCode" compact />
            <RouterLink :to="{ name: 'licence', params: { number: String(data.number) } }" class="strong">{{
              data.name
            }}</RouterLink>
            <Tag v-if="data.status !== 'Active'" :value="data.status.toLowerCase()" severity="secondary" />
          </span>
        </template>
      </Column>
      <Column v-for="c in columns" :key="c.key" :header="c.label" :field="c.key" sortable>
        <template #body="{ data }"><ListCell :column="c.key" :row="data" :today="today" /></template>
      </Column>
      <template #empty>
        <EmptyState v-if="rows.length || narrowed" icon="pi pi-search" title="Nothing matches the filters.">
          <Button label="Clear the filters" size="small" severity="secondary" outlined @click="clearAll" />
        </EmptyState>
        <EmptyState v-else :title="emptyText[type]">
          <div v-if="edit" class="empty-actions">
            <Button :label="`Add a ${typeLabels[type].toLowerCase()}`" icon="pi pi-plus" size="small" @click="add" />
            <Button
              as="router-link"
              :to="{ name: 'licences', params: { tab: 'templates' } }"
              label="Templates"
              size="small"
              severity="secondary"
              outlined
            />
          </div>
        </EmptyState>
      </template>
    </DataTable>
    <p v-if="rows.length" class="totals muted" data-test="licences-totals">{{ totalsText(totals, type) }}</p>
    <ReviewDialog v-model:visible="reviewing" :ids="reviewIds" :label="reviewLabel" @done="selection = []" />
  </div>
</template>

<style scoped>
.list {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.selected {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.table {
  overflow: hidden;
}

.table :deep(tr) {
  cursor: pointer;
}

.name {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.strong {
  font-weight: 500;
}

.totals {
  margin: 0;
}

.empty-actions {
  display: flex;
  gap: var(--pnmt-space-2);
}

.mobile ul {
  list-style: none;
  margin: 0;
  padding: 0;
}

.mobile li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
  cursor: pointer;
}

.small {
  font-size: 0.857rem;
}
</style>
