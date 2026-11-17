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
import AuditHistory from '@/components/AuditHistory.vue'
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
import KbPanel from '@/views/kb/KbPanel.vue'
import RedminePanel from '@/views/redmine/RedminePanel.vue'
import SensorTicket from '@/views/redmine/SensorTicket.vue'
import ZabbixSensorTab from './zabbix/ZabbixSensorTab.vue'
import CertificateChangeBanner from './certificate/CertificateChangeBanner.vue'
import CertificateConfigRows from './certificate/CertificateConfigRows.vue'
import CertificateStatusStrip from './certificate/CertificateStatusStrip.vue'
import CertificateTab from './certificate/CertificateTab.vue'
import AdvancedDataTab from './advanced/AdvancedDataTab.vue'
import AdvancedRulesTab from './advanced/AdvancedRulesTab.vue'
import AdvancedStatusStrip from './advanced/AdvancedStatusStrip.vue'
import FieldCards from './advanced/FieldCards.vue'
import SensorNotesPanel from './SensorNotesPanel.vue'
import CookieNotesPanel from '@/views/cookie/CookieNotesPanel.vue'
import { useCookieNotes } from '@/views/cookie/cookieNotes'
import BackupsTab from './mikrotik/BackupsTab.vue'
import MikrotikCommandsTab from './mikrotik/CommandsTab.vue'
import UpdatesTab from './mikrotik/UpdatesTab.vue'
import MikrotikSettingsDialog from './mikrotik/MikrotikSettingsDialog.vue'
import MikrotikStatusStrip from './mikrotik/MikrotikStatusStrip.vue'
import SetupTab from './mikrotik/SetupTab.vue'
import RouterAccessTab from './mikrotik/RouterAccessTab.vue'
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
import AgentChangesTab from './agent/AgentChangesTab.vue'
import AgentDiagnosticsTab from './agent/AgentDiagnosticsTab.vue'
import AgentHardwareTab from './agent/AgentHardwareTab.vue'
import AgentNetworkTab from './agent/AgentNetworkTab.vue'
import AgentScriptsTab from './agent/AgentScriptsTab.vue'
import AgentStatusStrip from './agent/AgentStatusStrip.vue'
import AgentTab from './agent/AgentTab.vue'
import { useAgentServer } from './agent/useAgentServer'
import LinuxAccessTab from './linux/LinuxAccessTab.vue'
import LinuxJournalTab from './linux/LinuxJournalTab.vue'
import LinuxOverviewTab from './linux/LinuxOverviewTab.vue'
import LinuxRemoteAccessTab from './linux/LinuxRemoteAccessTab.vue'
import LinuxServicesTab from './linux/LinuxServicesTab.vue'
import LinuxSshKeysTab from './linux/LinuxSshKeysTab.vue'
import WindowsAccessTab from './windows/WindowsAccessTab.vue'
import WindowsEventLogTab from './windows/WindowsEventLogTab.vue'
import WindowsOverviewTab from './windows/WindowsOverviewTab.vue'
import WindowsRemoteAccessTab from './windows/WindowsRemoteAccessTab.vue'
import WindowsServicesTab from './windows/WindowsServicesTab.vue'
import WindowsWmiTab from './windows/WindowsWmiTab.vue'

