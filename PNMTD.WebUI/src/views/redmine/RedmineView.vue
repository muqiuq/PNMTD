<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type RedmineFilters, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import TabNav from '@/components/TabNav.vue'
import { useServerTable } from '@/composables/useServerTable'
import RedmineFreshnessBar from './RedmineFreshnessBar.vue'
import TicketButtons from './TicketButtons.vue'
import TicketRow from './TicketRow.vue'

/**
 * Redmine (docs/audit-redmine-zabbix-plan.md §6.3): *Overview*, the most important tickets of the configured projects in
 * tiles and six lists, and *Search*, Redmine's list with filters, 25 to a page. Every ticket has its two buttons; a
 * number in the search opens the ticket's page. Both come from the server's snapshots at once, with the bar in the header
 * (docs/redmine-queue-outbound-plan.md §5): polled every 2 s while a refresh runs, else every 30 s (the overview) or
 * 60 s (the search, §5.4). The overview doesn't wait for the status.
 */
const route = useRoute()
const router = useRouter()
const tab = computed(() => (route.params.tab === 'search' ? 'search' : 'overview'))
const fromQuery = (k: string) => (typeof route.query[k] === 'string' ? (route.query[k] as string) : '')
const numberOf = (k: string) => (fromQuery(k) ? Number(fromQuery(k)) : null)

const status = useQuery({ queryKey: [...keys.redmine, 'status'], queryFn: api.redmine.status, staleTime: 60_000 })
const lookups = useQuery({
  queryKey: [...keys.redmine, 'lookups'],
  queryFn: api.redmine.lookups,
  staleTime: 600_000,
  enabled: computed(() => !!status.data.value?.enabled),
})
const notConfigured = computed(() => status.data.value && !status.data.value.enabled)
const projects = computed(() => status.data.value?.projects ?? [])
const redmineUrl = computed(() => {
  const p = projects.value.find((x) => x.id === project.value)
  if (p) return `${p.url}/issues`
  return projects.value.length === 1 ? `${projects.value[0]!.url}/issues` : `${status.data.value?.url ?? ''}/issues`
})

// --- the overview --------------------------------------------------------------------------------------------------
const project = ref<number | null>(numberOf('project'))
const overviewKey = computed(() => [...keys.redmine, 'overview', project.value])
const overview = useQuery({
  queryKey: overviewKey,
  queryFn: () => api.redmine.overview(project.value ?? undefined),
  enabled: computed(() => tab.value === 'overview'),
  refetchInterval: (q) => (q.state.data?.freshness.refreshing ? 2000 : 30_000),
})
const o = computed(() => overview.data.value)

type Filters = RedmineFilters & { sort?: string }
const toSearch = (filters: Filters) => ({
  name: 'redmine',
  params: { tab: 'search' },
  query: Object.fromEntries(
    Object.entries({ project: project.value ?? undefined, ...filters }).filter(([, v]) => v !== undefined),
  ) as Record<string, string>,
})
const tiles = computed(() => {
  const t = o.value?.tiles
  if (!t) return []
  return [
    { key: 'open', label: 'Open', value: t.open, to: toSearch({}) },
    // docs/boards-plan.md §7.4: today and yesterday in PNMT's time zone; solved is a closed status.
    {
      key: 'received',
      label: 'Received today',
      value: t.receivedToday,
      sub: `yesterday ${t.receivedYesterday}`,
      to: toSearch({ received: 'today', status: 'all', sort: '-created' }),
    },
    {
      key: 'solved',
      label: 'Solved today',
      value: t.solvedToday,
      sub: `yesterday ${t.solvedYesterday}`,
      to: toSearch({ solved: 'today', status: 'closed' }),
    },
    {
      key: 'urgent',
      label: 'Urgent',
      value: t.urgent,
      to: toSearch({ sort: '-priority' }),
      tone: t.urgent ? 'fail' : '',
    },
    {
      key: 'unassigned',
      label: 'New, no one assigned',
      value: t.unassigned,
      to: toSearch({ assignee: 'none', sort: 'created' }),
      tone: t.unassigned ? 'warn' : '',
    },
    {
      key: 'overdue',
      label: 'Overdue',
      value: t.overdue,
      to: toSearch({ overdue: true } as Filters),
      tone: t.overdue ? 'warn' : '',
    },
    {
      key: 'stale',
      label: `No update for ${o.value!.staleDays} days`,
      value: t.stale,
      to: toSearch({ stale: true, sort: 'updated' } as Filters),
    },
  ]
})
const lists = computed(() => {
  const v = o.value
  if (!v) return []
  return [
    { key: 'urgent', title: 'Urgent', items: v.urgent, all: toSearch({ sort: '-priority' }) },
    {
      key: 'unassigned',
      title: 'New, no one assigned',
      items: v.unassigned,
      all: toSearch({ assignee: 'none', sort: 'created' }),
    },
    { key: 'overdue', title: 'Overdue', items: v.overdue, all: toSearch({ overdue: true } as Filters) },
    {
      key: 'problems',
      title: 'Linked to a problem in PNMT',
      items: v.problems,
      all: toSearch({ linked: true } as Filters),
    },
    {
      key: 'stale',
      title: `No update for ${v.staleDays} days`,
      items: v.stale,
      all: toSearch({ stale: true, sort: 'updated' } as Filters),
    },
    { key: 'recent', title: 'Recently updated', items: v.recent, all: toSearch({ status: 'all' }) },
  ]
})

