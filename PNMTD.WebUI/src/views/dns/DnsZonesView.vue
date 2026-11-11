<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatDuration } from '@/format'
import { useSession } from '@/stores/session'
import { logLabel, zoneStatus } from './dnsState'

/** DNS zones (§5.6): state, records with mismatches, last and next check; the history below. */
const session = useSession()
const router = useRouter()
const table = useServerTable<S['DnsZoneItem']>({
  key: keys.dns,
  fetch: (q) => api.dns.list(q),
  defaultSort: 'name',
  syncUrl: true,
})
const showLog = ref(false)
const log = useServerTable<S['DnsLogEntry']>({ key: [...keys.dns, 'log'], fetch: (q) => api.dns.log(q), pageSize: 25 })
</script>

<template>
  <div class="page">
    <PageHeader title="DNS zones">
      <template #actions>
        <Button
          v-if="session.can('User')"
          label="New zone"
          icon="pi pi-plus"
          size="small"
          @click="router.push({ name: 'dns-zone-new' })"
        />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search zone name" />
    <ListTable :table="table" what="DNS zones" :row-class="(z) => (z.enabled ? undefined : 'row-muted')">
      <Column header="" style="width: 2.5rem">
        <template #body="{ data }"><StatusBadge v-bind="zoneStatus(data)" icon-only /></template>
      </Column>
      <Column field="name" header="Zone" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'dns-zone', params: { id: data.id } }" class="strong">{{
            data.zoneName
          }}</RouterLink>
        </template>
      </Column>
      <Column header="Host">
        <template #body="{ data }">
          <RouterLink v-if="data.host" :to="{ name: 'host', params: { id: data.host.id } }">{{
            data.host.name
          }}</RouterLink>
          <span v-else class="muted">–</span>
        </template>
      </Column>
      <Column header="Records">
        <template #body="{ data }">
          {{ data.recordCount }}
          <Tag v-if="data.mismatchCount" :value="`${data.mismatchCount} mismatch`" severity="danger" class="tag" />
          <span v-if="data.ignoredCount" class="muted"> · {{ data.ignoredCount }} ignored</span>
        </template>
      </Column>
      <Column header="Every" style="width: 6rem">
        <template #body="{ data }">{{ formatDuration(data.interval * 1000) }}</template>
      </Column>
      <Column field="lastchecked" header="Last check" sortable style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.lastChecked" /></template>
      </Column>
      <Column header="Next check" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.nextCheck" /></template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'dns-zone', params: { id: item.id } }" class="mobile-row">
          <StatusBadge v-bind="zoneStatus(item)" icon-only />
          <span class="strong">{{ item.zoneName }}</span>
          <span class="muted right">{{ item.recordCount }} records</span>
        </RouterLink>
      </template>
    </ListTable>

    <section class="panel">
      <button type="button" class="panel-header toggle" :aria-expanded="showLog" @click="showLog = !showLog">
        <i :class="showLog ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
        History of all zones
      </button>
      <ListTable v-if="showLog" :table="log" what="history entries" class="log">
        <Column header="Time" style="width: 9rem">
          <template #body="{ data }"><RelativeTime :value="data.created" /></template>
        </Column>
        <Column header="Zone" style="width: 12rem">
          <template #body="{ data }">
            <RouterLink v-if="data.dnsZoneId" :to="{ name: 'dns-zone', params: { id: data.dnsZoneId } }">{{
              data.zoneName
            }}</RouterLink>
          </template>
        </Column>
        <Column header="What" style="width: 10rem">
          <template #body="{ data }">{{ logLabel[data.entryType] ?? data.entryType }}</template>
        </Column>
        <Column field="message" header="Detail" />
        <template #mobile="{ item }">
          <div class="muted">{{ item.zoneName }} · <RelativeTime :value="item.created" /></div>
          <div>{{ item.message }}</div>
        </template>
      </ListTable>
    </section>
  </div>
</template>

<style scoped>
.strong {
  font-weight: 500;
}

.tag {
  margin-left: var(--pnmt-space-2);
}

.toggle {
  all: unset;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  box-sizing: border-box;
  min-height: 40px;
  padding: 0 var(--pnmt-space-3);
  font-weight: 600;
  cursor: pointer;
}

.log {
  border: none;
}

.mobile-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  color: var(--pnmt-text);
}

.right {
  margin-left: auto;
}
</style>
