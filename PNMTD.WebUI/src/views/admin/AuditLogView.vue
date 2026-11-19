<script setup lang="ts">
import Button from 'primevue/button'
import Chip from 'primevue/chip'
import Column from 'primevue/column'
import DatePicker from 'primevue/datepicker'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type AuditFilters, type S } from '@/api/resources'
import AuditChanges from '@/components/AuditChanges.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useAuditCatalog } from '@/composables/useAuditCatalog'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import AccessReviewBadge from '@/views/cookie/AccessReviewBadge.vue'

/**
 * The audit log (docs/audit-redmine-zabbix-plan.md §4): every operation, by whom and from where, with its outcome and its
 * changes; newest first. The filters live in the URL; the detail takes the width the other columns leave, and a row opens
 * with everything, the changes field by field. *Download CSV* exports what the filters show, and is an entry itself. A
 * sign-in the access watch reviewed shows its verdict, linking to its case (docs/cookie-access-plan.md §10.4).
 */
const route = useRoute()
const router = useRouter()
const { catalog, label, severity } = useAuditCatalog()

const fromQuery = (k: string) => (typeof route.query[k] === 'string' ? (route.query[k] as string) : '')
const listQuery = (k: string) => {
  const v = route.query[k]
  return (Array.isArray(v) ? v : v ? [v] : []).filter((x): x is string => typeof x === 'string')
}

const categories = ref<S['AuditCategory'][]>(listQuery('categories') as S['AuditCategory'][])
const type = ref(fromQuery('type'))
const outcome = ref<S['AuditOutcome'] | ''>(fromQuery('outcome') as S['AuditOutcome'] | '')
const actor = ref<S['AuditActor'] | ''>(fromQuery('actor') as S['AuditActor'] | '')
const userId = ref(fromQuery('userId'))
const userName = ref(fromQuery('user'))
const subject = ref(fromQuery('subject'))
const subjectName = ref(fromQuery('subjectName'))
const correlationId = ref(fromQuery('correlationId'))
const range = ref(fromQuery('range') || 'all')
const custom = ref<(Date | null)[] | null>(
  fromQuery('from') ? [new Date(fromQuery('from')), fromQuery('to') ? new Date(fromQuery('to')) : null] : null,
)

const ranges = [
  { label: 'All time', value: 'all', ms: 0 },
  { label: 'Last 24 hours', value: '24h', ms: 24 * 3600_000 },
  { label: 'Last 7 days', value: '7d', ms: 7 * 24 * 3600_000 },
  { label: 'Last 30 days', value: '30d', ms: 30 * 24 * 3600_000 },
  { label: 'Custom…', value: 'custom', ms: 0 },
]
const categoryOptions = computed(() =>
  [...new Set((catalog.data.value ?? []).map((t) => t.category))].sort().map((c) => ({ value: c, label: c })),
)
// Events grouped by category, narrowed to the chosen categories.
const typeGroups = computed(() =>
  categoryOptions.value
    .filter((c) => categories.value.length === 0 || categories.value.includes(c.value))
    .map((c) => ({
      label: c.label,
      items: (catalog.data.value ?? [])
        .filter((t) => t.category === c.value)
        .map((t) => ({ value: t.type, label: t.label }))
        .sort((a, b) => a.label.localeCompare(b.label)),
    })),
)
const outcomes = ['Succeeded', 'Failed', 'Denied'].map((o) => ({ value: o, label: o }))
const actors = [
  { value: 'User', label: 'A user' },
  { value: 'Cli', label: 'The CLI' },
  { value: 'Agent', label: 'An agent' },
  { value: 'System', label: 'PNMT itself' },
  { value: 'Mcp', label: 'An MCP token' },
  // docs/connectors-plan.md §12.2: an outside system's call to a connector.
  { value: 'Connector', label: 'A connector’s caller' },
]

const timeFilter = computed(() => {
  const r = ranges.find((x) => x.value === range.value)
  if (r && r.ms > 0) return { from: new Date(Date.now() - r.ms).toISOString() }
  if (range.value === 'custom' && custom.value?.[0]) {
    const end = custom.value[1] ?? custom.value[0]
    const to = new Date(end)
    to.setHours(24, 0, 0, 0)
    return { from: custom.value[0].toISOString(), to: to.toISOString() }
  }
  return {}
})