// --- the search ----------------------------------------------------------------------------------------------------
const statusFilter = ref<'open' | 'closed' | 'all'>((fromQuery('status') as 'open' | 'closed' | 'all') || 'open')
const tracker = ref<number | null>(numberOf('tracker'))
const priority = ref<number | null>(numberOf('priority'))
const assignee = ref<string | null>(fromQuery('assignee') || null)
const overdue = ref(fromQuery('overdue') === 'true')
const linked = ref(fromQuery('linked') === 'true')
const stale = ref(fromQuery('stale') === 'true')
type Day = 'today' | 'yesterday'
const dayOf = (k: string): Day | undefined =>
  ['today', 'yesterday'].includes(fromQuery(k)) ? (fromQuery(k) as Day) : undefined
/** Received or solved today or yesterday (the tiles' filter): "received-today", or null for any day. */
const day = ref<string | null>(
  dayOf('received') ? `received-${dayOf('received')}` : dayOf('solved') ? `solved-${dayOf('solved')}` : null,
)
const days = [
  { value: 'received-today', label: 'Received today' },
  { value: 'received-yesterday', label: 'Received yesterday' },
  { value: 'solved-today', label: 'Solved today' },
  { value: 'solved-yesterday', label: 'Solved yesterday' },
]
const dayFilter = (kind: 'received' | 'solved'): Day | undefined =>
  day.value?.startsWith(`${kind}-`) ? (day.value.slice(kind.length + 1) as Day) : undefined
const staleLabel = computed(() => (o.value ? `No update for ${o.value.staleDays} days` : 'No recent update'))
const filters = computed<RedmineFilters>(() => ({
  project: project.value ?? undefined,
  status: statusFilter.value,
  tracker: tracker.value ?? undefined,
  priority: priority.value ?? undefined,
  assignee: assignee.value ?? undefined,
  overdue: overdue.value || undefined,
  linked: linked.value || undefined,
  stale: stale.value || undefined,
  received: dayFilter('received'),
  solved: dayFilter('solved'),
}))
watch([project, statusFilter, tracker, priority, assignee, overdue, linked, stale, day], () =>
  router.replace({
    query: {
      ...route.query,
      project: project.value ?? undefined,
      status: statusFilter.value === 'open' ? undefined : statusFilter.value,
      tracker: tracker.value ?? undefined,
      priority: priority.value ?? undefined,
      assignee: assignee.value ?? undefined,
      overdue: overdue.value ? 'true' : undefined,
      linked: linked.value ? 'true' : undefined,
      stale: stale.value ? 'true' : undefined,
      received: dayFilter('received'),
      solved: dayFilter('solved'),
      page: undefined,
    },
  }),
)
const active = computed(
  () =>
    [tracker.value, priority.value, assignee.value].filter((x) => x != null).length +
    (statusFilter.value === 'open' ? 0 : 1) +
    (overdue.value ? 1 : 0) +
    (linked.value ? 1 : 0) +
    (stale.value ? 1 : 0) +
    (day.value ? 1 : 0),
)
const searchRefreshing = ref(false)
const table = useServerTable<S['RedmineIssueItem'], S['RedmineSearchPage']>({
  key: [...keys.redmine, 'issues'],
  fetch: (q) => api.redmine.issues({ ...q, ...filters.value }),
  filters: computed(() => ({ ...filters.value })),
  defaultSort: '-updated',
  pageSize: 25,
  syncUrl: true,
  refetchInterval: computed(() => (searchRefreshing.value ? 2000 : 60_000)),
  enabled: computed(() => tab.value === 'search'),
})
const searchPage = computed(() => table.query.data.value)
watch(
  () => !!searchPage.value?.freshness.refreshing,
  (refreshing) => (searchRefreshing.value = refreshing),
)

