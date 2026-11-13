<script setup lang="ts">
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
import { modeLabel } from '@/views/sensors/mikrotik/mikrotikDraft'

/** Every router (§11.2): its state, version against the latest of its channel, and whether its script is up to date. */
const filter = ref<'' | 'update' | 'outdated' | 'failing'>('')
const filters = computed(() => ({
  updateAvailable: filter.value === 'update' ? true : undefined,
  scriptOutdated: filter.value === 'outdated' ? true : undefined,
  state: filter.value === 'failing' ? ('Failing' as S['SensorState']) : undefined,
}))
const table = useServerTable<S['MikrotikFleetItem'] & { id: string }>({
  key: [...keys.mikrotik, 'routers'],
  fetch: async (q) => {
    const page = await api.mikrotik.routers(q)
    return { ...page, items: page.items.map((i) => ({ ...i, id: i.sensorId })) }
  },
  defaultSort: 'name',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const choices = [
  { label: 'All routers', value: '' },
  { label: 'Behind on updates', value: 'update' },
  { label: 'Script outdated', value: 'outdated' },
  { label: 'Failing', value: 'failing' },
]
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
        aria-label="Filter"
      />
    </div>
    <ListTable :table="table" what="routers" empty-text="No MikroTik router yet: add one as a sensor of a host.">
      <Column header="Router">
        <template #body="{ data }">
          <StatusBadge :status="data.state" icon-only />
          <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }">{{ data.name }}</RouterLink>
          <div class="muted small">{{ data.host.name }} · {{ data.identity ?? '–' }} · {{ data.model ?? '' }}</div>
        </template>
      </Column>
      <Column header="Connection" style="width: 11rem">
        <template #body="{ data }">
          {{ modeLabel(data.mode) }}
          <div class="muted small mono">{{ data.address ?? '' }}</div>
        </template>
      </Column>
      <Column header="RouterOS" style="width: 12rem">
        <template #body="{ data }">
          <span class="mono">{{ data.version ?? '–' }}</span> <span class="muted small">{{ data.channel ?? '' }}</span>
          <div v-if="data.updateAvailable" class="small update">{{ data.latestVersion }} available</div>
        </template>
      </Column>
      <Column header="State">
        <template #body="{ data }">
          <CodeTag :code="data.lastCode" compact />
          <span class="small ellipsis">{{ data.lastMessage }}</span>
          <div class="small">
            <Tag v-if="data.setupState !== 'Ready'" :value="data.setupState" severity="secondary" />
            <Tag v-if="data.scriptOutdated" value="script outdated" severity="warn" />
          </div>
        </template>
      </Column>
      <Column header="Last poll" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSuccessAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <StatusBadge :status="item.state" icon-only />
          <RouterLink :to="{ name: 'sensor', params: { id: item.sensorId } }">{{ item.name }}</RouterLink>
          <span class="mono muted">{{ item.version }}</span>
        </div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.update {
  color: var(--pnmt-warn);
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
