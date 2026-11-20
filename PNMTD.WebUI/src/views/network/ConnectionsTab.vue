<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
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
import ConnectionDialog from './ConnectionDialog.vue'
import { endLabel, scanLabel } from './networkFormat'

/**
 * The connections (docs/network-map-plan.md §8.4): paginated, filtered by location, device, physical or virtual, and those
 * the scans no longer show. New connection, Edit and Delete with Network map: Edit.
 */
const route = useRoute()
const session = useSession()
const { ask, done } = useActions()
const locationId = ref<string | null>(typeof route.query.location === 'string' ? route.query.location : null)
const device = ref<string | null>(typeof route.query.device === 'string' ? route.query.device : null)
const kind = ref<'all' | 'physical' | 'virtual'>('all')
const routersOnly = ref(false)
const unconfirmed = ref(route.query.unconfirmed === '1')
const filters = computed(() => ({
  locationId: locationId.value ?? undefined,
  deviceId: device.value ?? undefined,
  physical: kind.value === 'all' ? undefined : kind.value === 'physical',
  routersOnly: routersOnly.value || undefined,
  unconfirmed: unconfirmed.value || undefined,
}))
const table = useServerTable<S['NetworkConnectionItem']>({
  key: [...keys.network, 'connections'],
  fetch: (q) => api.network.connections(q),
  defaultSort: 'a',
  pageSize: 50,
  filters,
  syncUrl: true,
})
const adding = ref(false)
const editing = ref<S['NetworkConnectionItem'] | null>(null)
const editOpen = computed({ get: () => editing.value !== null, set: (v) => !v && (editing.value = null) })
const kindChoices = [
  { label: 'All connections', value: 'all' },
  { label: 'Physical', value: 'physical' },
  { label: 'Virtual', value: 'virtual' },
]
const active = computed(
  () =>
    (locationId.value ? 1 : 0) +
    (device.value ? 1 : 0) +
    (kind.value !== 'all' ? 1 : 0) +
    (routersOnly.value ? 1 : 0) +
    (unconfirmed.value ? 1 : 0),
)

async function remove(c: S['NetworkConnectionItem']) {
  if (
    !(await ask(
      'Delete the connection?',
      `${endLabel(c.a)} ↔ ${endLabel(c.b)} goes from the map. The scans may suggest it again.`,
    ))
  )
    return
  await done(() => api.network.deleteConnection(c.id), { success: 'Connection deleted', invalidate: [keys.network] })
}
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Search names, ports, notes" :active="active">
      <LocationPicker v-model="locationId" none-label="All locations" size="small" aria-label="Location" />
      <Select
        v-model="kind"
        :options="kindChoices"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Physical or virtual"
      />
      <label class="check"
        ><Checkbox v-model="routersOnly" binary input-id="routers-only" /><span>Router ↔ router</span></label
      >
      <label class="check"
        ><Checkbox v-model="unconfirmed" binary input-id="unconfirmed" /><span>No longer seen</span></label
      >
      <Button v-if="device" label="All devices" icon="pi pi-times" size="small" text @click="device = null" />
      <template #end>
        <Button
          v-if="session.can('NetworkMap', 'Edit')"
          label="New connection"
          icon="pi pi-plus"
          size="small"
          @click="adding = true"
        />
      </template>
    </FilterBar>
    <ListTable :table="table" what="connections" empty-text="No connections: accept suggestions, or add one by hand.">
      <Column header="From" field="a" sortable>
        <template #body="{ data }">
          <RouterLink
            :to="{
              name: 'network-map',
              query: { focus: data.a.kind === 'Router' ? `router:${data.a.sensorId}` : `host:${data.a.hostId}` },
            }"
            >{{ data.a.name }}</RouterLink
          >
          <span class="mono muted"> {{ data.a.interface ?? '' }}</span>
          <div class="muted small">{{ data.a.site ?? '' }}</div>
        </template>
      </Column>
      <Column header="To" field="b" sortable>
        <template #body="{ data }">
          <RouterLink
            :to="{
              name: 'network-map',
              query: { focus: data.b.kind === 'Router' ? `router:${data.b.sensorId}` : `host:${data.b.hostId}` },
            }"
            >{{ data.b.name }}</RouterLink
          >
          <span class="mono muted"> {{ data.b.interface ?? '' }}</span>
          <div class="muted small">{{ data.b.site ?? '' }}</div>
        </template>
      </Column>
      <Column header="Type" field="type" sortable style="width: 11rem">
        <template #body="{ data }">
          <Tag :value="data.typeLabel" :severity="data.physical ? 'secondary' : 'contrast'" />
        </template>
      </Column>
      <Column header="Scan" field="scan" sortable style="width: 12rem">
        <template #body="{ data }">
          <span :class="{ warn: data.scan === 'Unconfirmed', muted: data.scan === 'Manual' }">{{
            scanLabel(data.scan)
          }}</span>
          <div class="small muted">
            <RelativeTime v-if="data.unconfirmedSince" :value="data.unconfirmedSince" />
            <RelativeTime v-else-if="data.confirmedAt" :value="data.confirmedAt" />
          </div>
        </template>
      </Column>
      <Column header="Notes">
        <template #body="{ data }"
          ><span class="small ellipsis">{{ data.notes ?? '' }}</span></template
        >
      </Column>
      <Column v-if="session.can('NetworkMap', 'Edit')" style="width: 6rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-pencil"
            text
            size="small"
            :aria-label="`Edit ${endLabel(data.a)} ↔ ${endLabel(data.b)}`"
            @click="editing = data"
          />
          <Button
            icon="pi pi-trash"
            text
            size="small"
            severity="danger"
            :aria-label="`Delete ${endLabel(data.a)} ↔ ${endLabel(data.b)}`"
            @click="remove(data)"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div>{{ endLabel(item.a) }} ↔ {{ endLabel(item.b) }}</div>
        <div class="muted small">{{ item.typeLabel }} · {{ scanLabel(item.scan) }}</div>
      </template>
    </ListTable>
    <ConnectionDialog v-model:visible="adding" />
    <ConnectionDialog v-if="editing" v-model:visible="editOpen" :connection="editing" />
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

.warn {
  color: var(--pnmt-warn);
}
</style>
