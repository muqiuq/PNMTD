<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Chip from 'primevue/chip'
import Column from 'primevue/column'
import DatePicker from 'primevue/datepicker'
import Drawer from 'primevue/drawer'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import ToggleButton from 'primevue/togglebutton'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type ListQuery, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useStatusCodes } from '@/composables/useLookups'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'

/**
 * Every event (§5.6): a time preset, the status, a host or a sensor, text; the newest first, paged
 * by the server. "Live" refreshes every 10 s. A row opens its details.
 */
const route = useRoute()
const router = useRouter()
const codes = useStatusCodes()

const ranges = [
  { label: '1 h', value: '1h', ms: 3_600_000 },
  { label: '24 h', value: '24h', ms: 86_400_000 },
  { label: '7 d', value: '7d', ms: 7 * 86_400_000 },
  { label: '30 d', value: '30d', ms: 30 * 86_400_000 },
  { label: 'All', value: 'all', ms: 0 },
  { label: 'Custom', value: 'custom', ms: 0 },
]
const statuses = [
  { label: 'All', value: '' },
  { label: 'Failed', value: 'failed' },
  { label: 'OK', value: 'ok' },
]
const fromQuery = (k: string) => (typeof route.query[k] === 'string' ? (route.query[k] as string) : '')
const range = ref(fromQuery('range') || '24h')
const status = ref(fromQuery('status'))
const hostId = ref(fromQuery('hostId'))
const sensorId = ref(fromQuery('sensorId'))
const live = ref(false)
// Custom: from a day to a day (the end day included), kept in the URL as from/to.
const parseDay = (v: string) => (v ? new Date(v) : null)
const custom = ref<(Date | null)[] | null>(
  fromQuery('from') ? [parseDay(fromQuery('from')), parseDay(fromQuery('to'))] : null,
)
const customFrom = computed(() => custom.value?.[0] ?? null)
const customTo = computed(() => {
  const end = custom.value?.[1] ?? custom.value?.[0] ?? null
  return end ? new Date(end.getFullYear(), end.getMonth(), end.getDate() + 1) : null
})

watch([range, status, hostId, sensorId, custom], () =>
  router.replace({
    query: {
      ...route.query,
      range: range.value === '24h' ? undefined : range.value,
      status: status.value || undefined,
      hostId: hostId.value || undefined,
      sensorId: sensorId.value || undefined,
      from: range.value === 'custom' && customFrom.value ? customFrom.value.toISOString() : undefined,
      to: range.value === 'custom' && custom.value?.[1] ? custom.value[1].toISOString() : undefined,
      page: undefined,
    },
  }),
)

