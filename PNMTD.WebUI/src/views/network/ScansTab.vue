<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import InputNumber from 'primevue/inputnumber'
import ProgressBar from 'primevue/progressbar'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import { hourLabel, progressLabel, secondsLabel, stepLabel } from './networkFormat'
import ScanItems from './ScanItems.vue'

/**
 * Scans (docs/network-map-plan.md §7, §8.7): what runs now and how many routers are left, the queue, starting a scan (all
 * routers or a location: Manage; one router: Commands: Run), the schedule (off until someone turns it on; 14 days at 02:00
 * by default, from the first full scan by hand), and the history with each router's result.
 */
const props = defineProps<{ status: S['NetworkScanStatus'] | undefined }>()
const session = useSession()
const { run } = useActions()
const manage = computed(() => session.can('NetworkMap', 'Manage'))
const oneRouter = computed(() => manage.value || session.can('Commands', 'Run'))

const settings = useQuery({ queryKey: [...keys.network, 'settings'], queryFn: () => api.network.settings() })
const routers = useQuery({
  queryKey: [...keys.network, 'graph', 'routers'],
  queryFn: () => api.network.graph({ routers: 'all' }),
  enabled: oneRouter,
})
const routerOptions = computed(() =>
  (routers.data.value?.nodes ?? [])
    .filter((n) => n.kind === 'Router' && n.sensorId)
    .map((n) => ({ label: n.name, value: n.sensorId! })),
)
const locationId = ref<string | null>(null)
const routerId = ref<string | null>(null)

async function start(body: S['NetworkScanRequest'], what: string) {
  await run(() => api.network.startScan(body), {
    success: `${what}: queued`,
    failure: 'Not started',
    invalidate: [keys.network],
  })
}
async function cancel(scan: S['NetworkScanRow']) {
  await run(() => api.network.cancelScan(scan.id), {
    success: 'Cancelled',
    failure: 'Not cancelled',
    invalidate: [keys.network],
  })
}

// The schedule.
const enabled = ref(false)
const interval = ref(14)
const hour = ref(2)
const retention = ref(90)
watch(
  () => settings.data.value,
  (s) => {
    if (!s) return
    enabled.value = s.scanScheduleEnabled
    interval.value = s.scanIntervalDays
    hour.value = s.scanHour
    retention.value = s.retentionDays
  },
  { immediate: true },
)
const changed = computed(() => {
  const s = settings.data.value
  return (
    !!s &&
    (s.scanScheduleEnabled !== enabled.value ||
      s.scanIntervalDays !== interval.value ||
      s.scanHour !== hour.value ||
      s.retentionDays !== retention.value)
  )
})
const presets = [
  { label: 'Every week', value: 7 },
  { label: 'Every 14 days', value: 14 },
  { label: 'Every month', value: 30 },
]
const custom = computed(() => !presets.some((p) => p.value === interval.value))
const intervalChoice = computed({
  get: () => (custom.value ? 0 : interval.value),
  set: (v: number) => (interval.value = v === 0 ? (custom.value ? interval.value : 21) : v),
})
const intervalOptions = [...presets, { label: 'Custom…', value: 0 }]
const hours = Array.from({ length: 24 }, (_, h) => ({ label: hourLabel(h), value: h }))
async function save() {
  await run(
    () =>
      api.network.saveSettings({
        scanScheduleEnabled: enabled.value,
        scanIntervalDays: interval.value,
        scanHour: hour.value,
        retentionDays: retention.value,
      }),
    { success: 'Schedule saved', failure: 'Not saved', invalidate: [keys.network] },
  )
}

const history = useServerTable<S['NetworkScanRow']>({
  key: [...keys.network, 'scans'],
  fetch: (q) => api.network.scans(q),
  pageSize: 20,
  refetchInterval: computed(() => (props.status?.running ? 3000 : 30_000)),
})
const expanded = ref<Record<string, boolean>>({})
const percent = (s: S['NetworkScanRow']) =>
  s.total ? Math.round(((s.succeeded + s.failed + s.skipped) / s.total) * 100) : 0
const stateSeverity = (s: string) =>
  s === 'Done' ? 'success' : s === 'Running' ? 'info' : s === 'Cancelled' ? 'warn' : 'secondary'
</script>

