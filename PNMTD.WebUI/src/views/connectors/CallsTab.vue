<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, onScopeDispose, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type ConnectorCallFilters, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import ConnectorCallDialog from './ConnectorCallDialog.vue'
import { duration, isActive, modeLabel, outcomeLabel, stateSeverity } from './connectorFormat'

/**
 * *Calls*, the area's main view (docs/connectors-plan.md §13.2): every call of every connector, the active ones first, as the worker
 * takes them, then the processed ones, newest first; refreshed every 5 s. The filters are in the address. On top, the calls running
 * against the limit over all connectors and the last 24 hours. A connector's own *Calls* tab is this view with the connector set.
 */
const props = defineProps<{ connectorId?: string }>()
const route = useRoute()
const router = useRouter()
const text = (k: string) => (typeof route.query[k] === 'string' ? (route.query[k] as string) : '')
const show = ref<'all' | 'active' | 'processed'>((text('show') as 'active' | 'processed') || 'all')
const state = ref<S['ConnectorCallState'] | null>((text('state') as S['ConnectorCallState']) || null)
const callerId = ref<string | null>(text('caller') || null)
const connectorFilter = ref<string | null>(text('connector') || null)
const from = ref(text('from'))
const reference = ref(text('reference'))
const refusedBy = ref(text('refusedBy'))
const filters = computed<ConnectorCallFilters>(() => ({
  connectorId: props.connectorId ?? connectorFilter.value ?? undefined,
  callerId: callerId.value ?? undefined,
  active: show.value === 'all' ? undefined : show.value === 'active',
  state: state.value ?? undefined,
  from: from.value.trim() || undefined,
  reference: reference.value.trim() || undefined,
  refusedBy: refusedBy.value.trim() || undefined,
}))
watch(
  [show, state, callerId, connectorFilter, from, reference, refusedBy],
  () => {
    const query: Record<string, string> = {}
    for (const [k, v] of Object.entries({
      show: show.value === 'all' ? '' : show.value,
      state: state.value ?? '',
      caller: callerId.value ?? '',
      connector: props.connectorId ? '' : (connectorFilter.value ?? ''),
      from: from.value,
      reference: reference.value,
      refusedBy: refusedBy.value,
    }))
      if (v) query[k] = v
    void router.replace({ query })
  },
  { deep: true },
)
const table = useServerTable<S['ConnectorCallItem']>({
  key: [...keys.connectors, 'calls'],
  fetch: (q) => api.connectors.calls(q),
  filters: computed(() => ({ ...filters.value })),
  pageSize: 50,
  refetchInterval: 5000,
})
const figures = useQuery({
  queryKey: [...keys.connectors, 'figures'],
  queryFn: api.connectors.figures,
  refetchInterval: 5000,
})
const callers = useQuery({
  queryKey: [...keys.connectors, 'callers', 'all'],
  queryFn: () => api.connectors.callers({ pageSize: 500 }),
})
const connectors = useQuery({
  queryKey: [...keys.connectors, 'list', 'all'],
  queryFn: () => api.connectors.list({ pageSize: 500 }),
  enabled: computed(() => !props.connectorId),
})
const states: S['ConnectorCallState'][] = [
  'Queued',
  'Running',
  'Done',
  'Failed',
  'TimedOut',
  'Offline',
  'Lost',
  'Refused',
  'Expired',
  'Cancelled',
  'Checked',
]
const shows = [
  { label: 'All', value: 'all' },
  { label: 'Active', value: 'active' },
  { label: 'Processed', value: 'processed' },
]
const activeCount = computed(() => (figures.data.value ? figures.data.value.running + figures.data.value.queued : null))
const selected = ref<string | null>(null)
// *Running for* as a live counter, every second while the tab is open.
const now = ref(Date.now())
const ticker = setInterval(() => (now.value = Date.now()), 1000)
onScopeDispose(() => clearInterval(ticker))
const runningFor = (c: S['ConnectorCallItem']) =>
  c.startedAt ? duration((now.value - Date.parse(c.startedAt)) / 1000) : ''
</script>

