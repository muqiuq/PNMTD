<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useServerTable } from '@/composables/useServerTable'
import AddServerDialog from './AddServerDialog.vue'
import { connection, uptime } from './windowsFormat'

/**
 * The fleet (docs/windows-sensor-plan.md §10.1): state, host, OS, agent, connection, remote access, audit gaps and
 * diagnostics, with filters. Add server makes a join token for an existing host.
 */
const filter = ref<
  '' | 'failing' | 'offline' | 'unsupported' | 'outdated' | 'remoteAccess' | 'auditGaps' | 'diagnosticsFailing'
>('')
const filters = computed(() => ({
  state: filter.value === 'failing' ? ('Failing' as S['SensorState']) : undefined,
  connected: filter.value === 'offline' ? false : undefined,
  unsupported: filter.value === 'unsupported' ? true : undefined,
  outdated: filter.value === 'outdated' ? true : undefined,
  remoteAccess: filter.value === 'remoteAccess' ? true : undefined,
  auditGaps: filter.value === 'auditGaps' ? true : undefined,
  diagnosticsFailing: filter.value === 'diagnosticsFailing' ? true : undefined,
}))
type Server = S['WindowsServerItem'] & { id: string }
const table = useServerTable<Server>({
  key: [...keys.windows, 'servers'],
  fetch: async (q) => {
    const page = await api.windows.servers(q)
    return { ...page, items: page.items.map((i) => ({ ...i, id: i.sensorId })) }
  },
  defaultSort: 'host',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const choices = [
  { label: 'All servers', value: '' },
  { label: 'Failing', value: 'failing' },
  { label: 'Offline', value: 'offline' },
  { label: 'Unsupported Windows', value: 'unsupported' },
  { label: 'Outdated agent', value: 'outdated' },
  { label: 'Remote access open', value: 'remoteAccess' },
  { label: 'Audit gaps', value: 'auditGaps' },
  { label: 'Diagnostics failing', value: 'diagnosticsFailing' },
]
const adding = ref(false)
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select
        v-model="filter"
        :options="choices"
        option-label="label"
        option-value="value"
        size="small"
        placeholder="All servers"
        aria-label="Filter"
      />
      <span class="spacer" />
      <Button label="Add server" icon="pi pi-plus" size="small" @click="adding = true" />
    </div>
    <ListTable :table="table" what="servers" empty-text="No Windows server yet: Add server makes a join token.">
      <Column header="Server" field="host" sortable>
        <template #body="{ data }">
          <span class="line">
            <StatusBadge :status="data.state" icon-only />
            <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }"
              >{{ data.host.name }} / {{ data.name }}</RouterLink
            >
          </span>
          <div class="muted small">
            {{ data.hostname }}<template v-if="data.domain"> · {{ data.domain }}</template>
          </div>
        </template>
      </Column>
      <Column header="Windows" field="os" sortable style="width: 16rem">
        <template #body="{ data }">
          {{ data.osCaption ?? '–' }} <span class="muted small mono">{{ data.osBuild }}</span>
          <div>
            <Tag v-if="!data.osSupported" value="not a supported version" severity="warn" class="small" />
          </div>
        </template>
      </Column>
      <Column header="Agent" style="width: 11rem">
        <template #body="{ data }">
          <Tag :value="connection(data).label" :severity="connection(data).severity" />
          <span class="mono small"> {{ data.agentVersion }}</span>
          <div><Tag v-if="data.outdated" value="outdated" severity="warn" class="small" /></div>
        </template>
      </Column>
      <Column header="State">
        <template #body="{ data }">
          <CodeTag :code="data.lastCode" compact />
          <span class="small ellipsis">{{ data.lastMessage }}</span>
          <div class="small">
            <Tag v-if="data.remoteAccess" :value="data.remoteAccess" severity="secondary" icon="pi pi-desktop" />
            <Tag v-if="data.auditGaps" :value="`${data.auditGaps} audit gaps`" severity="warn" />
            <Tag
              v-if="data.diagnosticsFailing"
              :value="`${data.diagnosticsFailing} diagnostics failing`"
              severity="danger"
            />
          </div>
        </template>
      </Column>
      <Column header="Uptime" style="width: 7rem">
        <template #body="{ data }">{{ uptime(data.bootTime) ?? '–' }}</template>
      </Column>
      <Column header="Last seen" field="lastseen" sortable style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSeenAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <StatusBadge :status="item.state" icon-only />
          <RouterLink :to="{ name: 'sensor', params: { id: item.sensorId } }"
            >{{ item.host.name }} / {{ item.name }}</RouterLink
          >
          <Tag :value="connection(item).label" :severity="connection(item).severity" />
        </div>
        <div class="small muted">{{ item.osCaption }} · {{ item.agentVersion }}</div>
      </template>
    </ListTable>
    <AddServerDialog v-model:visible="adding" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar,
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