<template>
  <div class="tab">
    <section class="card" data-testid="scan-now">
      <h2>Now</h2>
      <template v-if="status?.running">
        <p>
          Scanning <strong>{{ status.running.currentRouterName ?? '…' }}</strong>
          <template v-if="status.running.currentStep">: {{ stepLabel(status.running) }}</template>
        </p>
        <ProgressBar :value="percent(status.running)" :show-value="false" style="height: 0.5rem" />
        <p class="muted small">
          {{ progressLabel(status.running) }} · {{ status.running.scopeName }} · started
          <RelativeTime :value="status.running.startedAt ?? status.running.createdAt" /> by
          {{ status.running.requestedByName ?? 'the schedule' }}
          <template v-if="status.running.estimatedSecondsLeft">
            · {{ secondsLabel(status.running.estimatedSecondsLeft) }}</template
          >
        </p>
        <Button
          v-if="manage"
          label="Cancel"
          icon="pi pi-times"
          size="small"
          severity="secondary"
          @click="cancel(status.running)"
        />
      </template>
      <p v-else class="muted">No scan is running.</p>
      <template v-if="status?.queued.length">
        <h3>Queued</h3>
        <ul class="queue">
          <li v-for="q in status.queued" :key="q.id">
            {{ q.scopeName }} · {{ q.total }} router{{ q.total === 1 ? '' : 's' }}
            <Button v-if="manage" label="Cancel" size="small" text @click="cancel(q)" />
          </li>
        </ul>
      </template>
      <p v-if="status && !status.enabled" class="warn">
        The network map is turned off on this installation (NetworkMap__Enabled or Mikrotik__Enabled).
      </p>
    </section>

    <section v-if="oneRouter" class="card">
      <h2>Start a scan</h2>
      <p class="muted small">
        PNMT reads each router's interfaces, MAC table, neighbors, ARP and DHCP leases as its own read-only user, one
        router at a time.
      </p>
      <div class="row">
        <Button
          v-if="manage"
          label="Scan all routers"
          icon="pi pi-sync"
          size="small"
          @click="start({ scope: 'All' }, 'All routers')"
        />
      </div>
      <div v-if="manage" class="row">
        <LocationPicker v-model="locationId" none-label="A location…" size="small" aria-label="Location to scan" />
        <Button
          label="Scan the location"
          size="small"
          severity="secondary"
          :disabled="!locationId"
          @click="start({ scope: 'Location', locationId: locationId! }, 'The location')"
        />
      </div>
      <div class="row">
        <Select
          v-model="routerId"
          :options="routerOptions"
          option-label="label"
          option-value="value"
          filter
          placeholder="A router…"
          size="small"
          aria-label="Router to scan"
        />
        <Button
          label="Scan the router"
          size="small"
          severity="secondary"
          :disabled="!routerId"
          @click="start({ scope: 'Router', sensorId: routerId! }, 'The router')"
        />
      </div>
    </section>

    <section class="card" data-testid="scan-schedule">
      <h2>Schedule</h2>
      <div class="row">
        <ToggleSwitch v-model="enabled" input-id="schedule-on" :disabled="!manage" />
        <label for="schedule-on">{{ enabled ? 'On' : 'Off' }}</label>
        <Select
          v-model="intervalChoice"
          :options="intervalOptions"
          option-label="label"
          option-value="value"
          size="small"
          :disabled="!manage || !enabled"
          aria-label="Interval"
        />
        <InputNumber
          v-if="custom"
          v-model="interval"
          :min="1"
          :max="365"
          suffix=" days"
          size="small"
          :disabled="!manage || !enabled"
          aria-label="Days"
        />
        <span>at</span>
        <Select
          v-model="hour"
          :options="hours"
          option-label="label"
          option-value="value"
          size="small"
          :disabled="!manage || !enabled"
          aria-label="Hour"
        />
        <span class="muted small">{{ settings.data.value?.timeZone ?? '' }}</span>
      </div>
      <div class="row">
        <label for="retention">Interfaces no router has seen are kept</label>
        <InputNumber
          v-model="retention"
          input-id="retention"
          :min="7"
          :max="3650"
          suffix=" days"
          size="small"
          :disabled="!manage"
        />
        <Button v-if="manage" label="Save" size="small" :disabled="!changed" @click="save" />
      </div>
      <p class="muted small">
        <template v-if="status?.lastFull">Last full scan: {{ formatAbsolute(status.lastFull.createdAt) }}. </template>
        <template v-if="enabled && status?.nextScheduledAt"
          >Next: {{ formatAbsolute(status.nextScheduledAt) }}.</template
        >
        <template v-else-if="enabled">The schedule counts from the first full scan: start it by hand.</template>
        <template v-else>Off: the first scan is started by hand, then the schedule can take over.</template>
      </p>
    </section>

    <section class="card">
      <h2>History</h2>
      <ListTable v-model:expanded="expanded" :table="history" what="scans" empty-text="No scans yet.">
        <Column expander style="width: 2.5rem" />
        <Column header="Scope">
          <template #body="{ data }"
            >{{ data.scopeName }}
            <span class="muted small"
              >· {{ data.trigger === 'Schedule' ? 'scheduled' : `by ${data.requestedByName ?? '–'}` }}</span
            ></template
          >
        </Column>
        <Column header="Started" style="width: 10rem">
          <template #body="{ data }"><RelativeTime :value="data.startedAt ?? data.createdAt" /></template>
        </Column>
        <Column header="State" style="width: 8rem">
          <template #body="{ data }"><Tag :value="data.state" :severity="stateSeverity(data.state)" /></template>
        </Column>
        <Column header="Routers">
          <template #body="{ data }"
            ><span class="small">{{ progressLabel(data) }}</span></template
          >
        </Column>
        <template #expansion="{ data }">
          <ScanItems :scan-id="data.id" :live="data.state === 'Running' || data.state === 'Queued'" />
        </template>
        <template #mobile="{ item }">
          <div>{{ item.scopeName }} · {{ item.state }}</div>
          <div class="muted small">{{ progressLabel(item) }}</div>
        </template>
      </ListTable>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.card {
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  padding: var(--pnmt-space-3);
  background: var(--pnmt-panel);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

h2 {
  margin: 0;
  font-size: 1rem;
}

h3 {
  margin: var(--pnmt-space-2) 0 0;
  font-size: 0.929rem;
}

p {
  margin: 0;
}

.row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.queue {
  margin: 0;
  padding-left: 1.2rem;
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
}
</style>
