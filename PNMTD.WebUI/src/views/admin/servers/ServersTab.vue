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
import { connection, platformIcon, platforms, uptime } from './agentFormat'

/**
 * The fleet of both platforms (docs/windows-sensor-plan.md §10.1; docs/linux-sensor-plan.md §11.1): the platform, state,
 * host, OS (for Linux the distribution and the kernel), agent, connection, remote access, audit gaps and diagnostics, Linux's
 * pending security updates and whether its config.yaml allows key removal, with filters. Add server makes a join token for an
 * existing host.
 */
const platform = ref<S['AgentPlatform'] | null>(null)
const filter = ref<
  | ''
  | 'failing'
  | 'offline'
  | 'unsupported'
  | 'outdated'
  | 'remoteAccess'
  | 'auditGaps'
  | 'diagnosticsFailing'
  | 'securityUpdates'
  | 'keyRemoval'
>('')
const filters = computed(() => ({
  platform: platform.value ?? undefined,
  state: filter.value === 'failing' ? ('Failing' as S['SensorState']) : undefined,
  connected: filter.value === 'offline' ? false : undefined,
  unsupported: filter.value === 'unsupported' ? true : undefined,
  outdated: filter.value === 'outdated' ? true : undefined,
  remoteAccess: filter.value === 'remoteAccess' ? true : undefined,
  auditGaps: filter.value === 'auditGaps' ? true : undefined,
  diagnosticsFailing: filter.value === 'diagnosticsFailing' ? true : undefined,
  securityUpdates: filter.value === 'securityUpdates' ? true : undefined,
  keyRemoval: filter.value === 'keyRemoval' ? true : undefined,
}))
type Server = S['AgentServerItem'] & { id: string }
const table = useServerTable<Server>({
  key: [...keys.agents, 'servers'],
  fetch: async (q) => {
    const page = await api.agents.servers(q)
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
  { label: 'Unsupported OS version', value: 'unsupported' },
  { label: 'Outdated agent', value: 'outdated' },
  { label: 'Remote access open', value: 'remoteAccess' },
  { label: 'Audit gaps', value: 'auditGaps' },
  { label: 'Diagnostics failing', value: 'diagnosticsFailing' },
  { label: 'Security updates pending (Linux)', value: 'securityUpdates' },
  { label: 'Key removal allowed (Linux)', value: 'keyRemoval' },
]
const adding = ref(false)
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select
        v-model="platform"
        :options="platforms"
        show-clear
        size="small"
        placeholder="Every platform"
        aria-label="Platform"
      />
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
    <ListTable :table="table" what="servers" empty-text="No server yet: Add server makes a join token.">
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
      <Column header="Platform" style="width: 7rem">
        <template #body="{ data }"
          ><span class="line"
            ><i :class="platformIcon(data.platform)" aria-hidden="true" />{{ data.platform }}</span
          ></template
        >
      </Column>
      <Column header="System" field="os" sortable style="width: 16rem">
        <template #body="{ data }">
          <template v-if="data.platform === 'Linux'">
            {{ data.distribution ?? data.osCaption ?? '–' }}
            <div class="muted small mono">{{ data.kernelRelease ?? data.osBuild }}</div>
          </template>
          <template v-else>
            {{ data.osCaption ?? '–' }} <span class="muted small mono">{{ data.osBuild }}</span>
          </template>
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
            <Tag
              v-if="data.securityUpdates"
              :value="`${data.securityUpdates} security updates`"
              severity="danger"
              icon="pi pi-shield"
            />
            <Tag v-if="data.keyRemovalAllowed" value="key removal allowed" severity="info" icon="pi pi-key" />
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
        <div class="small muted">
          {{ item.platform }} · {{ item.distribution ?? item.osCaption }} · {{ item.agentVersion }}
          <template v-if="item.securityUpdates"> · {{ item.securityUpdates }} security updates</template>
        </div>
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