const filters = computed<AuditFilters>(() => ({
  type: type.value || undefined,
  categories: categories.value.length ? categories.value : undefined,
  outcome: outcome.value || undefined,
  actor: actor.value || undefined,
  userId: userId.value || undefined,
  subject: subject.value || undefined,
  correlationId: correlationId.value || undefined,
  ...timeFilter.value,
}))
watch([type, categories, outcome, actor, userId, subject, correlationId, range, custom], () =>
  router.replace({
    query: {
      ...route.query,
      type: type.value || undefined,
      categories: categories.value.length ? categories.value : undefined,
      outcome: outcome.value || undefined,
      actor: actor.value || undefined,
      userId: userId.value || undefined,
      user: userId.value ? userName.value : undefined,
      subject: subject.value || undefined,
      subjectName: subject.value ? subjectName.value : undefined,
      correlationId: correlationId.value || undefined,
      range: range.value === 'all' ? undefined : range.value,
      from: range.value === 'custom' ? timeFilter.value.from : undefined,
      to: range.value === 'custom' ? timeFilter.value.to : undefined,
      page: undefined,
    },
  }),
)
const active = computed(
  () =>
    [type.value, outcome.value, actor.value, userId.value, subject.value, correlationId.value].filter(Boolean).length +
    (categories.value.length ? 1 : 0) +
    (range.value === 'all' ? 0 : 1),
)

const table = useServerTable<S['AuditLogItem']>({
  key: [...keys.audit, 'log'],
  fetch: (q) => api.audit.log({ ...q, ...filters.value }),
  filters: computed(() => ({ ...filters.value })),
  syncUrl: true,
})
const expanded = ref<Record<string, boolean>>({})
const exportUrl = computed(() => api.audit.exportUrl({ ...filters.value, search: table.search.value || undefined }))

const who = (e: S['AuditLogItem']) =>
  e.actor === 'User'
    ? e.username || '–'
    : e.actor === 'Cli'
      ? 'CLI'
      : e.actor === 'System'
        ? 'PNMT'
        : e.actor === 'Connector'
          ? `${e.username || 'a caller'} (connector)`
          : e.actor
const targetLink = (e: S['AuditLogItem']) => {
  if (!e.targetId) return null
  switch (e.targetType) {
    case 'Host':
      return { name: 'host', params: { id: e.targetId } }
    case 'Sensor':
      return { name: 'sensor', params: { id: e.targetId } }
    case 'Location':
      return { name: 'location', params: { id: e.targetId } }
    case 'User':
      return { name: 'audit-log', query: { userId: e.targetId, user: e.targetName ?? '' } }
    case 'Group':
      return { name: 'group', params: { id: e.targetId } }
    default:
      return null
  }
}
const showUser = (e: S['AuditLogItem']) => {
  if (!e.userId) return
  userName.value = e.username
  userId.value = e.userId
}
const showRequest = (e: S['AuditLogItem']) => {
  if (e.correlationId) correlationId.value = e.correlationId
}
</script>

