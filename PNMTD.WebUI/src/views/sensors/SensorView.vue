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
import { HOST, sensorImportanceOptions } from '@/views/notifications/importance'
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
import TabNav from '@/components/TabNav.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { useActions } from '@/composables/useActions'
import {
  humanize,
  typeIcon,
  typeLabel,
  usesGracePeriod,
  useSensorTypes,
  useStatusCodes,
} from '@/composables/useLookups'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatPercent } from '@/format'
import { useSession } from '@/stores/session'
import BackupsTab from './mikrotik/BackupsTab.vue'
import MikrotikCommandsTab from './mikrotik/CommandsTab.vue'
import UpdatesTab from './mikrotik/UpdatesTab.vue'
import MikrotikSettingsDialog from './mikrotik/MikrotikSettingsDialog.vue'
import MikrotikStatusStrip from './mikrotik/MikrotikStatusStrip.vue'
import SetupTab from './mikrotik/SetupTab.vue'
import CommandsTab from './udp/CommandsTab.vue'
import FirmwareTab from './udp/FirmwareTab.vue'
import LastPacketsTab from './udp/LastPacketsTab.vue'
import OtaDialog from './udp/OtaDialog.vue'
import ProtocolPage from './udp/protocol/ProtocolPage.vue'
import SecretsDialog from './udp/SecretsDialog.vue'
import UdpNodePanel from './udp/UdpNodePanel.vue'
import UdpSettingsDialog from './udp/UdpSettingsDialog.vue'
import UdpStatusStrip from './udp/UdpStatusStrip.vue'
import { useNodeActions } from './udp/useNodeActions'

