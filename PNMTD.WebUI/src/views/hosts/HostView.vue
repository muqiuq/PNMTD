<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, hostRequest, keys, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import SinceText from '@/components/SinceText.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { useActions } from '@/composables/useActions'
import { typeIcon, typeLabel, useStatusCodes } from '@/composables/useLookups'
import { useMedia } from '@/composables/useMedia'
import { formatAbsolute, formatPercent } from '@/format'
import { useSession } from '@/stores/session'
import { importanceOptions } from '@/views/notifications/importance'
import AddServerDialog from '@/views/admin/servers/AddServerDialog.vue'
import HostLocationField from '@/views/locations/HostLocationField.vue'
import KbPanel from '@/views/kb/KbPanel.vue'

/**
 * A host (§5.6): its state and 90 days, its properties edited in place, and every sensor with its
 * state (ignored and never reported included), code, message and since when it fails.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const codes = useStatusCodes()
const { ask, invalidate, done } = useActions()
const phone = useMedia('(max-width: 599px)')
const queryClient = useQueryClient()

const id = computed(() => String(route.params.id))
const query = useQuery({
  queryKey: computed(() => keys.host(id.value)),
  queryFn: () => api.hosts.get(id.value),
  refetchInterval: 30_000,
})
const host = computed(() => query.data.value)
const canWrite = computed(() => session.can('Monitoring', 'Edit'))

const rank: Record<string, number> = { Failing: 0, NoData: 1, Ok: 2, Ignored: 3, Disabled: 4 }
const sensors = computed(() =>
  [...(host.value?.sensors ?? [])].sort(
    (a, b) => (rank[a.state] ?? 9) - (rank[b.state] ?? 9) || a.name.localeCompare(b.name),
  ),
)

async function save(patch: Partial<S['HostRequest']>) {
  const updated = await api.hosts.update(id.value, hostRequest(host.value!, patch))
  queryClient.setQueryData(keys.host(id.value), updated)
  // A location counts its hosts (docs/locations-knowledge-plan.md §3.1).
  await invalidate(patch.locationId !== undefined ? [keys.hosts, keys.locations] : [keys.hosts])
}

async function remove() {
  const h = host.value!
  const ok = await ask(
    `Delete ${h.name}?`,
    `Its ${h.sensorCounts.total} sensor${h.sensorCounts.total === 1 ? '' : 's'} and ${h.eventCount.toLocaleString()} event${h.eventCount === 1 ? '' : 's'} are deleted with it. This cannot be undone.`,
  )
  if (ok && (await done(() => api.hosts.remove(h.id), { success: `${h.name} deleted`, invalidate: [keys.hosts] }))) {
    await router.replace({ name: 'hosts' })
  }
}

// A server, Windows or Linux, joins with a token (docs/windows-sensor-plan.md §3.3; docs/linux-sensor-plan.md §11.1): Admin,
// with this host preselected.
const addingServer = ref(false)
const more = computed(() => [
  ...(session.can('Servers', 'Manage')
    ? [{ label: 'Add server…', icon: 'pi pi-desktop', command: () => (addingServer.value = true) }]
    : []),
  { label: 'Delete host…', icon: 'pi pi-trash', class: 'danger', command: remove },
])
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="query.isError.value && !host"
      :error="query.error.value"
      what="the host"
      @retry="query.refetch()"
    />
    <div v-else-if="!host" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader :title="host.name" :crumbs="[{ label: 'Hosts', to: { name: 'hosts' } }]">
        <template #status>
          <StatusBadge :status="host.state" />
          <RouterLink
            v-if="host.assignedLocation"
            :to="{ name: 'location', params: { id: host.assignedLocation.id } }"
            >{{ host.assignedLocation.path }}</RouterLink
          >
          <span v-if="host.location" class="muted">{{ host.location }}</span>
        </template>
        <template #actions>
          <Button
            v-if="canWrite"
            label="Add sensor"
            icon="pi pi-plus"
            size="small"
            @click="router.push({ name: 'sensor-new', params: { id: host.id } })"
          />
          <MoreMenu v-if="canWrite" :items="more" label="More actions for the host" />
        </template>
      </PageHeader>

      <section class="panel strip" aria-label="Uptime">
        <dl class="numbers">
          <div>
            <dt>24 h</dt>
            <dd>{{ formatPercent(host.uptime24Hours, 2) }}</dd>
          </div>
          <div>
            <dt>30 days</dt>
            <dd>{{ formatPercent(host.uptime30Days, 2) }}</dd>
          </div>
          <div>
            <dt>Last event</dt>
            <dd><RelativeTime :value="host.lastEventAt" /></dd>
          </div>
        </dl>
        <div class="bar90">
          <UptimeBar :values="host.days.values ?? []" :start="host.days.start" :height="22" />
          <div class="bar-legend muted"><span>90 days ago</span><span>today</span></div>
        </div>
      </section>

      <div class="two-columns">
        <section class="panel" aria-labelledby="props-title">
          <div id="props-title" class="panel-header">Properties</div>
          <dl class="panel-body facts">
            <dt>Name</dt>
            <dd>
              <InlineField
                :value="host.name"
                label="Name"
                field="name"
                :readonly="!canWrite"
                :save="(v) => save({ name: String(v) })"
              />
            </dd>
            <dt>Location</dt>
            <dd>
              <HostLocationField :host="host" :readonly="!canWrite" :save="save" />
            </dd>
            <dt>Importance</dt>
            <dd>
              <InlineField
                :value="host.importance ?? 'Normal'"
                type="enum"
                :options="importanceOptions"
                label="Importance"
                :readonly="!canWrite"
                :save="(v) => save({ importance: v as S['Importance'] })"
              />
            </dd>
            <dt>Enabled</dt>
            <dd>
              <InlineField
                :value="host.enabled"
                type="bool"
                label="Enabled"
                :readonly="!canWrite"
                :save="(v) => save({ enabled: Boolean(v) })"
              />
            </dd>
            <dt>Notes</dt>
            <dd>
              <InlineField
                :value="host.notes"
                type="textarea"
                label="Notes"
                field="notes"
                placeholder="No notes"
                :readonly="!canWrite"
                :save="(v) => save({ notes: v ? String(v) : null })"
              />
            </dd>
            <dt>Created</dt>
            <dd>{{ formatAbsolute(host.created) }}</dd>
            <dt>Events</dt>
            <dd>
              <RouterLink :to="{ name: 'events', query: { hostId: host.id } }">{{
                host.eventCount.toLocaleString()
              }}</RouterLink>
            </dd>
          </dl>
        </section>

        <section class="panel" aria-labelledby="sensors-title">
          <div id="sensors-title" class="panel-header">
            Sensors
            <span class="muted counts">
              {{ host.sensorCounts.ok }} ok · {{ host.sensorCounts.failing }} failing · {{ host.sensorCounts.total }} in
              all
            </span>
          </div>
          <EmptyState v-if="!sensors.length" title="No sensors yet." text="Add one to start monitoring this host." />
          <ul v-else-if="phone" class="mobile-sensors">
            <li
              v-for="s in sensors"
              :key="s.id"
              :class="{ 'row-muted': s.state === 'Disabled' || s.state === 'Ignored' }"
            >
              <RouterLink :to="{ name: 'sensor', params: { id: s.id } }" class="line">
                <StatusBadge :status="s.state" icon-only />
                <span class="strong">{{ s.name }}</span>
                <CodeTag :code="s.lastCode" compact :name="codes.nameOf(s.lastCode)" />
                <RelativeTime :value="s.lastEventAt" class="muted right" />
              </RouterLink>
              <div v-if="s.lastMessage" class="message muted ellipsis">{{ s.lastMessage }}</div>
            </li>
          </ul>
          <DataTable
            v-else
            :value="sensors"
            size="small"
            data-key="id"
            :row-class="
              (s: S['SensorSummary']) => (s.state === 'Disabled' || s.state === 'Ignored' ? 'row-muted' : undefined)
            "
          >
            <Column header="" style="width: 2.25rem">
              <template #body="{ data }"><StatusBadge :status="data.state" icon-only /></template>
            </Column>
            <Column header="Sensor">
              <template #body="{ data }">
                <span class="name-cell">
                  <i :class="typeIcon(data.type)" class="muted" :title="typeLabel(data.type)" aria-hidden="true" />
                  <RouterLink :to="{ name: 'sensor', params: { id: data.id } }" class="strong">{{
                    data.name
                  }}</RouterLink>
                  <span v-if="data.childCount" class="muted">({{ data.childCount }})</span>
                </span>
              </template>
            </Column>
            <Column header="Code">
              <template #body="{ data }"
                ><CodeTag :code="data.lastCode" :name="codes.nameOf(data.lastCode)"
              /></template>
            </Column>
            <Column header="Message" style="max-width: 16rem">
              <template #body="{ data }">
                <span class="ellipsis message-cell" :title="data.lastMessage ?? ''">{{ data.lastMessage }}</span>
              </template>
            </Column>
            <Column header="Last event" style="width: 7rem">
              <template #body="{ data }"><RelativeTime :value="data.lastEventAt" /></template>
            </Column>
            <Column header="Failing" style="width: 6rem">
              <template #body="{ data }">
                <SinceText v-if="data.state === 'Failing'" :since="data.failingSince" />
              </template>
            </Column>
          </DataTable>
        </section>
        <!-- Its pages, then its location's and that one's parents' (docs/locations-knowledge-plan.md §6.5). -->
        <KbPanel :host-id="host.id" :name="host.name" />
      </div>
    </template>
    <AddServerDialog v-if="host" v-model:visible="addingServer" :host-id="host.id" />
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.strip {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-6);
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
}

.numbers {
  display: flex;
  gap: var(--pnmt-space-6);
  margin: 0;
}

.numbers dt {
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
  font-weight: 600;
}

.numbers dd {
  margin: 0;
  font-weight: 600;
  font-size: 1.071rem;
  white-space: nowrap;
}

.bar90 {
  flex: 1;
  min-width: 0;
}

.bar-legend {
  display: flex;
  justify-content: space-between;
  font-size: 0.786rem;
}

.counts {
  font-weight: 400;
  margin-left: auto;
}

.strong {
  font-weight: 500;
}

.name-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  white-space: nowrap;
}

.message-cell {
  display: block;
  max-width: 16rem;
}

.mobile-sensors {
  list-style: none;
  margin: 0;
  padding: 0;
}

.mobile-sensors li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  color: var(--pnmt-text);
}

.right {
  margin-left: auto;
}

.message {
  font-size: 0.929rem;
  padding-left: 1.6rem;
}

@media (max-width: 599px) {
  .strip {
    flex-direction: column;
    align-items: stretch;
    gap: var(--pnmt-space-2);
  }

  .numbers {
    justify-content: space-between;
  }
}
</style>