<template>
  <div class="tab">
    <section v-if="figures.data.value" class="figures" aria-label="The last 24 hours">
      <div class="figure">
        <span class="value">{{ figures.data.value.running }} of {{ figures.data.value.maxRunning }}</span>
        <span class="muted small">running</span>
      </div>
      <div class="figure">
        <span class="value">{{ figures.data.value.queued }}</span><span class="muted small">queued</span>
      </div>
      <div class="figure">
        <span class="value">{{ figures.data.value.calls24h }}</span><span class="muted small">calls in 24 h</span>
      </div>
      <div class="figure">
        <span class="value">{{ figures.data.value.refused24h }}</span><span class="muted small">refused</span>
      </div>
      <div class="figure">
        <span class="value">{{ figures.data.value.failed24h }}</span><span class="muted small">failed runs</span>
      </div>
      <div class="figure">
        <span class="value">{{ figures.data.value.expired24h }}</span><span class="muted small">expired</span>
      </div>
      <div class="figure">
        <span class="value">{{ duration(figures.data.value.longestWaitSeconds24h) || '–' }}</span><span
          class="muted small"
          >longest wait</span
        >
      </div>
      <div v-if="figures.data.value.sensor" class="figure sensor">
        <RouterLink :to="{ name: 'sensor', params: { id: figures.data.value.sensor.id } }">
          <Tag
            :value="figures.data.value.sensorState === 'Failing' ? 'refusals reported' : 'nothing reported'"
            :severity="figures.data.value.sensorState === 'Failing' ? 'danger' : 'success'"
          />
        </RouterLink>
        <span v-if="figures.data.value.sensorMessage" class="small">{{ figures.data.value.sensorMessage }}</span>
      </div>
    </section>
    <p v-if="figures.data.value && !figures.data.value.enabled" class="muted">
      Connectors are off (Connectors__Enabled=false): /connector answers 404, and nothing is queued.
    </p>

    <div class="filters">
      <SelectButton
        v-model="show"
        :options="shows"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      />
      <Select v-model="state" :options="states" placeholder="Any state" show-clear aria-label="State" size="small" />
      <Select
        v-if="!connectorId"
        v-model="connectorFilter"
        :options="connectors.data.value?.items ?? []"
        option-label="name"
        option-value="id"
        placeholder="Any connector"
        show-clear
        filter
        aria-label="Connector"
        size="small"
      />
      <Select
        v-model="callerId"
        :options="callers.data.value?.items ?? []"
        option-label="name"
        option-value="id"
        placeholder="Any caller"
        show-clear
        filter
        aria-label="Caller"
        size="small"
      />
      <InputText v-model="from" placeholder="From (address or network)" aria-label="From" size="small" />
      <InputText v-model="reference" placeholder="Reference" aria-label="Reference" size="small" />
      <InputText v-model="refusedBy" placeholder="Refused by" aria-label="Refused by" size="small" />
      <span v-if="activeCount !== null" class="muted small">Active ({{ activeCount }})</span>
    </div>

    <ListTable :table="table" what="calls" selectable empty-text="No call yet." @select="(c) => (selected = c.id)">
      <Column header="Received" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.receivedAt" /></template>
      </Column>
      <Column header="State" style="width: 13rem">
        <template #body="{ data }">
          <Tag :value="outcomeLabel(data)" :severity="stateSeverity(data.state, data.exitCode)" />
          <span v-if="data.exitCode !== null && !isActive(data.state)" class="mono small"> {{ data.exitCode }}</span>
          <div v-if="data.state === 'Queued'" class="small muted">
            {{ data.waitingText }}<template v-if="data.place"> · #{{ data.place }} in line</template>
          </div>
          <div v-else-if="data.state === 'Running'" class="small muted">running for {{ runningFor(data) }}</div>
          <div class="tags">
            <Tag v-if="data.reviewState === 'unreviewed'" value="unreviewed" severity="warn" class="small-tag" />
            <Tag v-if="data.collected" value="collected" severity="secondary" class="small-tag" />
            <Tag v-if="data.dryRun" value="dry run" severity="secondary" class="small-tag" />
          </div>
        </template>
      </Column>
      <Column v-if="!connectorId" header="Connector">
        <template #body="{ data }">
          <RouterLink
            v-if="data.connector"
            :to="{ name: 'connector', params: { id: data.connector.id } }"
            @click.stop
            >{{ data.connectorName }}</RouterLink
          >
          <span v-else>{{ data.connectorName }}</span>
          <span class="muted small"> · {{ modeLabel(data.mode) }}</span>
        </template>
      </Column>
      <Column header="Caller">
        <template #body="{ data }">
          <RouterLink
            v-if="data.caller"
            :to="{ name: 'connector-caller', params: { id: data.caller.id } }"
            @click.stop
            >{{ data.callerName }}</RouterLink
          >
          <span v-else>{{ data.callerName }}</span>
          <div class="small muted mono">{{ data.from }}</div>
        </template>
      </Column>
      <Column header="Reference" style="width: 10rem">
        <template #body="{ data }"
          ><span class="small">{{ data.reference }}</span></template
        >
      </Column>
      <Column header="Server" style="width: 12rem">
        <template #body="{ data }"
          ><span class="small">{{ data.serverName }}</span></template
        >
      </Column>
      <Column header="Queue · run" style="width: 9rem">
        <template #body="{ data }">
          <span class="small">{{ duration(data.queuedSeconds) }}</span>
          <span v-if="data.durationMs !== null" class="small muted"> · {{ duration(data.durationMs / 1000) }}</span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="small">
          <Tag :value="outcomeLabel(item)" :severity="stateSeverity(item.state, item.exitCode)" />
          {{ item.connectorName }}
        </div>
        <div class="small muted">
          {{ item.callerName }} · {{ item.from }} · <RelativeTime :value="item.receivedAt" />
        </div>
      </template>
    </ListTable>
    <p v-if="figures.data.value" class="muted small">
      The newest {{ figures.data.value.keepCalls.toLocaleString() }} calls are kept (Connectors__KeepCalls); active
      calls always.
    </p>
    <ConnectorCallDialog :call-id="selected" @close="selected = null" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.figures {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-4);
  padding: var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
}

.figure {
  display: flex;
  flex-direction: column;
}

.figure .value {
  font-size: 1.25rem;
  font-weight: 600;
}

.figure.sensor {
  margin-left: auto;
  max-width: 28rem;
  gap: var(--pnmt-space-1);
}

.filters {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.tags {
  display: flex;
  gap: var(--pnmt-space-1);
  margin-top: 2px;
}

.small-tag {
  font-size: 0.75rem;
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
