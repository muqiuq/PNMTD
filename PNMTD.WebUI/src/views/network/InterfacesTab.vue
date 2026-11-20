<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { attachedLabel, matchLabel, sourceLabels } from './networkFormat'

/**
 * Network interfaces (docs/network-map-plan.md §8.6), Details only: every MAC the routers see, where it is attached, and what it
 * is matched to (a server's adapter, a router's port, a host by hand). PNMT discovers interfaces, not hosts (decision 6): an
 * interface is assigned to a host that exists, and the suggestions follow.
 */
type Row = S['NetworkInterfaceItem'] & { id: string }
const route = useRoute()
const session = useSession()
const { run, done } = useActions()
const unassigned = ref(route.query.unassigned === '1')
const showIgnored = ref(false)
const source = ref<string | null>(null)
const locationId = ref<string | null>(null)
const routerId = ref<string | null>(typeof route.query.router === 'string' ? route.query.router : null)
const hostId = ref<string | null>(typeof route.query.host === 'string' ? route.query.host : null)
const filters = computed(() => ({
  unassigned: unassigned.value || undefined,
  showIgnored: showIgnored.value || undefined,
  source: source.value ?? undefined,
  locationId: locationId.value ?? undefined,
  routerId: routerId.value ?? undefined,
  hostId: hostId.value ?? undefined,
}))
const table = useServerTable<Row>({
  key: [...keys.network, 'interfaces'],
  fetch: async (q) => {
    const page = await api.network.interfaces(q)
    return { ...page, items: page.items.map((i) => ({ ...i, id: i.mac })) }
  },
  defaultSort: '-lastseen',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const edit = computed(() => session.can('NetworkMap', 'Edit'))
const active = computed(
  () =>
    (unassigned.value ? 1 : 0) +
    (showIgnored.value ? 1 : 0) +
    (source.value ? 1 : 0) +
    (locationId.value ? 1 : 0) +
    (routerId.value ? 1 : 0) +
    (hostId.value ? 1 : 0),
)
const sources = [
  { label: 'Any source', value: null },
  ...Object.entries(sourceLabels).map(([value, s]) => ({ label: s.label, value })),
]

// Assigning to a host that exists.
const assigning = ref<Row | null>(null)
const assignOpen = computed({ get: () => assigning.value !== null, set: (v) => !v && (assigning.value = null) })
const assignHost = ref<string | null>(null)
const assignInterface = ref('')
const hosts = useQuery({
  queryKey: [...keys.hosts, 'network-picker'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
  enabled: computed(() => assignOpen.value && session.can('Monitoring', 'View')),
})
function openAssign(row: Row, host?: string | null) {
  assigning.value = row
  assignHost.value = host ?? row.matchHostId ?? null
  assignInterface.value = row.matchInterface ?? ''
}
async function assign() {
  if (!assigning.value || !assignHost.value) return
  const ok = await run(
    () =>
      api.network.assign(assigning.value!.mac, {
        hostId: assignHost.value!,
        interface: assignInterface.value || undefined,
      }),
    {
      success: 'Assigned: the suggestions follow in a few seconds',
      failure: 'Not assigned',
      invalidate: [keys.network],
    },
  )
  if (ok) assigning.value = null
}
async function clear(row: Row) {
  await done(() => api.network.clearAssignment(row.mac), { success: 'Assignment cleared', invalidate: [keys.network] })
}
async function ignore(row: Row, on: boolean) {
  await done(() => api.network.ignore(row.mac, on), {
    success: on ? 'Ignored' : 'No longer ignored',
    invalidate: [keys.network],
  })
}
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="MAC, IP or host name" :active="active">
      <label class="check"
        ><Checkbox v-model="unassigned" binary input-id="unassigned" /><span>Unassigned only</span></label
      >
      <Select
        v-model="source"
        :options="sources"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Source"
      />
      <LocationPicker v-model="locationId" none-label="All locations" size="small" aria-label="The router's location" />
      <label class="check"
        ><Checkbox v-model="showIgnored" binary input-id="show-ignored" /><span>Show ignored</span></label
      >
      <Button
        v-if="routerId || hostId"
        label="All devices"
        icon="pi pi-times"
        size="small"
        text
        @click="((routerId = null), (hostId = null))"
      />
    </FilterBar>
    <ListTable
      :table="table"
      what="network interfaces"
      empty-text="No network interfaces: a scan reads the routers' tables."
    >
      <Column header="MAC" field="mac" sortable style="width: 12rem">
        <template #body="{ data }">
          <span class="mono">{{ data.mac }}</span>
          <Tag v-if="data.random" value="random" severity="secondary" class="tag" />
          <Tag v-if="data.ignored" value="ignored" severity="secondary" class="tag" />
        </template>
      </Column>
      <Column header="IP · host name" field="hostName" sortable>
        <template #body="{ data }">
          <span class="mono small">{{ data.ips.slice(0, 2).join(', ') }}</span>
          <div class="small">{{ data.hostName ?? '' }}</div>
        </template>
      </Column>
      <Column header="Attached at" field="port" sortable>
        <template #body="{ data }">
          <span class="small">{{ attachedLabel(data) }}</span>
          <div v-if="data.neighborIdentity" class="muted small">
            Neighbor: {{ data.neighborIdentity }}{{ data.neighborBoard ? `, ${data.neighborBoard}` : ''
            }}{{ data.neighborPort ? `, its port ${data.neighborPort}` : '' }}
          </div>
        </template>
      </Column>
      <Column header="Sources" style="width: 7rem">
        <template #body="{ data }">
          <i
            v-for="s in data.sources"
            :key="s"
            v-tooltip.top="sourceLabels[s]?.label ?? s"
            :class="sourceLabels[s]?.icon ?? 'pi pi-circle'"
            class="source"
          />
        </template>
      </Column>
      <Column header="Matched to">
        <template #body="{ data }">
          <span :class="{ muted: data.match === 'None' }">{{ matchLabel(data) }}</span>
          <div v-if="data.hintHostId && data.match === 'None'" class="small">
            Possibly <strong>{{ data.hintHostName }}</strong>
            <Button v-if="edit" label="Use" size="small" text @click="openAssign(data, data.hintHostId)" />
          </div>
        </template>
      </Column>
      <Column header="Last seen" field="lastSeen" sortable style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSeenAt" /></template>
      </Column>
      <Column v-if="edit" style="width: 9rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-link"
            text
            size="small"
            :aria-label="`Assign ${data.mac} to a host`"
            @click="openAssign(data)"
          />
          <Button
            v-if="data.match === 'Manual'"
            icon="pi pi-times"
            text
            size="small"
            severity="secondary"
            :aria-label="`Clear ${data.mac}'s assignment`"
            @click="clear(data)"
          />
          <Button
            :icon="data.ignored ? 'pi pi-eye' : 'pi pi-eye-slash'"
            text
            size="small"
            severity="secondary"
            :aria-label="data.ignored ? `Stop ignoring ${data.mac}` : `Ignore ${data.mac}`"
            @click="ignore(data, !data.ignored)"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="mono">
          {{ item.mac }} <span class="muted">{{ item.hostName ?? '' }}</span>
        </div>
        <div class="muted small">{{ attachedLabel(item) }} · {{ matchLabel(item) }}</div>
      </template>
    </ListTable>

    <Dialog
      v-model:visible="assignOpen"
      :header="`Assign ${assigning?.mac ?? ''}`"
      modal
      :style="{ width: '28rem' }"
      :breakpoints="{ '599px': '95vw' }"
    >
      <div class="form">
        <p class="muted small">
          To a host that exists (create it on Hosts first). The scans' suggestions for it follow.
        </p>
        <label for="assign-host">Host</label>
        <Select
          id="assign-host"
          v-model="assignHost"
          :options="hosts.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          filter
          placeholder="A host"
          :loading="hosts.isPending.value"
          aria-label="Host"
        />
        <label for="assign-interface">Its interface (optional)</label>
        <InputText id="assign-interface" v-model="assignInterface" placeholder="eth0, Port 3…" />
      </div>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="assigning = null" />
        <Button label="Assign" :disabled="!assignHost" @click="assign" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.929rem;
}

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.tag {
  margin-left: 0.25rem;
}

.source {
  margin-right: 0.35rem;
  color: var(--pnmt-muted);
}

.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}
</style>
