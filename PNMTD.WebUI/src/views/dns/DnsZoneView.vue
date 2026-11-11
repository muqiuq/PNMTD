<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatDuration } from '@/format'
import { useSession } from '@/stores/session'
import { logLabel, zoneStatus } from './dnsState'

/**
 * A DNS zone (§5.6): reference and actual values side by side, mismatches highlighted and shown alone
 * when there are any. "Check now" asks for a check and follows it until the zone is processed.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run, ask, invalidate, notify, done } = useActions()

const id = computed(() => String(route.params.id))
const key = computed(() => [...keys.dns, id.value])
const polling = ref(false)
const query = useQuery({
  queryKey: key,
  queryFn: () => api.dns.get(id.value),
  refetchInterval: computed(() => (polling.value ? 2_000 : 30_000)),
})
const zone = computed(() => query.data.value)
const canWrite = computed(() => session.can('User'))
const state = computed(() => (zone.value ? zoneStatus(zone.value) : null))

const mismatches = computed(() => zone.value?.records.filter((r) => !r.isMatch && !r.ignore) ?? [])
const onlyMismatches = ref(false)
watch(
  () => mismatches.value.length,
  (n, old) => {
    if (old === undefined) onlyMismatches.value = n > 0
  },
  { immediate: true },
)
const records = computed(() => (onlyMismatches.value ? mismatches.value : (zone.value?.records ?? [])))

// "Check now": followed until the server has processed the zone, at most two minutes.
let pollTimer: ReturnType<typeof setTimeout> | undefined
async function checkNow() {
  if (
    (await run(() => api.dns.forceUpdate(id.value), { failure: 'Not requested', invalidate: [keys.dns] })) === undefined
  )
    return
  polling.value = true
  pollTimer = setTimeout(() => (polling.value = false), 120_000)
}
watch(
  () => zone.value && polling.value && !zone.value.forceUpdate && !zone.value.requiresProcessing,
  (done) => {
    if (done) {
      polling.value = false
      clearTimeout(pollTimer)
      notify.success(`${zone.value!.zoneName} checked`)
      void invalidate([keys.dns])
    }
  },
)
onBeforeUnmount(() => clearTimeout(pollTimer))

async function saveZone(patch: Partial<S['DnsZoneRequest']>) {
  const z = zone.value!
  const updated = await api.dns.update(id.value, {
    zoneFileContent: z.zoneFileContent,
    hostId: z.host?.id ?? null,
    interval: z.interval,
    enabled: z.enabled,
    ...patch,
  })
  queryClient.setQueryData(key.value, updated)
  await invalidate([keys.dns])
}

async function setIgnore(record: S['DnsRecord'], ignore: boolean) {
  await api.dns.record(id.value, record.id, { ignore })
  await queryClient.invalidateQueries({ queryKey: key.value })
  await invalidate([keys.dns])
}

const log = useServerTable<S['DnsLogEntry']>({
  key: [...keys.dns, 'zone-log'],
  fetch: ({ zone: z, ...q }) => api.dns.zoneLog(String(z), q),
  filters: computed(() => ({ zone: id.value })),
  pageSize: 10,
})

async function remove() {
  const z = zone.value!
  const ok = await ask(
    `Delete ${z.zoneName}?`,
    'Its records, its history and its DNS sensor are deleted with it. This cannot be undone.',
  )
  if (ok && (await done(() => api.dns.remove(z.id), { success: `${z.zoneName} deleted`, invalidate: [keys.dns] }))) {
    await router.replace({ name: 'dns-zones' })
  }
}
const more = computed(() => [{ label: 'Delete zone…', icon: 'pi pi-trash', command: remove }])
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="query.isError.value && !zone"
      :error="query.error.value"
      what="the zone"
      @retry="query.refetch()"
    />
    <div v-else-if="!zone" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader :title="zone.zoneName" :crumbs="[{ label: 'DNS zones', to: { name: 'dns-zones' } }]">
        <template #status>
          <StatusBadge v-if="state" :status="state.status" :label="state.label" />
          <span class="muted">checked <RelativeTime :value="zone.lastChecked" /></span>
        </template>
        <template #actions>
          <Button
            v-if="canWrite"
            label="Check now"
            icon="pi pi-sync"
            size="small"
            :loading="polling"
            @click="checkNow"
          />
          <Button
            v-if="canWrite"
            label="Edit zone file"
            icon="pi pi-pencil"
            size="small"
            severity="secondary"
            outlined
            @click="router.push({ name: 'dns-zone-edit', params: { id: zone.id } })"
          />
          <MoreMenu v-if="canWrite" :items="more" label="More actions for the zone" />
        </template>
      </PageHeader>

      <section class="panel facts-strip">
        <dl class="facts row">
          <dt>Host</dt>
          <dd>
            <RouterLink v-if="zone.host" :to="{ name: 'host', params: { id: zone.host.id } }">{{
              zone.host.name
            }}</RouterLink>
            <span v-else class="muted">–</span>
          </dd>
          <dt>Every (s)</dt>
          <dd>
            <InlineField
              :value="zone.interval"
              type="number"
              label="Interval in seconds"
              field="interval"
              :readonly="!canWrite"
              :save="(v) => saveZone({ interval: Number(v) })"
            />
            <span class="muted"> {{ formatDuration(zone.interval * 1000) }}</span>
          </dd>
          <dt>Next check</dt>
          <dd><RelativeTime :value="zone.nextCheck" /></dd>
          <dt>Enabled</dt>
          <dd>
            <InlineField
              :value="zone.enabled"
              type="bool"
              label="Enabled"
              :readonly="!canWrite"
              :save="(v) => saveZone({ enabled: Boolean(v) })"
            />
          </dd>
        </dl>
      </section>

      <section class="panel" aria-labelledby="records-title">
        <div id="records-title" class="panel-header">
          Records <span class="muted">{{ zone.records.length }}</span>
          <label v-if="mismatches.length" class="only">
            <ToggleSwitch v-model="onlyMismatches" /> Mismatches only ({{ mismatches.length }})
          </label>
        </div>
        <DataTable
          :value="records"
          size="small"
          data-key="id"
          :row-class="(r: S['DnsRecord']) => (r.isMatch || r.ignore ? undefined : 'mismatch')"
        >
          <Column field="name" header="Name">
            <template #body="{ data }"
              ><span class="mono">{{ data.name }}</span></template
            >
          </Column>
          <Column field="recordType" header="Type" style="width: 5rem" />
          <Column field="ttl" header="TTL" style="width: 5rem" />
          <Column header="Reference (zone file)">
            <template #body="{ data }"
              ><span class="mono value">{{ data.referenceValue }}</span></template
            >
          </Column>
          <Column header="Actual (resolvers)">
            <template #body="{ data }">
              <span class="mono value" :class="{ differs: !data.isMatch }">{{ data.actualValue ?? '–' }}</span>
            </template>
          </Column>
          <Column header="Failures" style="width: 6rem">
            <template #body="{ data }"
              ><span :class="{ fail: data.failuresInARow }">{{ data.failuresInARow }}</span></template
            >
          </Column>
          <Column header="Ignore" style="width: 5rem">
            <template #body="{ data }">
              <InlineField
                :value="data.ignore"
                type="bool"
                :label="`Ignore ${data.name} ${data.recordType}`"
                :readonly="!canWrite"
                :save="(v) => setIgnore(data, Boolean(v))"
              />
            </template>
          </Column>
          <template #empty><div class="muted pad">No records.</div></template>
        </DataTable>
      </section>

      <section aria-labelledby="zone-log-title" class="log">
        <h2 id="zone-log-title">History</h2>
        <ListTable :table="log" what="history entries">
          <Column header="Time" style="width: 9rem">
            <template #body="{ data }"><RelativeTime :value="data.created" /></template>
          </Column>
          <Column header="What" style="width: 10rem">
            <template #body="{ data }">{{ logLabel[data.entryType] ?? data.entryType }}</template>
          </Column>
          <Column field="message" header="Detail" />
          <template #mobile="{ item }">
            <div class="muted"><RelativeTime :value="item.created" /> · {{ logLabel[item.entryType] }}</div>
            <div>{{ item.message }}</div>
          </template>
        </ListTable>
      </section>
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.facts-strip {
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
}

.facts.row {
  grid-template-columns: repeat(4, max-content minmax(0, 1fr));
}

.facts.row dd {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.only {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-weight: 400;
}

.value {
  overflow-wrap: anywhere;
}

.differs {
  color: var(--pnmt-fail);
  font-weight: 600;
}

:deep(tr.mismatch) {
  background: var(--pnmt-fail-soft) !important;
}

.fail {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.pad {
  padding: var(--pnmt-space-3);
}

.log {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

@media (max-width: 959px) {
  .facts.row {
    grid-template-columns: max-content minmax(0, 1fr);
  }
}
</style>