<template>
  <div class="page">
    <PageHeader title="Audit log">
      <template #actions>
        <Button
          as="a"
          :href="exportUrl"
          label="Download CSV"
          icon="pi pi-download"
          size="small"
          severity="secondary"
          outlined
        />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search user, target or detail" :active="active">
      <MultiSelect
        v-model="categories"
        :options="categoryOptions"
        option-label="label"
        option-value="value"
        placeholder="All categories"
        :max-selected-labels="2"
        size="small"
        class="filter"
        aria-label="Categories"
      />
      <Select
        v-model="type"
        :options="typeGroups"
        option-group-label="label"
        option-group-children="items"
        option-label="label"
        option-value="value"
        placeholder="All events"
        show-clear
        filter
        size="small"
        class="filter"
        aria-label="Event type"
      />
      <Select
        v-model="outcome"
        :options="outcomes"
        option-label="label"
        option-value="value"
        placeholder="Any outcome"
        show-clear
        size="small"
        class="small-filter"
        aria-label="Outcome"
      />
      <Select
        v-model="actor"
        :options="actors"
        option-label="label"
        option-value="value"
        placeholder="Anyone"
        show-clear
        size="small"
        class="small-filter"
        aria-label="Who"
      />
      <Select
        v-model="range"
        :options="ranges"
        option-label="label"
        option-value="value"
        size="small"
        class="small-filter"
        aria-label="Time"
      />
      <DatePicker
        v-if="range === 'custom'"
        v-model="custom"
        selection-mode="range"
        :manual-input="false"
        :max-date="new Date()"
        show-button-bar
        size="small"
        placeholder="From – to"
        aria-label="Days"
      />
      <Chip v-if="userId" :label="userName || 'One user'" removable @remove="((userId = ''), (userName = ''))" />
      <Chip
        v-if="subject"
        :label="subjectName ? `${subjectName}, and what belongs to it` : 'One object'"
        removable
        @remove="((subject = ''), (subjectName = ''))"
      />
      <Chip v-if="correlationId" label="One request" removable @remove="correlationId = ''" />
    </FilterBar>
    <ListTable v-model:expanded="expanded" :table="table" what="entries" fixed>
      <Column expander style="width: 2.5rem" />
      <Column header="Time" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.createdAt" /></template>
      </Column>
      <Column header="User" style="width: 10rem">
        <template #body="{ data }">
          <a v-if="data.userId" href="#" class="ellipsis cell" @click.prevent="showUser(data)">{{ who(data) }}</a>
          <span v-else class="ellipsis cell muted">{{ who(data) }}</span>
        </template>
      </Column>
      <Column header="Event" style="width: 13rem">
        <template #body="{ data }">
          <span class="event">
            <Tag :value="label(data.eventType)" :severity="severity(data.eventType)" />
            <Tag v-if="data.outcome !== 'Succeeded'" :value="data.outcome" severity="danger" />
            <AccessReviewBadge v-if="data.review" :review="data.review" />
          </span>
        </template>
      </Column>
      <Column header="Target" style="width: 14rem">
        <template #body="{ data }">
          <RouterLink
            v-if="targetLink(data)"
            :to="targetLink(data)!"
            class="ellipsis cell"
            :title="data.targetName ?? ''"
            >{{ data.targetName ?? data.targetId }}</RouterLink
          >
          <span v-else class="ellipsis cell" :title="data.targetName ?? ''">{{ data.targetName }}</span>
        </template>
      </Column>
      <Column header="Detail">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.detail ?? ''">{{ data.detail }}</span></template
        >
      </Column>
      <Column header="Address" style="width: 9rem">
        <template #body="{ data }"
          ><span class="mono ellipsis cell">{{ data.ipAddress }}</span></template
        >
      </Column>
      <template #expansion="{ data }">
        <div class="details">
          <p v-if="data.detail" class="wrap">{{ data.detail }}</p>
          <AuditChanges v-if="data.changes.length" :changes="data.changes" />
          <dl class="facts">
            <dt>When</dt>
            <dd>{{ formatAbsolute(data.createdAt) }}</dd>
            <dt>Outcome</dt>
            <dd>
              {{ data.outcome }}<template v-if="data.error"> · {{ data.error }}</template>
            </dd>
            <template v-if="data.request">
              <dt>Request</dt>
              <dd class="mono">{{ data.request }}</dd>
            </template>
            <template v-if="data.actorRef">
              <dt>By</dt>
              <dd>{{ data.actorRef }}</dd>
            </template>
            <template v-if="data.userAgent">
              <dt>Browser</dt>
              <dd class="muted">{{ data.userAgent }}</dd>
            </template>
          </dl>
          <a v-if="data.correlationId && !correlationId" href="#" @click.prevent="showRequest(data)"
            >Every entry of this request ›</a
          >
        </div>
      </template>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="label(item.eventType)" :severity="severity(item.eventType)" />
          <Tag v-if="item.outcome !== 'Succeeded'" :value="item.outcome" severity="danger" />
          <AccessReviewBadge v-if="item.review" :review="item.review" />
          <span>{{ who(item) }}</span>
          <RelativeTime :value="item.createdAt" class="muted right" />
        </div>
        <div v-if="item.targetName" class="ellipsis">{{ item.targetName }}</div>
        <div v-if="item.detail" class="muted clamp">{{ item.detail }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.filter {
  width: 13rem;
}

.small-filter {
  width: 10rem;
}

/* Cut at the column's edge: the table's layout is fixed, and Detail takes what the others leave (§4.1). */
.cell {
  display: block;
}

.event {
  display: inline-flex;
  gap: var(--pnmt-space-1);
  flex-wrap: wrap;
}

.details {
  display: grid;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-2) 0;
  max-width: 70rem;
}

.wrap {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.right {
  margin-left: auto;
}

.clamp {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
</style>