/**
 * A sensor (§5.6). Left: its configuration (by type), how to report to it, its notification rules.
 * Right: its state and uptime, and its events, paged, on the first screen. A UDP node
 * (docs/udp-iot-plan.md §6.2) adds a status strip and subpages with routes of their own: Last
 * packets, Commands, Firmware, Protocol; its integration panel shows the endpoint, key and token.
 * A MikroTik router (docs/mikrotik-plan.md §11.1) adds its status strip, Setup, and in the SSH modes Backups.
 * An SSL certificate (docs/ssl-certificate-plan.md §8.3) adds its status strip, its rows, the change waiting
 * and the Certificate tab. An Advanced sensor (docs/advanced-sensor-plan.md §10.3) adds its status strip, its pinned fields,
 * and the Data and Rules tabs.
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
const canWrite = computed(() => session.can('Monitoring', 'Edit'))
// What names people (docs/cookie-object-notes-plan.md §4): logins, the journal, the event log, SSH keys.
const canDetails = computed(() => session.can('Monitoring', 'Details'))
// Cookie's notes on the sensor (docs/cookie-object-notes-plan.md §6), below the people's notes.
const cookieNotes = useCookieNotes('sensor', () => id.value)
// What only the Manage of the sensor's own area may do (docs/audit-redmine-zabbix-plan.md §5.2): a router's MikroTik &
// WireGuard, a node's UDP nodes, a server's Servers.
const isAdmin = computed(() =>
  isMikrotik.value
    ? session.can('Mikrotik', 'Manage')
    : isUdp.value
      ? session.can('UdpNodes', 'Manage')
      : isAgent.value
        ? session.can('Servers', 'Manage')
        : session.isAdmin,
)
const info = computed(() => types.info(sensor.value?.type))
const heartbeat = computed(() => usesGracePeriod(sensor.value?.type))

// --- a UDP node's subpages ------------------------------------------------------------------
const isUdp = computed(() => sensor.value?.type === 'UDP_NODE' && !!sensor.value.udp)
const isMikrotik = computed(() => sensor.value?.type === 'MIKROTIK' && !!sensor.value.mikrotik)
// A server's pages (docs/windows-sensor-plan.md §10.2; docs/linux-sensor-plan.md §11.2), the overview first: the shared tabs
// (views/sensors/agent/), and each platform's own (views/sensors/windows/, views/sensors/linux/).
const isWindows = computed(() => sensor.value?.type === 'WINDOWS')
const isLinux = computed(() => sensor.value?.type === 'LINUX')
const isAgent = computed(() => isWindows.value || isLinux.value)
// A Zabbix host's sensor (docs/audit-redmine-zabbix-plan.md §7.7): its overview, PNMT's events, its rules.
const isZabbix = computed(() => sensor.value?.type === 'ZABBIX' && !!sensor.value.zabbix)
// An SSL certificate sensor (docs/ssl-certificate-plan.md §6.4, §8.3): the sensor is Monitoring's, its certificate the
// Certificates area's; every change of the sensor needs Certificates: Edit besides Monitoring: Edit.
const isCertificateType = computed(() => sensor.value?.type === 'SSL_CERTIFICATE')
const isCertificate = computed(() => isCertificateType.value && !!sensor.value?.certificate)
const canCertificates = computed(() => session.can('Certificates', 'View'))
const canCheck = computed(() => session.can('Certificates', 'Edit'))
const canEditSensor = computed(
  () => canWrite.value && (!isCertificateType.value || session.can('Certificates', 'Edit')),
)
// An Advanced sensor (docs/advanced-sensor-plan.md §10.3): the app's JSON on the Data tab, its rules on the Rules tab.
const isAdvancedType = computed(() => sensor.value?.type === 'ADVANCED')
const isAdvanced = computed(() => isAdvancedType.value && !!sensor.value?.advanced)
const agent = useAgentServer(id, isAgent)
const tab = computed(
  () =>
    (route.params.tab === 'history' && 'history') ||
    ((isUdp.value || isMikrotik.value || isAgent.value || isZabbix.value || isCertificate.value || isAdvanced.value) &&
      typeof route.params.tab === 'string' &&
      route.params.tab) ||
    (isAgent.value || isZabbix.value ? 'overview' : 'events'),
)
// The Access tab's key filter, from a key's "Logins with this key" (?key=SHA256:…).
const keyQuery = computed(() => (typeof route.query.key === 'string' ? route.query.key : null))
const agentTabs = computed(() => (isWindows.value ? windowsTabs.value : linuxTabs.value))
const diagnosticsTab = (to: (t?: string) => object) => ({
  key: 'diagnostics',
  label: 'Diagnostics',
  icon: 'pi pi-check-circle',
  to: to('diagnostics'),
  badge: agent.server.value?.server.diagnosticsFailing || null,
})
const agentTab = (to: (t?: string) => object) => ({
  key: 'agent',
  label: 'Agent',
  icon: 'pi pi-cog',
  to: to('agent'),
  badge: agent.server.value?.server.connected === false ? '!' : null,
})
const linuxTabs = computed(() => {
  const to = (t?: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
  return [
    { key: 'overview', label: 'Overview', icon: 'pi pi-server', to: to() },
    { key: 'events', label: 'Events', icon: 'pi pi-list', to: to('events') },
    { key: 'hardware', label: 'Hardware', icon: 'pi pi-box', to: to('hardware') },
    { key: 'network', label: 'Network', icon: 'pi pi-sitemap', to: to('network') },
    { key: 'services', label: 'Services', icon: 'pi pi-cog', to: to('services') },
    { key: 'remote-access', label: 'Remote access', icon: 'pi pi-desktop', to: to('remote-access') },
    // Logins, the journal and the SSH keys hold names, addresses and key comments: Monitoring's Details.
    ...(canDetails.value
      ? [
          { key: 'access', label: 'Access', icon: 'pi pi-sign-in', to: to('access') },
          { key: 'journal', label: 'Journal', icon: 'pi pi-exclamation-triangle', to: to('journal') },
          { key: 'ssh-keys', label: 'SSH keys', icon: 'pi pi-key', to: to('ssh-keys') },
        ]
      : []),
    diagnosticsTab(to),
    { key: 'changes', label: 'Changes', icon: 'pi pi-history', to: to('changes') },
    // Bash scripts (§8): Admin.
    ...(isAdmin.value ? [{ key: 'scripts', label: 'Scripts', icon: 'pi pi-code', to: to('scripts') }] : []),
    agentTab(to),
    { key: 'history', label: 'History', icon: 'pi pi-history', to: to('history') },
  ]
})
const windowsTabs = computed(() => {
  const to = (t?: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
  return [
    { key: 'overview', label: 'Overview', icon: 'pi pi-microsoft', to: to() },
    { key: 'events', label: 'Events', icon: 'pi pi-list', to: to('events') },
    { key: 'hardware', label: 'Hardware', icon: 'pi pi-box', to: to('hardware') },
    { key: 'network', label: 'Network', icon: 'pi pi-sitemap', to: to('network') },
    { key: 'services', label: 'Services', icon: 'pi pi-cog', to: to('services') },
    { key: 'remote-access', label: 'Remote access', icon: 'pi pi-desktop', to: to('remote-access') },
    // Logins and the event log hold names and addresses: Monitoring's Details.
    ...(canDetails.value
      ? [
          { key: 'access', label: 'Access', icon: 'pi pi-sign-in', to: to('access') },
          { key: 'event-log', label: 'Event log', icon: 'pi pi-exclamation-triangle', to: to('event-log') },
        ]
      : []),
    diagnosticsTab(to),
    { key: 'changes', label: 'Changes', icon: 'pi pi-history', to: to('changes') },
    // PowerShell scripts (§7.3): Admin.
    ...(isAdmin.value ? [{ key: 'scripts', label: 'Scripts', icon: 'pi pi-code', to: to('scripts') }] : []),
    // WMI (§16): the agent's queries, their switches and timeouts, and own queries. Admin.
    ...(isAdmin.value ? [{ key: 'wmi', label: 'WMI', icon: 'pi pi-database', to: to('wmi') }] : []),
    agentTab(to),
    { key: 'history', label: 'History', icon: 'pi pi-history', to: to('history') },
  ]
})
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
          // Its logins hold names and addresses: Monitoring's Details, as a server's (docs/cookie-access-plan.md §10.4).
          ...(canDetails.value ? [{ key: 'access', label: 'Access', icon: 'pi pi-sign-in', to: to('access') }] : []),
        ]
      : []),
    { key: 'history', label: 'History', icon: 'pi pi-history', to: to('history') },
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
    { key: 'history', label: 'History', icon: 'pi pi-history', to: to('history') },
  ]
})
// Every other sensor: its page, and its History (docs/audit-redmine-zabbix-plan.md §4.3).
const plainTabs = computed(() => [
  { key: 'events', label: 'Overview', icon: 'pi pi-list', to: { name: 'sensor', params: { id: id.value } } },
  {
    key: 'history',
    label: 'History',
    icon: 'pi pi-history',
    to: { name: 'sensor', params: { id: id.value, tab: 'history' } },
  },
])
const zabbixTabs = computed(() => {
  const to = (t?: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
  return [
    { key: 'overview', label: 'Overview', icon: 'pi pi-chart-bar', to: to() },
    { key: 'events', label: 'Events', icon: 'pi pi-list', to: to('events') },
    { key: 'setup', label: 'Setup', icon: 'pi pi-cog', to: to('setup') },
    { key: 'history', label: 'History', icon: 'pi pi-history', to: to('history') },
  ]
})
// The overview keeps the plain page's key; the Certificate tab for Certificates: View, with a mark while 487 or 488.
const certificateTabs = computed(() => [
  plainTabs.value[0]!,
  ...(canCertificates.value
    ? [
        {
          key: 'certificate',
          label: 'Certificate',
          icon: 'pi pi-lock',
          to: { name: 'sensor', params: { id: id.value, tab: 'certificate' } },
          badge: sensor.value?.lastCode === 487 || sensor.value?.lastCode === 488 ? '!' : null,
        },
      ]
    : []),
  plainTabs.value[1]!,
])
// The overview keeps the plain page's key; Data carries a mark while the latest report can't be read, Rules the failing ones.
const advancedTabs = computed(() => {
  const a = sensor.value?.advanced
  const failing =
    a?.rules.filter((r) => r.enabled && (r.last?.outcome === 'Fail' || r.last?.outcome === 'Warn')).length ?? 0
  const to = (t?: string) => ({ name: 'sensor', params: { id: id.value, tab: t } })
  return [
    plainTabs.value[0]!,
    {
      key: 'data',
      label: 'Data',
      icon: 'pi pi-database',
      to: to('data'),
      badge: sensor.value?.lastCode === 496 || (a?.latest && !a.latest.valid) ? '!' : null,
    },
    { key: 'rules', label: 'Rules', icon: 'pi pi-list-check', to: to('rules'), badge: failing || null },
    plainTabs.value[1]!,
  ]
})
const checking = ref(false)
async function checkNow() {
  checking.value = true
  try {
    const detail = await run(() => api.certificates.check(id.value), {
      success: 'Certificate checked',
      failure: 'Not checked',
      invalidate: [keys.sensor(id.value), keys.events],
    })
    if (detail) queryClient.setQueryData(keys.certificate(id.value), detail)
  } finally {
    checking.value = false
  }
}
const settingsOpen = ref(false)
const secretsOpen = ref(false)
const secretsMode = ref<'manual' | 'regenerate'>('manual')
function editSecrets(mode: 'manual' | 'regenerate') {
  secretsMode.value = mode
  secretsOpen.value = true
}
const updating = ref(false)
const nodeActions = useNodeActions()
// The type can change between the types one may create (as in v0.12), not to or from DNS; to an SSL certificate with
// Certificates: Edit, checked daily (its interval is 1 hour to 30 days); to an Advanced sensor without parameters.
const typeOptions = computed(() =>
  types.list.value
    .filter((t) => t.canCreate && (t.type !== 'SSL_CERTIFICATE' || session.can('Certificates', 'Edit')))
    .map((t) => ({ label: typeLabel(t.type), value: t.type })),
)
function saveType(type: S['SensorType']) {
  if (type === 'ADVANCED' && sensor.value?.type !== type) {
    // An Advanced sensor takes no parameters, and expects a report every 30 s or more, or never (docs/advanced-sensor-plan.md §8.1).
    const interval = sensor.value?.interval ?? 0
    return save({ type, parameters: null, interval: interval >= 30 ? interval : 0 })
  }
  return save(type === 'SSL_CERTIFICATE' && sensor.value?.type !== type ? { type, interval: 0 } : { type })
}

async function save(patch: Partial<S['SensorRequest']>) {
  const updated = await api.sensors.update(id.value, sensorRequest(sensor.value!, patch))
  queryClient.setQueryData(keys.sensor(id.value), updated)
  await invalidate([
    keys.host(updated.host.id),
    keys.hosts,
    ...(updated.type === 'SSL_CERTIFICATE' ? [keys.certificate(id.value)] : []),
  ])
}
const saveNotes = (notes: string | null) => save({ notes })

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
        ...(sensor.value?.integration && !isAgent.value
          ? [{ label: 'New tokens…', icon: 'pi pi-key', command: () => (rotating.value = true) }]
          : []),
        ...(canEditSensor.value ? [{ label: 'Delete sensor…', icon: 'pi pi-trash', command: remove }] : []),
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
          <SensorTicket v-if="sensor.state === 'Failing'" :sensor-id="sensor.id" />
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
      <template v-else-if="isAgent">
        <AgentStatusStrip :server="agent.server.value" />
        <TabNav :items="agentTabs" :active="tab" label="Server pages" />
      </template>
      <TabNav v-else-if="isZabbix" :items="zabbixTabs" :active="tab" label="Zabbix sensor pages" />
      <template v-else-if="isCertificate">
        <CertificateStatusStrip
          :sensor="sensor"
          :can-check="canCheck"
          :checking="checking"
          :show-error="tab !== 'certificate'"
          @check="checkNow"
        />
        <TabNav :items="certificateTabs" :active="tab" label="Sensor pages" />
        <CertificateChangeBanner v-if="tab !== 'history'" :sensor="sensor" :can-accept="canCheck" />
      </template>
      <template v-else-if="isAdvanced">
        <AdvancedStatusStrip :sensor="sensor" />
        <TabNav :items="advancedTabs" :active="tab" label="Sensor pages" />
      </template>
      <TabNav v-else :items="plainTabs" :active="tab" label="Sensor pages" />

      <AuditHistory v-if="tab === 'history'" :subject-id="sensor.id" :name="sensor.name" />
      <CertificateTab
        v-else-if="isCertificate && tab === 'certificate' && canCertificates"
        :key="sensor.id"
        :sensor="sensor"
        :can-edit="canEditSensor"
        :can-manage="session.can('Certificates', 'Manage')"
      />
      <AdvancedDataTab
        v-else-if="isAdvanced && tab === 'data'"
        :key="`data-${sensor.id}`"
        :sensor="sensor"
        :can-write="canWrite"
      />
      <AdvancedRulesTab
        v-else-if="isAdvanced && tab === 'rules'"
        :key="`rules-${sensor.id}`"
        :sensor="sensor"
        :can-write="canWrite"
      />
      <ZabbixSensorTab
        v-else-if="isZabbix && (tab === 'overview' || tab === 'setup')"
        :sensor="sensor"
        :tab="tab"
        :can-write="canWrite"
      />
      <SetupTab
        v-else-if="isMikrotik && tab === 'setup'"
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
      <RouterAccessTab v-else-if="isMikrotik && tab === 'access' && canDetails" :sensor="sensor" />
      <LastPacketsTab v-else-if="tab === 'last-packets'" :sensor="sensor" />
      <CommandsTab v-else-if="tab === 'commands'" :sensor="sensor" :can-write="canWrite" />
      <FirmwareTab v-else-if="tab === 'firmware'" :sensor="sensor" :is-admin="isAdmin" @update="updating = true" />
      <ProtocolPage v-else-if="tab === 'protocol'" :sensor-id="sensor.id" :sensor-name="sensor.name" />
      <template v-else-if="isAgent && tab !== 'events'">
        <div v-if="!agent.server.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
        <WindowsOverviewTab
          v-else-if="isWindows && tab === 'overview'"
          :server="agent.server.value"
          :can-write="canWrite"
        >
          <template #notes>
            <SensorNotesPanel :notes="sensor.notes ?? null" :can-write="canWrite" :save="saveNotes" />
            <CookieNotesPanel
              :notes="cookieNotes.notes.value"
              :can-remove="cookieNotes.canRemove.value"
              :can-link-case="cookieNotes.canLinkCase.value"
              @remove="cookieNotes.remove"
            />
          </template>
        </WindowsOverviewTab>
        <LinuxOverviewTab v-else-if="isLinux && tab === 'overview'" :server="agent.server.value" :can-write="canWrite">
          <template #notes>
            <SensorNotesPanel :notes="sensor.notes ?? null" :can-write="canWrite" :save="saveNotes" />
            <CookieNotesPanel
              :notes="cookieNotes.notes.value"
              :can-remove="cookieNotes.canRemove.value"
              :can-link-case="cookieNotes.canLinkCase.value"
              @remove="cookieNotes.remove"
            />
          </template>
        </LinuxOverviewTab>
        <AgentHardwareTab v-else-if="tab === 'hardware'" :server="agent.server.value" :can-write="canWrite" />
        <AgentNetworkTab v-else-if="tab === 'network'" :server="agent.server.value" :can-write="canWrite" />
        <WindowsServicesTab
          v-else-if="isWindows && tab === 'services'"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <LinuxServicesTab
          v-else-if="isLinux && tab === 'services'"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <WindowsRemoteAccessTab
          v-else-if="isWindows && tab === 'remote-access'"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <LinuxRemoteAccessTab
          v-else-if="isLinux && tab === 'remote-access'"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <WindowsAccessTab
          v-else-if="isWindows && tab === 'access' && canDetails"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <LinuxAccessTab
          v-else-if="isLinux && tab === 'access' && canDetails"
          :server="agent.server.value"
          :can-write="canWrite"
          :key-fingerprint="keyQuery"
        />
        <LinuxJournalTab
          v-else-if="isLinux && tab === 'journal' && canDetails"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <LinuxSshKeysTab
          v-else-if="isLinux && tab === 'ssh-keys' && canDetails"
          :server="agent.server.value"
          :can-write="canWrite"
          :is-admin="isAdmin"
        />
        <WindowsEventLogTab
          v-else-if="isWindows && tab === 'event-log' && canDetails"
          :server="agent.server.value"
          :can-write="canWrite"
        />
        <AgentChangesTab v-else-if="tab === 'changes'" :server="agent.server.value" />
        <AgentScriptsTab v-else-if="tab === 'scripts' && isAdmin" :server="agent.server.value" />
        <WindowsWmiTab v-else-if="isWindows && tab === 'wmi' && isAdmin" :server="agent.server.value" />
        <AgentDiagnosticsTab
          v-else-if="tab === 'diagnostics'"
          :server="agent.server.value"
          :can-write="canWrite"
          :is-admin="isAdmin"
        />
        <AgentTab v-else-if="tab === 'agent'" :server="agent.server.value" :can-write="canWrite" :is-admin="isAdmin" />
        <KbPanel v-if="tab === 'overview'" :sensor-id="sensor.id" :name="sensor.name" />
        <RedminePanel
          v-if="tab === 'overview'"
          :sensor-id="sensor.id"
          :name="`${sensor.host.name} / ${sensor.name}`"
          :path="`/sensors/${sensor.id}`"
        />
      </template>

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
                  :readonly="!canEditSensor"
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
                  :readonly="!canEditSensor"
                  :save="(v) => save({ importance: v === HOST ? null : (v as S['Importance']) })"
                />
              </dd>
              <dt>Enabled</dt>
              <dd>
                <InlineField
                  :value="sensor.enabled"
                  type="bool"
                  label="Enabled"
                  :readonly="!canEditSensor"
                  :save="(v) => save({ enabled: Boolean(v) })"
                />
              </dd>
              <dt>Ignore</dt>
              <dd>
                <InlineField
                  :value="sensor.ignore"
                  type="bool"
                  label="Ignore"
                  :readonly="!canEditSensor"
                  :save="(v) => save({ ignore: Boolean(v) })"
                />
                <small class="field-hint">Ignored sensors do not count as problems.</small>
              </dd>
              <CertificateConfigRows v-if="isCertificate" :sensor="sensor" :readonly="!canEditSensor" :save="save" />
              <template v-if="isAdvanced">
                <dt>Names people</dt>
                <dd>
                  <InlineField
                    :value="sensor.advanced!.namesPeople"
                    type="bool"
                    label="Names people"
                    :readonly="!canEditSensor"
                    :save="(v) => save({ advanced: { namesPeople: Boolean(v), addStatusRule: false } })"
                  />
                  <small class="field-hint">Its reports need Monitoring: Details.</small>
                </dd>
              </template>
              <template v-if="info?.usesInterval && !isCertificateType">
                <dt>Interval</dt>
                <dd>
                  <InlineField
                    :value="sensor.interval"
                    type="number"
                    label="Interval"
                    field="interval"
                    :readonly="!canEditSensor"
                    :save="(v) => save({ interval: Number(v) })"
                  />
                  <small class="field-hint">{{
                    isAdvanced ? 'seconds between reports; 0: no expectation' : 'seconds'
                  }}</small>
                </dd>
              </template>
              <template v-if="heartbeat && (!isAdvanced || sensor.interval > 0)">
                <dt>Grace period</dt>
                <dd>
                  <InlineField
                    :value="sensor.gracePeriod"
                    type="number"
                    label="Grace period"
                    field="gracePeriod"
                    :readonly="!canEditSensor"
                    :save="(v) => save({ gracePeriod: Number(v) })"
                  />
                  <small class="field-hint">seconds</small>
                </dd>
              </template>
              <template v-if="(info?.parameters || sensor.parameters) && !isCertificateType">
                <dt>{{ sensor.type === 'PING' ? 'Target' : 'Parameters' }}</dt>
                <dd>
                  <InlineField
                    :value="sensor.parameters"
                    label="Parameters"
                    field="parameters"
                    mono
                    :readonly="!canEditSensor"
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
                  :readonly="!canEditSensor"
                  :save="(v) => saveType(v as S['SensorType'])"
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
                  :readonly="!canEditSensor"
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
                  :readonly="!canEditSensor"
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
          <CookieNotesPanel
            :notes="cookieNotes.notes.value"
            :can-remove="cookieNotes.canRemove.value"
            :can-link-case="cookieNotes.canLinkCase.value"
            @remove="cookieNotes.remove"
          />

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
            v-else-if="sensor.integration && !isMikrotik && !isSystem && !isAgent && !isCertificateType"
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
          <!-- Its pages, then its host's, the host's location's and that one's parents' (docs/locations-knowledge-plan.md §6.5). -->
          <KbPanel :sensor-id="sensor.id" :name="sensor.name" />
          <!-- Its tickets, then those via its host and locations (docs/audit-redmine-zabbix-plan.md §6.6). -->
          <RedminePanel
            :sensor-id="sensor.id"
            :name="`${sensor.host.name} / ${sensor.name}`"
            :path="`/sensors/${sensor.id}`"
          />
        </div>

        <div class="column">
          <FieldCards v-if="isAdvanced" :sensor="sensor" pinned-only :can-write="canWrite" />
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