/**
 * A sensor (§5.6). Left: its configuration (by type), how to report to it, its notification rules.
 * Right: its state and uptime, and its events, paged, on the first screen. A UDP node
 * (docs/udp-iot-plan.md §6.2) adds a status strip and subpages with routes of their own: Last
 * packets, Commands, Firmware, Protocol; its integration panel shows the endpoint, key and token.
 * A MikroTik router (docs/mikrotik-plan.md §11.1) adds its status strip, Setup, and in the SSH modes Backups.
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
const isAdmin = computed(() => session.can('Admin'))
const info = computed(() => types.info(sensor.value?.type))
const heartbeat = computed(() => usesGracePeriod(sensor.value?.type))

// --- a UDP node's subpages ------------------------------------------------------------------
const isUdp = computed(() => sensor.value?.type === 'UDP_NODE' && !!sensor.value.udp)
const isMikrotik = computed(() => sensor.value?.type === 'MIKROTIK' && !!sensor.value.mikrotik)
const tab = computed(
  () => ((isUdp.value || isMikrotik.value) && typeof route.params.tab === 'string' && route.params.tab) || 'events',
)
const mikrotikTabs = computed(() => {
  const to = (t?: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
  return [
    { key: 'events', label: 'Events', icon: 'pi pi-list', to: to() },
    {
      key: 'setup',
      label: 'Setup',
      icon: 'pi pi-cog',
      to: to('setup'),
      badge: sensor.value?.mikrotik?.setupState !== 'Ready' || sensor.value?.mikrotik?.scriptOutdated ? '!' : null,
    },
    ...(sensor.value?.mikrotik?.mode !== 'WireGuardOnly'
      ? [
          {
            key: 'backups',
            label: 'Backups',
            icon: 'pi pi-history',
            to: to('backups'),
            badge: sensor.value?.mikrotik?.backupCount || null,
          },
          {
            key: 'updates',
            label: 'Updates',
            icon: 'pi pi-upload',
            to: to('updates'),
            badge: sensor.value?.mikrotik?.updateAvailable ? '1' : null,
          },
          // Remote commands need CanWrite (§8.3).
          ...(canWrite.value ? [{ key: 'commands', label: 'Commands', icon: 'pi pi-code', to: to('commands') }] : []),
        ]
      : []),
  ]
})
const routerSettingsOpen = ref(false)
const tabs = computed(() => {
  const to = (t?: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
  return [
    { key: 'events', label: 'Events', icon: 'pi pi-list', to: to() },
    { key: 'last-packets', label: 'Last packets', icon: 'pi pi-inbox', to: to('last-packets') },
    {
      key: 'commands',
      label: 'Commands',
      icon: 'pi pi-send',
      to: to('commands'),
      badge: sensor.value?.udp?.openCommands || null,
    },
    { key: 'firmware', label: 'Firmware', icon: 'pi pi-upload', to: to('firmware') },
    { key: 'protocol', label: 'Protocol', icon: 'pi pi-book', to: to('protocol') },
  ]
})
const settingsOpen = ref(false)
const secretsOpen = ref(false)
const secretsMode = ref<'manual' | 'regenerate'>('manual')
function editSecrets(mode: 'manual' | 'regenerate') {
  secretsMode.value = mode
  secretsOpen.value = true
}
const updating = ref(false)
const nodeActions = useNodeActions()
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

/** A rule that covers the sensor through a scope (docs/ai-operations-plan.md §11.2): an exclusion takes it out. */
async function exclude(rule: S['NotificationRuleItem']) {
  await run(() => api.notifications.exclude(rule.id, id.value), {
    success: `Excluded from ${rule.name}`,
    invalidate: [keys.rules, keys.notifications],
  })
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

const udpItems = computed(() => {
  const s = sensor.value
  if (!isUdp.value || !s?.udp) return []
  return [
    { label: 'Edit UDP settings…', icon: 'pi pi-sliders-h', command: () => (settingsOpen.value = true) },
    { label: 'Set key or token…', icon: 'pi pi-pencil', command: () => editSecrets('manual') },
    { label: 'Regenerate key or token…', icon: 'pi pi-refresh', command: () => editSecrets('regenerate') },
    { label: 'Reset counter…', icon: 'pi pi-undo', command: () => nodeActions.resetCounter(s.id, s.name) },
    ...(s.udp.sessionMode === 'JoinRequired' || s.udp.sessionActive || s.udp.sessionPending
      ? [{ label: 'Reset session…', icon: 'pi pi-sign-out', command: () => nodeActions.resetSession(s.id, s.name) }]
      : []),
    ...(isAdmin.value && !s.udp.openUpdate
      ? [{ label: 'Update firmware…', icon: 'pi pi-upload', command: () => (updating.value = true) }]
      : []),
    { separator: true },
  ]
})

const mikrotikItems = computed(() =>
  isMikrotik.value
    ? [
        { label: 'Edit router settings…', icon: 'pi pi-sliders-h', command: () => (routerSettingsOpen.value = true) },
        { separator: true },
      ]
    : [],
)

/** PNMT's own conditions (docs/ai-operations-plan.md §4): renamed, moved or disabled, never deleted. */
const isSystem = computed(() => sensor.value?.type === 'SYSTEM')
const more = computed(() =>
  isSystem.value
    ? []
    : [
        ...udpItems.value,
        ...mikrotikItems.value,
        ...(sensor.value?.integration
          ? [{ label: 'New tokens…', icon: 'pi pi-key', command: () => (rotating.value = true) }]
          : []),
        { label: 'Delete sensor…', icon: 'pi pi-trash', command: remove },
      ],
)
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
          <MoreMenu v-if="canWrite && more.length" :items="more" label="More actions for the sensor" />
        </template>
      </PageHeader>

      <template v-if="isUdp">
        <UdpStatusStrip :sensor="sensor" />
        <TabNav :items="tabs" :active="tab" label="Node pages" />
      </template>
      <template v-else-if="isMikrotik">
        <MikrotikStatusStrip :sensor="sensor" />
        <TabNav :items="mikrotikTabs" :active="tab" label="Router pages" />
      </template>

      <SetupTab
        v-if="isMikrotik && tab === 'setup'"
        :sensor="sensor"
        :can-write="canWrite"
        :is-admin="isAdmin"
        @edit="routerSettingsOpen = true"
      />
      <UpdatesTab v-else-if="isMikrotik && tab === 'updates'" :sensor="sensor" :is-admin="isAdmin" />
      <BackupsTab
        v-else-if="isMikrotik && tab === 'backups'"
        :sensor="sensor"
        :can-write="canWrite"
        :is-admin="isAdmin"
      />
      <MikrotikCommandsTab v-else-if="isMikrotik && tab === 'commands'" :sensor="sensor" :is-admin="isAdmin" />
      <LastPacketsTab v-else-if="tab === 'last-packets'" :sensor="sensor" />
      <CommandsTab v-else-if="tab === 'commands'" :sensor="sensor" :can-write="canWrite" />
      <FirmwareTab v-else-if="tab === 'firmware'" :sensor="sensor" :is-admin="isAdmin" @update="updating = true" />
      <ProtocolPage v-else-if="tab === 'protocol'" :sensor-id="sensor.id" :sensor-name="sensor.name" />

      <div v-else class="two-columns">
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
              <dt>Importance</dt>
              <dd>
                <InlineField
                  :value="sensor.importance ?? HOST"
                  type="enum"
                  :options="sensorImportanceOptions(sensor.importance ? null : sensor.effectiveImportance)"
                  label="Importance"
                  :readonly="!canWrite"
                  :save="(v) => save({ importance: v === HOST ? null : (v as S['Importance']) })"
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
                <span v-if="isSystem"
                  >PNMT's own condition<template v-if="sensor.parameters === 'uplink'">
                    · <RouterLink :to="{ name: 'uplink' }">the uplink page</RouterLink></template
                  ></span
                >
                <InlineField
                  v-else-if="sensor.type !== 'DNS'"
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
              <dt>Notes</dt>
              <dd>
                <InlineField
                  :value="sensor.notes ?? null"
                  type="textarea"
                  label="Notes"
                  field="notes"
                  placeholder="No notes"
                  :readonly="!canWrite"
                  :save="(v) => save({ notes: v ? String(v) : null })"
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

          <section v-if="isUdp" class="panel" aria-labelledby="integration-title">
            <div id="integration-title" class="panel-header">Reporting over UDP</div>
            <div class="panel-body">
              <UdpNodePanel
                :sensor="sensor"
                :can-write="canWrite"
                @edit="settingsOpen = true"
                @manual="editSecrets('manual')"
                @regenerate="editSecrets('regenerate')"
              />
            </div>
          </section>
          <section
            v-else-if="sensor.integration && !isMikrotik && !isSystem"
            class="panel"
            aria-labelledby="integration-title"
          >
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
                  <span class="muted ellipsis"
                    >{{ humanize(rule.type) }} · {{ rule.recipient
                    }}<template v-if="rule.via?.startsWith('scope')"> · {{ rule.via }}</template></span
                  >
                  <Button
                    v-if="canWrite && rule.via?.startsWith('scope')"
                    v-tooltip="'Exclude this sensor from the rule\'s scope'"
                    icon="pi pi-minus-circle"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Exclude this sensor from ${rule.name}`"
                    @click="exclude(rule)"
                  />
                  <Button
                    v-else-if="canWrite"
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

      <template v-if="isUdp">
        <UdpSettingsDialog v-if="canWrite" v-model:visible="settingsOpen" :sensor="sensor" />
        <SecretsDialog
          v-if="canWrite"
          v-model:visible="secretsOpen"
          :sensor-id="sensor.id"
          :name="sensor.name"
          :mode="secretsMode"
        />
        <OtaDialog v-if="isAdmin" v-model:visible="updating" :sensor-ids="[sensor.id]" :names="[sensor.name]" />
      </template>

      <MikrotikSettingsDialog
        v-if="isMikrotik && canWrite"
        v-model:visible="routerSettingsOpen"
        :sensor="sensor"
        :is-admin="isAdmin"
      />

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