const hosts = useQuery({
  queryKey: [...keys.hosts, 'all'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
})
const sensor = useQuery({
  queryKey: computed(() => keys.sensor(sensorId.value)),
  queryFn: () => api.sensors.get(sensorId.value),
  enabled: computed(() => !!sensorId.value),
})

const filters = computed(() => ({
  range: range.value,
  customFrom: range.value === 'custom' ? customFrom.value?.toISOString() : undefined,
  customTo: range.value === 'custom' ? customTo.value?.toISOString() : undefined,
  status: status.value || undefined,
  hostId: sensorId.value ? undefined : hostId.value || undefined,
  sensorId: sensorId.value || undefined,
}))

const table = useServerTable<S['EventItem']>({
  key: keys.events,
  fetch: ({ range: r, customFrom: cf, customTo: ct, ...q }) => {
    if (r === 'custom') {
      return api.events.list({ ...(q as ListQuery), from: cf as string | undefined, to: ct as string | undefined })
    }
    const ms = ranges.find((x) => x.value === r)?.ms ?? 0
    return api.events.list({ ...(q as ListQuery), from: ms ? new Date(Date.now() - ms).toISOString() : undefined })
  },
  defaultSort: '-created',
  filters,
  syncUrl: true,
  refetchInterval: computed(() => (live.value ? 10_000 : 30_000)),
})
watch(live, (on) => {
  if (on) {
    void table.query.refetch()
  }
})

const active = computed(() => (status.value ? 1 : 0) + (hostId.value ? 1 : 0) + (range.value !== '24h' ? 1 : 0))
const selected = ref<S['EventItem'] | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
</script>

<template>
  <div class="page">
    <PageHeader title="Events">
      <template #status>
        <span class="muted">{{ table.total.value.toLocaleString() }}</span>
      </template>
      <template #actions>
        <ToggleButton
          v-model="live"
          on-label="Live"
          off-label="Live"
          on-icon="pi pi-circle-fill"
          off-icon="pi pi-circle"
          size="small"
          aria-label="Live: refresh every 10 seconds"
        />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search messages" :active="active">
      <SelectButton
        v-model="range"
        :options="ranges"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Time range"
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
        class="range-picker"
        aria-label="Days"
      />
      <SelectButton
        v-model="status"
        :options="statuses"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Status"
      />
      <Chip
        v-if="sensorId"
        :label="sensor.data.value ? `${sensor.data.value.host.name} / ${sensor.data.value.name}` : 'Sensor'"
        removable
        @remove="sensorId = ''"
      />
      <Select
        v-else
        v-model="hostId"
        :options="hosts.data.value?.items ?? []"
        option-label="name"
        option-value="id"
        placeholder="All hosts"
        show-clear
        filter
        size="small"
        class="host-select"
        aria-label="Host"
      />
    </FilterBar>
    <ListTable
      :table="table"
      what="events"
      empty-text="No events in this range."
      selectable
      @select="(e) => (selected = e)"
    >
      <Column field="created" header="Time" sortable style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.created" /></template>
      </Column>
      <Column header="Host / sensor" style="width: 18rem">
        <template #body="{ data }">
          <span class="who ellipsis">
            <RouterLink :to="{ name: 'host', params: { id: data.hostId } }" @click.stop>{{ data.hostName }}</RouterLink>
            <span class="muted"> / </span>
            <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }" @click.stop>{{
              data.sensorName
            }}</RouterLink>
          </span>
        </template>
      </Column>
      <Column field="code" header="Code" sortable style="width: 13rem">
        <template #body="{ data }"><CodeTag :code="data.code" :name="codes.nameOf(data.code)" /></template>
      </Column>
      <Column header="Message">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.message ?? ''">{{ data.message }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="mobile-event">
          <div class="line">
            <CodeTag :code="item.code" compact />
            <span class="ellipsis">{{ item.hostName }} / {{ item.sensorName }}</span>
            <RelativeTime :value="item.created" class="muted right" />
          </div>
          <div v-if="item.message" class="muted ellipsis">{{ item.message }}</div>
        </div>
      </template>
    </ListTable>

    <Drawer v-model:visible="open" position="right" header="Event" class="event-drawer">
      <dl v-if="selected" class="facts">
        <dt>Time</dt>
        <dd>{{ formatAbsolute(selected.created) }} · <RelativeTime :value="selected.created" /></dd>
        <dt>Host</dt>
        <dd>
          <RouterLink :to="{ name: 'host', params: { id: selected.hostId } }">{{ selected.hostName }}</RouterLink>
        </dd>
        <dt>Sensor</dt>
        <dd>
          <RouterLink :to="{ name: 'sensor', params: { id: selected.sensorId } }">{{ selected.sensorName }}</RouterLink>
        </dd>
        <dt>Code</dt>
        <dd><CodeTag :code="selected.code" :name="codes.nameOf(selected.code)" /></dd>
        <dt>Source</dt>
        <dd class="mono">{{ selected.source ?? '–' }}</dd>
        <dt>Message</dt>
        <dd>
          <div class="mono message-box">{{ selected.message ?? '(none)' }}</div>
        </dd>
      </dl>
      <Button
        v-if="selected"
        label="This sensor's events"
        text
        size="small"
        @click="((sensorId = selected.sensorId), (selected = null))"
      />
    </Drawer>
  </div>
</template>

<style scoped>
.range-picker {
  width: 13rem;
}

.host-select {
  width: 12rem;
}

.who {
  display: block;
  max-width: 18rem;
}

.cell {
  display: block;
  max-width: 40rem;
}

.mobile-event .line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.right {
  margin-left: auto;
  white-space: nowrap;
}

.message-box {
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.facts {
  align-items: start;
}
</style>