/**
 * A tile, or a list's *All*, opens the search with its filters and sort in the URL. The page stays mounted when the tab
 * changes, so the search reads them from the URL again then, not only when the page loads; otherwise it kept the old ones.
 */
function readQuery() {
  if (numberOf('project') != null) project.value = numberOf('project')
  statusFilter.value = (fromQuery('status') as 'open' | 'closed' | 'all') || 'open'
  tracker.value = numberOf('tracker')
  priority.value = numberOf('priority')
  assignee.value = fromQuery('assignee') || null
  overdue.value = fromQuery('overdue') === 'true'
  linked.value = fromQuery('linked') === 'true'
  stale.value = fromQuery('stale') === 'true'
  day.value = dayOf('received') ? `received-${dayOf('received')}` : dayOf('solved') ? `solved-${dayOf('solved')}` : null
  table.sort.value = fromQuery('sort') || '-updated'
  table.search.value = fromQuery('q')
  table.page.value = Number(fromQuery('page')) || 1
}
watch(tab, (t) => {
  if (t === 'search') readQuery()
})
const statuses = [
  { value: 'open', label: 'Open' },
  { value: 'closed', label: 'Closed' },
  { value: 'all', label: 'All' },
]
const sorts = [
  { value: '-updated', label: 'Last updated' },
  { value: '-created', label: 'Newest' },
  { value: '-priority', label: 'Priority' },
  { value: '-id', label: 'Number' },
]
const assignees = computed(() => [
  { value: 'none', label: 'No one' },
  ...(lookups.data.value?.members ?? []).map((m) => ({ value: String(m.id), label: m.name })),
])
const projectOptions = computed(() =>
  projects.value.map((p) => ({ value: p.id, label: p.parentId ? `· ${p.name}` : p.name })),
)

/** A number (1234, #1234) opens the ticket's page. */
function enter() {
  const m = /^#?(\d+)$/.exec(table.search.value.trim())
  if (m) void router.push({ name: 'redmine-issue', params: { id: m[1] } })
}

/** What the bar refreshes: the overview shown, or the search with exactly the page's query. */
const overviewRefresh = computed(() => ({ view: 'overview' as const, project: project.value ?? undefined }))
const searchRefresh = computed(() => ({ view: 'search' as const, ...table.params.value, ...filters.value }))

const tabs = computed(() => [
  { key: 'overview', label: 'Overview', icon: 'pi pi-th-large', to: { name: 'redmine', params: { tab: 'overview' } } },
  { key: 'search', label: 'Search', icon: 'pi pi-search', to: { name: 'redmine', params: { tab: 'search' } } },
])
const configError = computed(() =>
  overview.error.value instanceof ApiError && overview.error.value.status === 409 ? null : overview.error.value,
)
</script>

