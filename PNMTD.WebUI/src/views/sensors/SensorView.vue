<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, sensorRequest, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import SensorUrlPanel from '@/components/SensorUrlPanel.vue'
import SinceText from '@/components/SinceText.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { useActions } from '@/composables/useActions'
import { humanize, typeIcon, typeLabel, useSensorTypes, useStatusCodes } from '@/composables/useLookups'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatPercent } from '@/format'
import { useSession } from '@/stores/session'

/**
 * A sensor (§5.6). Left: its configuration (by type), how to report to it, its notification rules.
 * Right: its state and uptime, and its events, paged, on the first screen.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const codes = useStatusCodes()
const types = useSensorTypes()
const queryClient = useQueryClient()
const { run, ask, invalidate, done } = useActions()

const id = computed(() => String(route.params.id))
const query = useQuery({
  queryKey: computed(() => keys.sensor(id.value)),
  queryFn: () => api.sensors.get(id.value),
  refetchInterval: 30_000,
})
const sensor = computed(() => query.data.value)
const canWrite = computed(() => session.can('User'))
const info = computed(() => types.info(sensor.value?.type))
const heartbeat = computed(() => sensor.value?.type === 'HEARTBEAT' || sensor.value?.type === 'HEARTBEAT_VALUECHECK')
// The type can change between the types one may create (as in v0.12), not to or from DNS.
const typeOptions = computed(() =>
  types.list.value.filter((t) => t.canCreate).map((t) => ({ label: typeLabel(t.type), value: t.type })),
)

async function save(patch: Partial<S['SensorRequest']>) {
  const updated = await api.sensors.update(id.value, sensorRequest(sensor.value!, patch))
  queryClient.setQueryData(keys.sensor(id.value), updated)
  await invalidate([keys.host(updated.host.id), keys.hosts])
}

// --- events -----------------------------------------------------------------------------------
const status = ref('')
const statuses = [
  { label: 'All', value: '' },
  { label: 'Failed', value: 'failed' },
  { label: 'OK', value: 'ok' },
]
// The sensor id is in the filters, so the key changes when the router reuses this page for another sensor.
const eventFilters = computed(() => ({ status: status.value || undefined, sensorId: id.value }))
const events = useServerTable<S['EventItem']>({
  key: [...keys.events, 'sensor'],
  fetch: (q) => api.sensors.events(id.value, q),
  defaultSort: '-created',
  pageSize: 25,
  filters: eventFilters,
})

// --- notification rules ----------------------------------------------------------------------------
const linked = useQuery({
  queryKey: computed(() => [...keys.rules, 'sensor', id.value]),
  queryFn: () => api.sensors.rules(id.value),
})
const allRules = useQuery({
  queryKey: [...keys.rules, 'all'],
  queryFn: () => api.rules.list({ pageSize: 500 }),
  enabled: canWrite,
})
const linkable = computed(() =>
  (allRules.data.value?.items ?? []).filter((r) => !(linked.data.value ?? []).some((l) => l.id === r.id)),
)
const toLink = ref<string | null>(null)

async function link() {
  if (!toLink.value) return
  const ruleId = toLink.value
  await run(() => api.sensors.link(id.value, ruleId), { success: 'Rule linked', invalidate: [keys.rules] })
  toLink.value = null
}

async function unlink(rule: S['NotificationRuleItem']) {
  await run(() => api.sensors.unlink(id.value, rule.id), { success: `${rule.name} unlinked`, invalidate: [keys.rules] })
}

// --- actions ----------------------------------------------------------------------------------
const rotating = ref(false)
const rotateWrite = ref(true)
const rotateRead = ref(false)

async function rotate() {
  const result = await run(() => api.sensors.rotate(id.value, { write: rotateWrite.value, read: rotateRead.value }), {
    success: 'New tokens issued',
    invalidate: [keys.sensor(id.value)],
  })
  if (result) {
    rotating.value = false
  }
}

async function remove() {
  const s = sensor.value!
  const ok = await ask(
    `Delete ${s.name}?`,
    `Its events${s.children.length ? ` and its ${s.children.length} child sensors` : ''} are deleted with it, and its URLs stop working. This cannot be undone.`,
  )
  if (
    ok &&
    (await done(() => api.sensors.remove(s.id), {
      success: `${s.name} deleted`,
      invalidate: [keys.host(s.host.id), keys.hosts],
    }))
  ) {
    await router.replace({ name: 'host', params: { id: s.host.id } })
  }
}

const more = computed(() => [
  ...(sensor.value?.integration
    ? [{ label: 'New tokens…', icon: 'pi pi-key', command: () => (rotating.value = true) }]
    : []),
  { label: 'Delete sensor…', icon: 'pi pi-trash', command: remove },
])
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="query.isError.value && !sensor"
      :error="query.error.value"
      what="the sensor"
      @retry="query.refetch()"
    />
    <div v-else-if="!sensor" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader
        :title="sensor.name"
        :crumbs="[
          { label: 'Hosts', to: { name: 'hosts' } },
          { label: sensor.host.name, to: { name: 'host', params: { id: sensor.host.id } } },
        ]"
      >
        <template #status>
          <StatusBadge :status="sensor.state" />
          <span class="muted type"><i :class="typeIcon(sensor.type)" /> {{ typeLabel(sensor.type) }}</span>
          <CodeTag :code="sensor.lastCode" :name="codes.nameOf(sensor.lastCode)" />
          <span v-if="sensor.state === 'Failing'" class="muted">since <SinceText :since="sensor.failingSince" /></span>
        </template>
        <template #actions>
          <MoreMenu v-if="canWrite" :items="more" label="More actions for the sensor" />
        </template>
      </PageHeader>

      <div class="two-columns">
        <div class="column">
          <section class="panel" aria-labelledby="config-title">
            <div id="config-title" class="panel-header">Configuration</div>
            <dl class="panel-body facts">
              <dt>Name</dt>
              <dd>
                <InlineField
                  :value="sensor.name"
                  label="Name"
                  field="name"
                  :readonly="!canWrite"
                  :save="(v) => save({ name: String(v) })"
                />
              </dd>
              <dt>Enabled</dt>
              <dd>
                <InlineField
                  :value="sensor.enabled"
                  type="bool"
                  label="Enabled"
                  :readonly="!canWrite"
                  :save="(v) => save({ enabled: Boolean(v) })"
                />
              </dd>
              <dt>Ignore</dt>
              <dd>
                <InlineField
                  :value="sensor.ignore"
                  type="bool"
                  label="Ignore"
                  :readonly="!canWrite"
                  :save="(v) => save({ ignore: Boolean(v) })"
                />
                <small class="field-hint">Ignored sensors do not count as problems.</small>
              </dd>
              <template v-if="info?.usesInterval">
                <dt>Interval</dt>
                <dd>
                  <InlineField
                    :value="sensor.interval"
                    type="number"
                    label="Interval"
                    field="interval"
                    :readonly="!canWrite"
                    :save="(v) => save({ interval: Number(v) })"
                  />
                  <small class="field-hint">seconds</small>
                </dd>
              </template>
              <template v-if="heartbeat">
                <dt>Grace period</dt>
                <dd>
                  <InlineField
                    :value="sensor.gracePeriod"
                    type="number"
                    label="Grace period"
                    field="gracePeriod"
                    :readonly="!canWrite"
                    :save="(v) => save({ gracePeriod: Number(v) })"
                  />
                  <small class="field-hint">seconds</small>
                </dd>
              </template>
              <template v-if="info?.parameters || sensor.parameters">
                <dt>{{ sensor.type === 'PING' ? 'Target' : 'Parameters' }}</dt>
                <dd>
                  <InlineField
                    :value="sensor.parameters"
                    label="Parameters"
                    field="parameters"
                    mono
                    :readonly="!canWrite"
                    :save="(v) => save({ parameters: v ? String(v) : null })"
                  />
                  <small v-if="info?.parameters" class="field-hint">{{ info.parameters }}</small>
                </dd>
              </template>
              <dt>Type</dt>
              <dd>
                <InlineField
                  v-if="sensor.type !== 'DNS'"
                  :value="sensor.type"
                  type="enum"
                  :options="typeOptions"
                  label="Type"
                  :readonly="!canWrite"
                  :save="(v) => save({ type: v as S['SensorType'] })"
                />
                <span v-else>DNS · <RouterLink :to="{ name: 'dns-zones' }">its zone</RouterLink></span>
              </dd>
              <dt>Text id</dt>
              <dd>
                <InlineField
                  :value="sensor.textId"
                  label="Text id"
                  field="textId"
                  mono
                  placeholder="none"
                  :readonly="!canWrite"
                  :save="(v) => save({ textId: v ? String(v) : null })"
                />
              </dd>
              <dt>Created</dt>
              <dd>{{ formatAbsolute(sensor.created) }}</dd>
              <template v-if="sensor.parentSensorId">
                <dt>Part of</dt>
                <dd>
                  <RouterLink :to="{ name: 'sensor', params: { id: sensor.parentSensorId } }"
                    >its encapsulated parent</RouterLink
                  >
                </dd>
              </template>
            </dl>
          </section>

          <section v-if="sensor.integration" class="panel" aria-labelledby="integration-title">
            <div id="integration-title" class="panel-header">Integration</div>
            <div class="panel-body">
              <SensorUrlPanel :integration="sensor.integration" :type="sensor.type" />
            </div>
          </section>

          <section class="panel" aria-labelledby="rules-title">
            <div id="rules-title" class="panel-header">Notifications</div>
            <div class="panel-body rules">
              <EmptyState
                v-if="linked.data.value && !linked.data.value.length"
                icon="pi pi-bell-slash"
                title="No notification rule."
              />
              <ul v-else class="rule-list">
                <li v-for="rule in linked.data.value" :key="rule.id">
                  <i class="pi pi-bell muted" aria-hidden="true" />
                  <RouterLink :to="{ name: 'notification-rule', params: { id: rule.id } }">{{ rule.name }}</RouterLink>
                  <span class="muted ellipsis">{{ humanize(rule.type) }} · {{ rule.recipient }}</span>
                  <Button
                    v-if="canWrite"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Unlink ${rule.name}`"
                    @click="unlink(rule)"
                  />
                </li>
              </ul>
              <div v-if="canWrite && linkable.length" class="link-row">
                <Select
                  v-model="toLink"
                  :options="linkable"
                  option-label="name"
                  option-value="id"
                  placeholder="Link a rule…"
                  size="small"
                  filter
                  class="grow"
                  aria-label="Rule to link"
                />
                <Button label="Link" size="small" :disabled="!toLink" @click="link" />
              </div>
            </div>
          </section>
        </div>

        <div class="column">
          <section class="panel" aria-labelledby="status-title">
            <div id="status-title" class="panel-header">Status &amp; uptime</div>
            <div class="panel-body status">
              <dl class="numbers">
                <div>
                  <dt>24 h</dt>
                  <dd>{{ formatPercent(sensor.uptime24Hours, 2) }}</dd>
                </div>
                <div>
                  <dt>30 days</dt>
                  <dd>{{ formatPercent(sensor.uptime30Days, 2) }}</dd>
                </div>
                <div>
                  <dt>Last event</dt>
                  <dd><RelativeTime :value="sensor.lastEventAt" /></dd>
                </div>
              </dl>
              <UptimeBar :values="sensor.days.values ?? []" :start="sensor.days.start" :height="20" />
              <div v-if="sensor.lastMessage" class="last-message">
                <span class="muted">Last message</span>
                <div class="mono message-box">{{ sensor.lastMessage }}</div>
              </div>
              <div v-if="sensor.children.length" class="children">
                <span class="muted">Child sensors</span>
                <ul>
                  <li v-for="child in sensor.children" :key="child.id">
                    <StatusBadge :status="child.state" icon-only />
                    <RouterLink :to="{ name: 'sensor', params: { id: child.id } }">{{ child.name }}</RouterLink>
                    <CodeTag :code="child.lastCode" compact :name="codes.nameOf(child.lastCode)" />
                    <span class="muted ellipsis">{{ child.lastMessage }}</span>
                  </li>
                </ul>
              </div>
            </div>
          </section>

          <section aria-labelledby="events-title" class="events">
            <div class="events-header">
              <h2 id="events-title">Events</h2>
              <SelectButton
                v-model="status"
                :options="statuses"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
              />
              <RouterLink :to="{ name: 'events', query: { sensorId: sensor.id } }" class="all">All events ›</RouterLink>
            </div>
            <ListTable :table="events" what="events">
              <Column field="created" header="Time" sortable style="width: 8rem">
                <template #body="{ data }"><RelativeTime :value="data.created" /></template>
              </Column>
              <Column field="code" header="Code" sortable style="width: 6rem">
                <template #body="{ data }"
                  ><CodeTag :code="data.code" compact :name="codes.nameOf(data.code)"
                /></template>
              </Column>
              <Column header="Message">
                <template #body="{ data }"
                  ><span class="ellipsis cell" :title="data.message ?? ''">{{ data.message }}</span></template
                >
              </Column>
              <template #mobile="{ item }">
                <div class="mobile-event">
                  <CodeTag :code="item.code" compact />
                  <span class="ellipsis">{{ item.message }}</span>
                  <RelativeTime :value="item.created" class="muted right" />
                </div>
              </template>
            </ListTable>
          </section>
        </div>
      </div>

      <Dialog v-model:visible="rotating" header="New tokens" modal :style="{ width: '28rem' }">
        <p class="dialog-text">
          Every script or device that reports with the old token stops working until it uses the new URL. This cannot be
          undone.
        </p>
        <div class="checks">
          <label><Checkbox v-model="rotateWrite" binary /> Write token (reporting)</label>
          <label><Checkbox v-model="rotateRead" binary /> Read token (reading the state)</label>
        </div>
        <template #footer>
          <Button label="Cancel" severity="secondary" text @click="rotating = false" />
          <Button label="Issue new tokens" severity="danger" :disabled="!rotateWrite && !rotateRead" @click="rotate" />
        </template>
      </Dialog>
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.type {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.facts .field-hint {
  margin-left: var(--pnmt-space-2);
}

.rule-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rule-list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 32px;
}

.link-row {
  display: flex;
  gap: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-2);
}

.grow {
  flex: 1;
  min-width: 0;
}

.status {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
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
}

.message-box {
  margin-top: var(--pnmt-space-1);
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.children ul {
  list-style: none;
  margin: var(--pnmt-space-1) 0 0;
  padding: 0;
}

.children li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 28px;
}

.events {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.events-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.all {
  margin-left: auto;
}

.cell {
  display: block;
  max-width: 28rem;
}

.mobile-event {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.right {
  margin-left: auto;
  white-space: nowrap;
}

.dialog-text {
  margin-top: 0;
}

.checks {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.checks label {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
