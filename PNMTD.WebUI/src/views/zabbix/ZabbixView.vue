<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S, type ZabbixHostFilters, type ZabbixProblemFilters } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import SinceText from '@/components/SinceText.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatTime } from '@/format'
import { useSession } from '@/stores/session'
import SensorTicket from '@/views/redmine/SensorTicket.vue'
import AttachDialog from './AttachDialog.vue'
import ProblemDetail from './ProblemDetail.vue'
import ZabbixInterfaces from './ZabbixInterfaces.vue'
import ZabbixSeverityTag from './ZabbixSeverityTag.vue'
import { severities, severityIndex, severityLabel, severityOptions } from './zabbix'

/**
 * Zabbix (docs/audit-redmine-zabbix-plan.md §7.4–7.6, §7.8): *Overview*, the open problems by severity and by category
 * (a host group or a tag), and the hosts at a glance; *Problems*, filtered as the URL says; *Hosts*, the mirror; and
 * *Suggestions*, the unattached hosts with the PNMT hosts they match, to attach in bulk.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run } = useActions()
const canAttach = computed(() => session.can('Monitoring', 'Edit'))
const tab = computed(() => {
  const t = route.params.tab
  return t === 'problems' || t === 'hosts' || (t === 'suggestions' && canAttach.value) ? t : 'overview'
})
const fromQuery = (k: string) => (typeof route.query[k] === 'string' ? (route.query[k] as string) : '')

const status = useQuery({ queryKey: [...keys.zabbix, 'status'], queryFn: api.zabbix.status, staleTime: 60_000 })
const notConfigured = computed(() => status.data.value && !status.data.value.enabled)

// --- overview --------------------------------------------------------------------------------------------------------
const category = ref(fromQuery('category') || 'group')
const withSuppressed = ref(fromQuery('suppressed') === 'true')
const summary = useQuery({
  queryKey: computed(() => [...keys.zabbix, 'summary', category.value, withSuppressed.value]),
  queryFn: () => api.zabbix.summary(category.value, withSuppressed.value || undefined),
  enabled: computed(() => !!status.data.value?.enabled),
  refetchInterval: 30_000,
})
const s = computed(() => summary.data.value)
const categoryOptions = computed(() => [
  { value: 'group', label: 'Host group' },
  ...(s.value?.tagNames ?? []).map((t) => ({ value: `tag:${t}`, label: `Tag: ${t}` })),
])
const toProblems = (query: Record<string, string | undefined>) => ({
  name: 'zabbix',
  params: { tab: 'problems' },
  query: Object.fromEntries(
    Object.entries({ ...query, suppressed: withSuppressed.value ? 'true' : undefined }).filter(
      ([, v]) => v !== undefined,
    ),
  ) as Record<string, string>,
})
function cellLink(row: string, severity: S['ZabbixSeverity']) {
  if (category.value === 'group') return toProblems({ group: row, severity })
  if (row === '(none)') return null
  return toProblems({ tag: `${category.value.slice(4)}:${row}`, severity })
}
const asOfStale = computed(() => !!s.value?.stale)

// --- problems --------------------------------------------------------------------------------------------------------
const minSeverity = ref<S['ZabbixSeverity'] | null>((fromQuery('minSeverity') as S['ZabbixSeverity']) || null)
const severity = ref<S['ZabbixSeverity'] | null>((fromQuery('severity') as S['ZabbixSeverity']) || null)
const group = ref(fromQuery('group') || null)
const tag = ref(fromQuery('tag'))
const acknowledged = ref<'yes' | 'no' | null>((fromQuery('acknowledged') as 'yes' | 'no') || null)
const suppressed = ref(fromQuery('suppressed') === 'true')
const olderThan = ref<number | null>(fromQuery('olderThan') ? Number(fromQuery('olderThan')) : null)
const problemFilters = computed<ZabbixProblemFilters>(() => ({
  minSeverity: minSeverity.value ?? undefined,
  severity: severity.value ?? undefined,
  group: group.value ?? undefined,
  tag: tag.value.trim() || undefined,
  acknowledged: acknowledged.value === null ? undefined : acknowledged.value === 'yes',
  suppressed: suppressed.value || undefined,
  olderThanMinutes: olderThan.value ?? undefined,
}))
watch([minSeverity, severity, group, tag, acknowledged, suppressed, olderThan], () => {
  if (tab.value !== 'problems') return
  void router.replace({
    query: {
      ...route.query,
      minSeverity: minSeverity.value ?? undefined,
      severity: severity.value ?? undefined,
      group: group.value ?? undefined,
      tag: tag.value.trim() || undefined,
      acknowledged: acknowledged.value ?? undefined,
      suppressed: suppressed.value ? 'true' : undefined,
      olderThan: olderThan.value ?? undefined,
      page: undefined,
    },
  })
})
// A tile or a cell of the overview: the problems tab with its filters, set anew.
watch(
  () => [tab.value, route.query] as const,
  ([t, q]) => {
    if (t !== 'problems') return
    const get = (k: string) => (typeof q[k] === 'string' ? (q[k] as string) : '')
    minSeverity.value = (get('minSeverity') as S['ZabbixSeverity']) || null
    severity.value = (get('severity') as S['ZabbixSeverity']) || null
    group.value = get('group') || null
    tag.value = get('tag')
    acknowledged.value = (get('acknowledged') as 'yes' | 'no') || null
    suppressed.value = get('suppressed') === 'true'
    olderThan.value = get('olderThan') ? Number(get('olderThan')) : null
    if (get('q') !== problems.search.value) problems.search.value = get('q')
  },
)
const problemCount = computed(
  () =>
    [minSeverity.value, severity.value, group.value, acknowledged.value, olderThan.value].filter((x) => x !== null)
      .length +
    (tag.value.trim() ? 1 : 0) +
    (suppressed.value ? 1 : 0),
)
const problems = useServerTable<S['ZabbixProblemItem']>({
  key: [...keys.zabbix, 'problems'],
  fetch: (q) => api.zabbix.problems({ ...q, ...problemFilters.value }),
  filters: computed(() => ({ ...problemFilters.value })),
  syncUrl: true,
  refetchInterval: 30_000,
})
const expanded = ref<Record<string, boolean>>({})
const ackOptions = [
  { value: 'no', label: 'Not acknowledged' },
  { value: 'yes', label: 'Acknowledged' },
]
const olderOptions = [
  { value: 60, label: 'Older than an hour' },
  { value: 24 * 60, label: 'Older than a day' },
  { value: 7 * 24 * 60, label: 'Older than a week' },
]

// --- hosts -----------------------------------------------------------------------------------------------------------
const hostGroup = ref(fromQuery('hostGroup') || null)
const hostStatus = ref<ZabbixHostFilters['status'] | null>((fromQuery('status') as ZabbixHostFilters['status']) || null)
const availability = ref<S['ZabbixAvailability'] | null>((fromQuery('availability') as S['ZabbixAvailability']) || null)
const attached = ref<'yes' | 'no' | null>((fromQuery('attached') as 'yes' | 'no') || null)
const hostFilters = computed<ZabbixHostFilters>(() => ({
  group: hostGroup.value ?? undefined,
  status: hostStatus.value ?? undefined,
  availability: availability.value ?? undefined,
  attached: attached.value === null ? undefined : attached.value === 'yes',
}))
watch([hostGroup, hostStatus, availability, attached], () => {
  if (tab.value !== 'hosts') return
  void router.replace({
    query: {
      ...route.query,
      hostGroup: hostGroup.value ?? undefined,
      status: hostStatus.value ?? undefined,
      availability: availability.value ?? undefined,
      attached: attached.value ?? undefined,
      page: undefined,
    },
  })
})
const hosts = useServerTable<S['ZabbixHostItem']>({
  key: [...keys.zabbix, 'hosts'],
  fetch: (q) => api.zabbix.hosts({ ...q, ...hostFilters.value }),
  filters: computed(() => ({ ...hostFilters.value })),
  syncUrl: true,
  refetchInterval: 60_000,
})
const hostCount = computed(
  () => [hostGroup.value, hostStatus.value, availability.value, attached.value].filter((x) => x !== null).length,
)
const groupOptions = computed(() => (s.value?.groups ?? []).map((g) => ({ value: g, label: g })))
const attaching = ref<S['ZabbixHostItem'] | null>(null)
const attachOpen = ref(false)
function attach(host: S['ZabbixHostItem']) {
  attaching.value = host
  attachOpen.value = true
}

// --- suggestions -----------------------------------------------------------------------------------------------------
const suggestions = useQuery({
  queryKey: [...keys.zabbix, 'suggestions'],
  queryFn: api.zabbix.suggestions,
  enabled: computed(() => tab.value === 'suggestions' && canAttach.value),
})
const NEW = '__new__'
const choice = ref<Record<number, string | null>>({})
const picked = ref<Record<number, boolean>>({})
watch(
  () => suggestions.data.value,
  (list) => {
    for (const x of list ?? []) {
      if (!(x.host.id in choice.value)) choice.value[x.host.id] = x.suggested ?? null
      if (!(x.host.id in picked.value)) picked.value[x.host.id] = !!x.suggested
    }
  },
  { immediate: true },
)
const optionsOf = (x: S['ZabbixSuggestion']) => [
  ...x.candidates.map((c) => ({ value: c.hostId as string, label: `${c.name} (${c.reason})` })),
  { value: NEW, label: `A new host "${x.host.technicalName.split('.')[0]}"` },
]
const selected = computed(() =>
  (suggestions.data.value ?? []).filter((x) => picked.value[x.host.id] && choice.value[x.host.id]),
)
async function attachSelected() {
  const items: S['ZabbixAttachItem'][] = selected.value.map((x) => {
    const c = choice.value[x.host.id]!
    return {
      zabbixHostId: x.host.id,
      hostId: c === NEW ? null : c,
      newHostName: c === NEW ? (x.host.technicalName.split('.')[0] ?? x.host.name) : null,
    }
  })
  const done = await run(() => api.zabbix.attach(items), {
    success: `${items.length} Zabbix host${items.length === 1 ? '' : 's'} attached`,
    failure: 'Not attached',
    invalidate: [keys.zabbix, keys.hosts],
  })
  if (done) {
    picked.value = {}
    choice.value = {}
    await queryClient.invalidateQueries({ queryKey: keys.zabbix })
  }
}

const tabs = computed(() => [
  { key: 'overview', label: 'Overview', icon: 'pi pi-th-large', to: { name: 'zabbix', params: { tab: 'overview' } } },
  {
    key: 'problems',
    label: 'Problems',
    icon: 'pi pi-exclamation-triangle',
    to: { name: 'zabbix', params: { tab: 'problems' } },
    badge: s.value ? s.value.severities.reduce((n, x) => n + x.count, 0) || null : null,
  },
  { key: 'hosts', label: 'Hosts', icon: 'pi pi-server', to: { name: 'zabbix', params: { tab: 'hosts' } } },
  ...(canAttach.value
    ? [
        {
          key: 'suggestions',
          label: 'Suggestions',
          icon: 'pi pi-link',
          to: { name: 'zabbix', params: { tab: 'suggestions' } },
        },
      ]
    : []),
])
</script>

<template>
  <div class="page">
    <PageHeader title="Zabbix">
      <template #status>
        <span v-if="status.data.value?.version" class="muted">Zabbix {{ status.data.value.version }}</span>
      </template>
      <template #actions>
        <Button
          v-if="s"
          as="a"
          :href="s.dashboardUrl"
          target="_blank"
          rel="noopener noreferrer"
          label="Open Zabbix"
          icon="pi pi-external-link"
          size="small"
        />
      </template>
    </PageHeader>

    <EmptyState
      v-if="notConfigured"
      icon="pi pi-chart-bar"
      title="Zabbix isn't configured"
      text="Set Zabbix__Url and Zabbix__ApiToken in the deployment's .env (see deploy/README.md, “Zabbix”)."
    />
    <template v-else>
      <p v-if="status.data.value?.error || s?.error" class="warn-text">
        <i class="pi pi-exclamation-triangle" aria-hidden="true" /> {{ s?.error ?? status.data.value?.error }}
      </p>
      <TabNav :items="tabs" :active="tab" label="Zabbix" />

      <!-- Overview -->
      <template v-if="tab === 'overview'">
        <ErrorState
          v-if="summary.isError.value && !s"
          :error="summary.error.value"
          what="Zabbix's problems"
          @retry="summary.refetch()"
        />
        <template v-else-if="s">
          <section class="tiles panel" aria-label="By severity">
            <RouterLink
              v-for="x in s.severities"
              :key="x.severity"
              :to="toProblems({ severity: x.severity })"
              class="tile"
              :class="{ empty: !x.count }"
            >
              <span class="big"><span class="dot" :class="`sev-${x.severity}`" aria-hidden="true" />{{ x.count }}</span>
              <span class="muted">{{ severityLabel(x.severity) }}</span>
              <span v-if="x.unacknowledged" class="small">{{ x.unacknowledged }} unacknowledged</span>
            </RouterLink>
          </section>
          <div class="bar">
            <label class="switch"
              ><ToggleSwitch v-model="withSuppressed" /> Count suppressed ({{ s.suppressed }})</label
            >
            <span :class="asOfStale ? 'stale' : 'muted'" :title="s.asOf ? formatAbsolute(s.asOf) : ''">
              As of {{ s.asOf ? formatTime(s.asOf) : '–' }}
            </span>
          </div>
          <div class="overview">
            <section class="panel" aria-labelledby="zx-matrix-title">
              <div id="zx-matrix-title" class="panel-header">
                By category
                <Select
                  v-model="category"
                  :options="categoryOptions"
                  option-label="label"
                  option-value="value"
                  size="small"
                  class="category"
                  aria-label="Category"
                />
              </div>
              <div class="panel-body">
                <p v-if="!s.categories.length" class="muted none">No open problems.</p>
                <table v-else class="matrix">
                  <thead>
                    <tr>
                      <th scope="col">{{ category === 'group' ? 'Host group' : category.slice(4) }}</th>
                      <th v-for="sev in severities" :key="sev" scope="col" class="num">
                        <ZabbixSeverityTag :severity="sev" />
                      </th>
                      <th scope="col" class="num">Total</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="row in s.categories" :key="row.name">
                      <th scope="row">{{ row.name }}</th>
                      <td v-for="sev in severities" :key="sev" class="num">
                        <template v-if="row.counts[severityIndex(sev)]">
                          <RouterLink
                            v-if="cellLink(row.name, sev)"
                            :to="cellLink(row.name, sev)!"
                            :class="`cell sev-${sev}`"
                            >{{ row.counts[severityIndex(sev)] }}</RouterLink
                          >
                          <span v-else :class="`cell sev-${sev}`">{{ row.counts[severityIndex(sev)] }}</span>
                        </template>
                        <span v-else class="muted">·</span>
                      </td>
                      <td class="num">{{ row.total }}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </section>
            <section class="panel" aria-labelledby="zx-hosts-title">
              <div id="zx-hosts-title" class="panel-header">Hosts</div>
              <dl class="panel-body facts">
                <dt>Monitored</dt>
                <dd>
                  <RouterLink :to="{ name: 'zabbix', params: { tab: 'hosts' }, query: { status: 'monitored' } }">{{
                    s.hosts.monitored
                  }}</RouterLink>
                  <span class="muted"> of {{ s.hosts.total }}</span>
                </dd>
                <dt>Unavailable</dt>
                <dd>
                  <RouterLink
                    :to="{ name: 'zabbix', params: { tab: 'hosts' }, query: { availability: 'Unavailable' } }"
                    :class="{ fail: s.hosts.unavailable }"
                    >{{ s.hosts.unavailable }}</RouterLink
                  >
                </dd>
                <dt>In maintenance</dt>
                <dd>
                  <RouterLink :to="{ name: 'zabbix', params: { tab: 'hosts' }, query: { status: 'maintenance' } }">{{
                    s.hosts.inMaintenance
                  }}</RouterLink>
                </dd>
                <dt>Disabled</dt>
                <dd>{{ s.hosts.disabled }}</dd>
                <dt>Attached to PNMT</dt>
                <dd>
                  <component
                    :is="canAttach ? 'RouterLink' : 'span'"
                    :to="canAttach ? { name: 'zabbix', params: { tab: 'suggestions' } } : undefined"
                    >{{ s.hosts.attached }} of {{ s.hosts.total }}</component
                  >
                </dd>
              </dl>
            </section>
          </div>
        </template>
      </template>

      <!-- Problems -->
      <template v-else-if="tab === 'problems'">
        <FilterBar v-model:search="problems.search.value" placeholder="Problem or host" :active="problemCount">
          <Select
            v-model="severity"
            :options="severityOptions"
            option-label="label"
            option-value="value"
            placeholder="Any severity"
            show-clear
            size="small"
            class="small-filter"
            aria-label="Severity"
          />
          <Select
            v-model="minSeverity"
            :options="severityOptions"
            option-label="label"
            option-value="value"
            placeholder="From severity"
            show-clear
            size="small"
            class="small-filter"
            aria-label="From severity"
          />
          <Select
            v-model="group"
            :options="groupOptions"
            option-label="label"
            option-value="value"
            placeholder="Any host group"
            show-clear
            filter
            size="small"
            class="filter"
            aria-label="Host group"
          />
          <InputText
            v-model="tag"
            size="small"
            placeholder="Tag, e.g. component:storage"
            aria-label="Tag"
            class="filter"
          />
          <Select
            v-model="acknowledged"
            :options="ackOptions"
            option-label="label"
            option-value="value"
            placeholder="Acknowledged or not"
            show-clear
            size="small"
            class="small-filter"
            aria-label="Acknowledged"
          />
          <Select
            v-model="olderThan"
            :options="olderOptions"
            option-label="label"
            option-value="value"
            placeholder="Any age"
            show-clear
            size="small"
            class="small-filter"
            aria-label="Age"
          />
          <label class="switch"><ToggleSwitch v-model="suppressed" /> Suppressed</label>
        </FilterBar>
        <ListTable v-model:expanded="expanded" :table="problems" what="problems" empty-text="No open problems." fixed>
          <Column expander style="width: 2.5rem" />
          <Column header="Severity" style="width: 7.5rem">
            <template #body="{ data }"><ZabbixSeverityTag :severity="data.severity" /></template>
          </Column>
          <Column header="Since" style="width: 7rem">
            <template #body="{ data }"><SinceText :since="data.since" /></template>
          </Column>
          <Column header="Host" style="width: 14rem">
            <template #body="{ data }">
              <span class="cell stack">
                <RouterLink
                  v-for="h in data.hosts"
                  :key="h.id"
                  :to="{ name: 'zabbix-host', params: { hostId: h.id } }"
                  class="ellipsis"
                  >{{ h.name }}</RouterLink
                >
                <RouterLink
                  v-if="data.pnmtHost"
                  :to="{ name: 'host', params: { id: data.pnmtHost.id } }"
                  class="muted ellipsis"
                  ><i class="pi pi-server" aria-hidden="true" /> {{ data.pnmtHost.name }}</RouterLink
                >
              </span>
            </template>
          </Column>
          <Column header="Problem">
            <template #body="{ data }">
              <span class="cell stack">
                <span class="ellipsis" :title="data.name">{{ data.name }}</span>
                <span v-if="data.operationalData" class="muted ellipsis small">{{ data.operationalData }}</span>
              </span>
            </template>
          </Column>
          <Column header="Tags" style="width: 14rem">
            <template #body="{ data }">
              <span class="tags cell">
                <button
                  v-for="t in data.tags.slice(0, 4)"
                  :key="`${t.tag}:${t.value}`"
                  type="button"
                  class="chip"
                  :title="`Only ${t.tag}: ${t.value}`"
                  @click="tag = t.value ? `${t.tag}:${t.value}` : t.tag"
                >
                  {{ t.tag }}{{ t.value ? `: ${t.value}` : '' }}
                </button>
              </span>
            </template>
          </Column>
          <Column header="Ack." style="width: 4.5rem">
            <template #body="{ data }">
              <span v-if="data.acknowledged" class="ack" :title="`Acknowledged, ${data.updates} update(s)`"
                ><i class="pi pi-check-circle" aria-label="Acknowledged" /> {{ data.updates }}</span
              >
            </template>
          </Column>
          <Column style="width: 5rem">
            <template #body="{ data }">
              <span class="row-actions">
                <SensorTicket v-if="data.sensorId" :sensor-id="data.sensorId" />
                <a
                  :href="data.url"
                  target="_blank"
                  rel="noopener noreferrer"
                  title="Open in Zabbix"
                  :aria-label="`${data.name} in Zabbix`"
                  ><i class="pi pi-external-link"
                /></a>
              </span>
            </template>
          </Column>
          <template #expansion="{ data }">
            <ProblemDetail :problem="data" />
          </template>
          <template #mobile="{ item }">
            <div class="mobile-item">
              <span class="line">
                <ZabbixSeverityTag :severity="item.severity" />
                <span class="ellipsis">{{ item.name }}</span>
              </span>
              <span class="muted small">
                {{ item.hosts.map((h: S['ZabbixHostRef']) => h.name).join(', ') }} · <SinceText :since="item.since" />
              </span>
            </div>
          </template>
        </ListTable>
      </template>

      <!-- Hosts -->
      <template v-else-if="tab === 'hosts'">
        <FilterBar v-model:search="hosts.search.value" placeholder="Name or address" :active="hostCount">
          <Select
            v-model="hostGroup"
            :options="groupOptions"
            option-label="label"
            option-value="value"
            placeholder="Any host group"
            show-clear
            filter
            size="small"
            class="filter"
            aria-label="Host group"
          />
          <Select
            v-model="hostStatus"
            :options="[
              { value: 'monitored', label: 'Monitored' },
              { value: 'disabled', label: 'Disabled' },
              { value: 'maintenance', label: 'In maintenance' },
            ]"
            option-label="label"
            option-value="value"
            placeholder="Any status"
            show-clear
            size="small"
            class="small-filter"
            aria-label="Status"
          />
          <Select
            v-model="availability"
            :options="[
              { value: 'Available', label: 'Available' },
              { value: 'Unavailable', label: 'Unavailable' },
              { value: 'Unknown', label: 'Unknown' },
            ]"
            option-label="label"
            option-value="value"
            placeholder="Any availability"
            show-clear
            size="small"
            class="small-filter"
            aria-label="Availability"
          />
          <Select
            v-model="attached"
            :options="[
              { value: 'yes', label: 'Attached to PNMT' },
              { value: 'no', label: 'Not attached' },
            ]"
            option-label="label"
            option-value="value"
            placeholder="Attached or not"
            show-clear
            size="small"
            class="small-filter"
            aria-label="Attached"
          />
        </FilterBar>
        <ListTable :table="hosts" what="hosts" fixed>
          <Column header="Name">
            <template #body="{ data }">
              <span class="cell stack">
                <RouterLink :to="{ name: 'zabbix-host', params: { hostId: data.id } }" class="ellipsis">{{
                  data.name
                }}</RouterLink>
                <span v-if="data.technicalName !== data.name" class="muted mono small ellipsis">{{
                  data.technicalName
                }}</span>
              </span>
            </template>
          </Column>
          <Column header="Groups" style="width: 12rem">
            <template #body="{ data }"
              ><span class="ellipsis cell" :title="data.groups.join(', ')">{{ data.groups.join(', ') }}</span></template
            >
          </Column>
          <Column header="Addresses" style="width: 11rem">
            <template #body="{ data }"
              ><span class="ellipsis cell mono small" :title="data.addresses.join(', ')">{{
                data.addresses.join(', ')
              }}</span></template
            >
          </Column>
          <Column header="Interfaces" style="width: 7rem">
            <template #body="{ data }">
              <span class="cell">
                <ZabbixInterfaces :interfaces="data.interfaces" />
                <i v-if="data.inMaintenance" class="pi pi-wrench muted" title="In maintenance" />
                <i v-if="!data.monitored" class="pi pi-ban muted" title="Disabled in Zabbix" />
              </span>
            </template>
          </Column>
          <Column header="Problems" style="width: 7rem">
            <template #body="{ data }">
              <RouterLink
                v-if="data.problems"
                :to="toProblems({ q: data.technicalName })"
                :class="`count sev-${data.worstSeverity}`"
                :title="`Worst: ${severityLabel(data.worstSeverity)}`"
                >{{ data.problems }}</RouterLink
              >
            </template>
          </Column>
          <Column header="PNMT" style="width: 11rem">
            <template #body="{ data }">
              <RouterLink
                v-if="data.pnmtHost"
                :to="{ name: 'host', params: { id: data.pnmtHost.id } }"
                class="ellipsis cell"
                >{{ data.pnmtHost.name }}</RouterLink
              >
              <Button v-else-if="canAttach" label="Attach…" size="small" text @click="attach(data)" />
            </template>
          </Column>
          <template #mobile="{ item }">
            <div class="mobile-item">
              <span class="line">
                <RouterLink :to="{ name: 'zabbix-host', params: { hostId: item.id } }">{{ item.name }}</RouterLink>
                <ZabbixInterfaces :interfaces="item.interfaces" />
              </span>
              <span class="muted small">{{ item.groups.join(', ') }}</span>
            </div>
          </template>
        </ListTable>
        <AttachDialog v-if="attaching" v-model:visible="attachOpen" :host="attaching" />
      </template>

      <!-- Suggestions -->
      <template v-else>
        <ErrorState
          v-if="suggestions.isError.value"
          :error="suggestions.error.value"
          what="the suggestions"
          @retry="suggestions.refetch()"
        />
        <EmptyState
          v-else-if="suggestions.data.value && !suggestions.data.value.length"
          icon="pi pi-check"
          title="Every Zabbix host is attached."
        />
        <section v-else-if="suggestions.data.value" class="panel" aria-label="Suggestions">
          <div class="panel-header">
            {{ suggestions.data.value.length }} unattached
            <span class="muted count"
              >· {{ suggestions.data.value.filter((x) => x.suggested).length }} with a match</span
            >
            <Button
              class="attach-all"
              :label="`Attach ${selected.length}`"
              icon="pi pi-link"
              size="small"
              :disabled="!selected.length"
              @click="attachSelected"
            />
          </div>
          <ul class="panel-body suggestions">
            <li v-for="x in suggestions.data.value" :key="x.host.id">
              <Checkbox
                v-model="picked[x.host.id]"
                binary
                :input-id="`zs-${x.host.id}`"
                :aria-label="`Attach ${x.host.name}`"
              />
              <span class="main">
                <RouterLink :to="{ name: 'zabbix-host', params: { hostId: x.host.id } }">{{ x.host.name }}</RouterLink>
                <span class="muted small">
                  <span v-if="x.host.technicalName !== x.host.name" class="mono">{{ x.host.technicalName }} · </span
                  >{{ x.host.addresses.join(', ') || 'no address' }}
                </span>
              </span>
              <Select
                v-model="choice[x.host.id]"
                :options="optionsOf(x)"
                option-label="label"
                option-value="value"
                :placeholder="
                  x.candidates.length > 1 ? `${x.candidates.length} candidates: pick one` : 'No match: pick'
                "
                size="small"
                class="choice"
                :aria-label="`PNMT host for ${x.host.name}`"
                @change="picked[x.host.id] = true"
              />
            </li>
          </ul>
        </section>
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
  min-width: 8rem;
  flex: 1;
}

.tile:last-child {
  border-right: none;
}

.tile:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.tile.empty {
  opacity: 0.6;
}

.big {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-size: 1.5rem;
  font-weight: 600;
}

.dot {
  width: 0.7rem;
  height: 0.7rem;
  border-radius: 50%;
}

.small {
  font-size: 0.85em;
}

.bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
}

.stale {
  color: var(--pnmt-fail);
}

.overview {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 18rem;
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 999px) {
  .overview {
    grid-template-columns: minmax(0, 1fr);
  }
}

.category {
  margin-left: auto;
  font-weight: 400;
}

.panel-body:has(.matrix) {
  overflow-x: auto;
}

.matrix {
  border-collapse: collapse;
  width: 100%;
}

.matrix th,
.matrix td {
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  text-align: left;
}

.matrix .num {
  text-align: center;
  width: 6.5rem;
}

.cell.sev-Disaster,
.cell.sev-High,
.cell.sev-Average,
.cell.sev-Warning,
.cell.sev-Information,
.cell.sev-NotClassified,
.count {
  display: inline-block;
  min-width: 2rem;
  padding: 0 0.4em;
  border-radius: 4px;
  color: #111;
  font-weight: 600;
  text-align: center;
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

.stack {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.chip {
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  background: none;
  color: inherit;
  padding: 0 var(--pnmt-space-2);
  font-size: 0.8em;
  cursor: pointer;
  white-space: nowrap;
}

.ack {
  color: var(--pnmt-ok);
  white-space: nowrap;
}

.row-actions {
  display: inline-flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.fail {
  color: var(--pnmt-fail);
}

.warn-text {
  color: var(--pnmt-warn);
  margin: 0;
}

.mobile-item {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.mobile-item .line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.attach-all {
  margin-left: auto;
}

.panel-header .count {
  font-weight: 400;
}

.suggestions {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.suggestions li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.suggestions .main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.choice {
  width: min(24rem, 45vw);
}
</style>