<template>
  <div class="page">
    <PageHeader title="Redmine">
      <template #actions>
        <RedmineFreshnessBar
          v-if="tab === 'overview' && o"
          :freshness="o.freshness"
          :refresh="overviewRefresh"
          :query-key="overviewKey"
        />
        <RedmineFreshnessBar
          v-else-if="tab === 'search' && searchPage"
          :freshness="searchPage.freshness"
          :refresh="searchRefresh"
          :query-key="[...keys.redmine, 'issues']"
        />
        <Select
          v-if="projects.length > 1"
          v-model="project"
          :options="projectOptions"
          option-label="label"
          option-value="value"
          placeholder="All projects"
          show-clear
          size="small"
          aria-label="Project"
        />
        <Button
          v-if="status.data.value?.enabled"
          as="a"
          :href="redmineUrl"
          target="_blank"
          rel="noopener noreferrer"
          label="Open Redmine"
          icon="pi pi-external-link"
          size="small"
        />
      </template>
    </PageHeader>

    <EmptyState
      v-if="notConfigured"
      icon="pi pi-ticket"
      title="Redmine isn't configured"
      text="Set Redmine__Url, Redmine__ApiKey and Redmine__Projects in the deployment's .env (see deploy/README.md, “Redmine”)."
    />
    <template v-else>
      <p v-if="status.data.value?.error" class="warn-text">
        <i class="pi pi-exclamation-triangle" aria-hidden="true" /> {{ status.data.value.error }}
      </p>
      <TabNav :items="tabs" :active="tab" label="Redmine" />

      <template v-if="tab === 'overview'">
        <ErrorState
          v-if="configError && !o"
          :error="configError"
          what="Redmine's tickets"
          @retry="overview.refetch()"
        />
        <template v-else-if="o">
          <section class="tiles panel" aria-label="Counts">
            <RouterLink v-for="t in tiles" :key="t.key" :to="t.to" class="tile" :class="t.tone">
              <span class="big">{{ t.value }}</span>
              <span class="muted">{{ t.label }}</span>
              <span v-if="t.sub" class="muted sub">{{ t.sub }}</span>
            </RouterLink>
          </section>
          <div class="lists">
            <section v-for="l in lists" :key="l.key" class="panel" :aria-label="l.title">
              <div class="panel-header">
                {{ l.title }}
                <span v-if="l.items.length" class="muted count">{{ l.items.length }}</span>
                <RouterLink :to="l.all" class="all">All ›</RouterLink>
              </div>
              <div class="panel-body">
                <p v-if="!l.items.length" class="muted none">None.</p>
                <ul v-else class="list">
                  <li v-for="t in l.items" :key="t.id"><TicketRow :ticket="t" /></li>
                </ul>
              </div>
            </section>
          </div>
        </template>
      </template>

      <template v-else>
        <div @keydown.enter="enter">
          <FilterBar
            v-model:search="table.search.value"
            placeholder="Subject, description, notes, or a number"
            :active="active"
          >
            <Select
              v-model="statusFilter"
              :options="statuses"
              option-label="label"
              option-value="value"
              size="small"
              class="small-filter"
              aria-label="Status"
            />
            <Select
              v-model="tracker"
              :options="lookups.data.value?.trackers ?? []"
              option-label="name"
              option-value="id"
              placeholder="Any tracker"
              show-clear
              size="small"
              class="small-filter"
              aria-label="Tracker"
            />
            <Select
              v-model="priority"
              :options="lookups.data.value?.priorities ?? []"
              option-label="name"
              option-value="id"
              placeholder="Any priority"
              show-clear
              size="small"
              class="small-filter"
              aria-label="Priority"
            />
            <Select
              v-model="assignee"
              :options="assignees"
              option-label="label"
              option-value="value"
              placeholder="Anyone"
              show-clear
              filter
              size="small"
              class="filter"
              aria-label="Assignee"
            />
            <label class="switch"><ToggleSwitch v-model="overdue" /> Overdue</label>
            <label class="switch"><ToggleSwitch v-model="linked" /> Linked in PNMT</label>
            <label class="switch"><ToggleSwitch v-model="stale" /> {{ staleLabel }}</label>
            <Select
              v-model="day"
              :options="days"
              option-label="label"
              option-value="value"
              placeholder="Any day"
              show-clear
              size="small"
              class="small-filter"
              aria-label="Received or solved"
            />
            <Select
              v-model="table.sort.value"
              :options="sorts"
              option-label="label"
              option-value="value"
              size="small"
              class="small-filter"
              aria-label="Order"
            />
          </FilterBar>
        </div>
        <ListTable :table="table" what="tickets" fixed>
          <Column header="#" style="width: 5rem">
            <template #body="{ data }">
              <RouterLink :to="{ name: 'redmine-issue', params: { id: data.id } }" class="mono"
                >#{{ data.id }}</RouterLink
              >
            </template>
          </Column>
          <Column header="Tracker" style="width: 7rem">
            <template #body="{ data }"
              ><span class="ellipsis cell">{{ data.tracker }}</span></template
            >
          </Column>
          <Column header="Status" style="width: 8rem">
            <template #body="{ data }">
              <Tag :value="data.status" :severity="data.isClosed ? 'secondary' : 'info'" />
            </template>
          </Column>
          <Column header="Priority" style="width: 7rem">
            <template #body="{ data }"
              ><span class="ellipsis cell">{{ data.priority }}</span></template
            >
          </Column>
          <Column header="Subject">
            <template #body="{ data }">
              <RouterLink
                :to="{ name: 'redmine-issue', params: { id: data.id } }"
                class="ellipsis cell"
                :title="data.subject"
                >{{ data.subject }}</RouterLink
              >
            </template>
          </Column>
          <Column header="Assignee" style="width: 9rem">
            <template #body="{ data }"
              ><span class="ellipsis cell">{{ data.assignedTo ?? '–' }}</span></template
            >
          </Column>
          <Column header="Updated" style="width: 8rem">
            <template #body="{ data }"><RelativeTime :value="data.updatedOn" /></template>
          </Column>
          <Column header="PNMT" style="width: 4.5rem">
            <template #body="{ data }">
              <span v-if="data.links" class="pnmt" :title="`Linked to ${data.links} in PNMT`"
                ><i class="pi pi-link" aria-hidden="true" /> {{ data.links }}</span
              >
            </template>
          </Column>
          <Column style="width: 6rem">
            <template #body="{ data }"><TicketButtons :ticket="data" compact /></template>
          </Column>
          <template #mobile="{ item }">
            <div class="mobile-item">
              <div class="mobile-row">
                <span class="line">
                  <span class="mono">#{{ item.id }}</span>
                  <Tag :value="item.status" :severity="item.isClosed ? 'secondary' : 'info'" />
                  <span v-if="item.links" class="pnmt"
                    ><i class="pi pi-link" aria-hidden="true" /> {{ item.links }}</span
                  >
                </span>
                <RouterLink :to="{ name: 'redmine-issue', params: { id: item.id } }" class="ellipsis">{{
                  item.subject
                }}</RouterLink>
              </div>
              <TicketButtons :ticket="item" compact />
            </div>
          </template>
        </ListTable>
      </template>
    </template>
  </div>
</template>

<style scoped>
.tiles {
  display: flex;
  flex-wrap: wrap;
  overflow: hidden;
}

.tile {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pnmt-space-3) var(--pnmt-space-4);
  border-right: 1px solid var(--pnmt-border);
  color: var(--pnmt-text);
  min-width: 9rem;
  flex: 1;
}

.tile:last-child {
  border-right: none;
}

.tile:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.tile .big {
  font-size: 1.5rem;
  font-weight: 600;
}

.tile .sub {
  font-size: 0.8rem;
}

.tile.fail .big {
  color: var(--pnmt-fail);
}

.tile.warn .big {
  color: var(--pnmt-warn);
}

.lists {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 30rem), 1fr));
  gap: var(--pnmt-space-3);
  align-items: start;
}

.panel-header .count {
  font-weight: 400;
}

.panel-header .all {
  margin-left: auto;
  font-weight: 400;
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.none {
  margin: 0;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  white-space: nowrap;
}

.pnmt {
  color: var(--pnmt-muted);
  white-space: nowrap;
}

.mobile-item {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.mobile-row {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.mobile-row .line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.warn-text {
  color: var(--pnmt-warn);
  margin: 0;
}
</style>
