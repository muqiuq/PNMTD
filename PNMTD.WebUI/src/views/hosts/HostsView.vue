<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type HostState, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatPercent } from '@/format'
import { useSession } from '@/stores/session'

/** The hosts (§5.6): status, sensors by state, 30 days, the last event; quick filters, search. */
const session = useSession()
const route = useRoute()
const router = useRouter()

const states = [
  { label: 'All', value: '' },
  { label: 'Failing', value: 'Failing' },
  { label: 'No data', value: 'NoData' },
  { label: 'Disabled', value: 'Disabled' },
]
const state = ref<string>(typeof route.query.state === 'string' ? route.query.state : '')
watch(state, (value) => router.replace({ query: { ...route.query, state: value || undefined, page: undefined } }))
const filters = computed(() => ({ state: (state.value || undefined) as HostState | undefined }))

const table = useServerTable<S['HostListItem']>({
  key: keys.hosts,
  fetch: (q) => api.hosts.list(q),
  defaultSort: 'name',
  filters,
  syncUrl: true,
})
</script>

<template>
  <div class="page">
    <PageHeader title="Hosts">
      <template #status>
        <span class="muted">{{ table.total.value }}</span>
      </template>
      <template #actions>
        <Button
          v-if="session.can('User')"
          label="New host"
          icon="pi pi-plus"
          size="small"
          @click="router.push({ name: 'host-new' })"
        />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search name or location" :active="state ? 1 : 0">
      <SelectButton
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="State"
      />
    </FilterBar>
    <ListTable :table="table" what="hosts" :row-class="(h) => (h.enabled ? undefined : 'row-muted')">
      <Column field="state" header="" sortable style="width: 2.5rem">
        <template #body="{ data }"><StatusBadge :status="data.state" icon-only /></template>
      </Column>
      <Column field="name" header="Name" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'host', params: { id: data.id } }" class="strong">{{ data.name }}</RouterLink>
        </template>
      </Column>
      <Column header="Location">
        <template #body="{ data }"
          ><span class="muted">{{ data.location }}</span></template
        >
      </Column>
      <Column header="Sensors" style="width: 9rem">
        <template #body="{ data }">
          <span
            class="counts"
            :title="`${data.sensors.ok} ok · ${data.sensors.failing} failing · ${data.sensors.total} in all`"
          >
            <span class="ok">{{ data.sensors.ok }}</span> /
            <span :class="{ fail: data.sensors.failing }">{{ data.sensors.failing }}</span> /
            <span>{{ data.sensors.total }}</span>
          </span>
        </template>
      </Column>
      <Column field="uptime" header="30 days" sortable style="width: 16rem">
        <template #body="{ data }">
          <div class="uptime">
            <span class="percent">{{ formatPercent(data.uptime30Days) }}</span>
            <UptimeBar
              :values="data.days.values ?? []"
              :start="data.days.start"
              :height="12"
              :minimum="30"
              class="bar"
            />
          </div>
        </template>
      </Column>
      <Column field="lastevent" header="Last event" sortable style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastEventAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'host', params: { id: item.id } }" class="mobile-row">
          <StatusBadge :status="item.state" icon-only />
          <span class="strong">{{ item.name }}</span>
          <span class="muted ellipsis">{{ item.location }}</span>
          <span class="right">{{ formatPercent(item.uptime30Days) }}</span>
        </RouterLink>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.strong {
  font-weight: 500;
}

.counts {
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.ok {
  color: var(--pnmt-ok);
}

.fail {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.uptime {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.percent {
  width: 3.6rem;
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.bar {
  flex: 1;
}

.mobile-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  color: var(--pnmt-text);
}

.mobile-row .right {
  margin-left: auto;
  white-space: nowrap;
}
</style>
