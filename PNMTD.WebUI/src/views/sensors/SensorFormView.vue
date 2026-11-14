<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S, type SensorType } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'
import { typeIcon, typeLabel, usesGracePeriod, useSensorTypes } from '@/composables/useLookups'
import { formatDuration } from '@/format'
import { useSession } from '@/stores/session'
import MikrotikFields from './mikrotik/MikrotikFields.vue'
import ZabbixFields, { type ZabbixDraft } from './zabbix/ZabbixFields.vue'
import { draftErrors, MIKROTIK_FIELDS, mikrotikRequestFromDraft, newMikrotikDraft } from './mikrotik/mikrotikDraft'
import UdpNodeFields from './udp/UdpNodeFields.vue'
import { KEY_BYTES, newUdpDraft, secretError, TOKEN_BYTES, udpRequest } from './udp/udpDraft'

/**
 * A new sensor on a host (§5.6): the type decides which fields there are, each with its help. The
 * server validates the parameters (a regex, a ping target, a timespan such as "daily 8"). After the
 * creation the sensor's page shows its URLs. A UDP node (docs/udp-iot-plan.md §6.1) has a section
 * of its own; ?type=UDP_NODE&deviceId=42 preset it (the admin page's "Add UDP node", unknown senders).
 * So does a MikroTik router (docs/mikrotik-plan.md §11.1): after the creation its Setup tab shows the
 * join script.
 */
const route = useRoute()
const router = useRouter()
const types = useSensorTypes()
const session = useSession()
const { invalidate, notify } = useActions()

const hostId = computed(() => String(route.params.id))
const host = useQuery({ queryKey: computed(() => keys.host(hostId.value)), queryFn: () => api.hosts.get(hostId.value) })

const presets: SensorType[] = [
  'SIMPLE',
  'HEARTBEAT',
  'HEARTBEAT_VALUECHECK',
  'PING',
  'VALUECHECK',
  'ENCAPSULADED',
  'ONE_WITHIN_TIMESPAN',
  'ALL_WITHIN_TIMESPAN',
  'UDP_NODE',
  'MIKROTIK',
  'ZABBIX',
]
const presetType = presets.find((t) => t === route.query.type) ?? 'SIMPLE'
const presetDevice = Number(route.query.deviceId)
const type = ref<SensorType>(presetType)
const name = ref('')
const interval = ref<number | null>(null)
const grace = ref<number | null>(null)
const parameters = ref('')
const enabled = ref(true)
const textId = ref('')
const notes = ref('')
const udp = ref(newUdpDraft(Number.isInteger(presetDevice) && presetDevice > 0 ? presetDevice : null))
const mikrotik = ref(newMikrotikDraft(!!session.info?.wireGuard))
// A new router starts with the installation's backup schedule (docs/mikrotik-backup-plan.md §2.2), once it is known.
const backupDefaults = useQuery({
  queryKey: [...keys.mikrotik, 'backup-settings'],
  queryFn: api.mikrotik.backupSettings,
  enabled: computed(() => type.value === 'MIKROTIK'),
})
let scheduleFromDefaults = false
watch(
  () => backupDefaults.data.value,
  (settings) => {
    if (settings && !scheduleFromDefaults) {
      scheduleFromDefaults = true
      mikrotik.value = {
        ...mikrotik.value,
        backupIntervalHours: settings.defaultBackupIntervalHours ?? null,
        // MikroTik & WireGuard's Manage sees the default as the switch; for others the server applies it.
        encryptBackups: session.can('Mikrotik', 'Manage') ? settings.defaultEncryptBackups : null,
      }
    }
  },
  { immediate: true },
)
const errors = ref<Record<string, string | undefined>>({})
// A Zabbix host (docs/audit-redmine-zabbix-plan.md §7.8): the sensor is named as in Zabbix unless named otherwise.
const zabbix = ref<ZabbixDraft>({
  host: null,
  minSeverity: null,
  countAcknowledged: true,
  countSuppressed: false,
  unavailableFails: false,
})
watch(
  () => zabbix.value.host,
  (host, before) => {
    if (host && (!name.value.trim() || name.value === before?.name)) name.value = host.name
  },
)
const busy = ref(false)

// Zabbix sensors only while Zabbix is configured.
const creatable = computed(() =>
  types.list.value.filter((t) => t.canCreate && (t.type !== 'ZABBIX' || !!session.info?.zabbix)),
)
const info = computed(() => types.info(type.value))
const heartbeat = computed(() => usesGracePeriod(type.value))
const isUdp = computed(() => type.value === 'UDP_NODE')
const isMikrotik = computed(() => type.value === 'MIKROTIK')
const isZabbix = computed(() => type.value === 'ZABBIX')
const timespan = computed(() => type.value === 'ONE_WITHIN_TIMESPAN' || type.value === 'ALL_WITHIN_TIMESPAN')

// Sensible defaults per type: a ping every minute, a daily heartbeat with an hour of grace, a UDP
// node every 10 minutes with 5 minutes of grace, a router polled every 5 minutes with 10 of grace.
function defaults(t: SensorType) {
  interval.value =
    t === 'PING' ? 60 : t === 'UDP_NODE' ? 600 : t === 'MIKROTIK' ? 300 : usesGracePeriod(t) ? 86_400 : null
  grace.value = t === 'UDP_NODE' ? 300 : t === 'MIKROTIK' ? 600 : usesGracePeriod(t) ? 3_600 : null
}
defaults(type.value)
watch(type, (t) => {
  defaults(t)
  errors.value = {}
})

// The form says when the listener is off (§6.1); only an Admin may ask (/udp/status).
const status = useQuery({
  queryKey: [...keys.udp, 'status'],
  queryFn: api.udp.status,
  enabled: computed(() => isUdp.value && session.can('UdpNodes', 'Manage')),
})
const listenerOff = computed(() => {
  const s = status.data.value
  if (!s || s.listening) return null
  return s.enabled
    ? `The UDP listener is not running${s.error ? `: ${s.error}` : ''}. Nodes can be added, but no packet arrives until it runs.`
    : 'The UDP listener is off (Udp__Enabled=false in .env). Remove the setting, or set it to true, and restart PNMTD.'
})
const graceHint = computed(() => {
  if (!isUdp.value || !interval.value) return null
  const i = formatDuration(interval.value * 1000)
  const g = formatDuration((grace.value ?? 0) * 1000)
  return `${i} + ${g} grace: 460 after ${formatDuration((interval.value + (grace.value ?? 0)) * 1000)} without a packet.`
})

const parameterHint = computed(() => {
  switch (type.value) {
    case 'PING':
      return 'The host name or address to ping, for example 192.0.2.10 or nas.example.org.'
    case 'VALUECHECK':
    case 'HEARTBEAT_VALUECHECK':
      return 'A regular expression the message must match, for example ^[0-8]?[0-9] % used$ (275 when it matches, 475 when not).'
    case 'ONE_WITHIN_TIMESPAN':
    case 'ALL_WITHIN_TIMESPAN':
      return 'When to check, for example "daily 8", "weekly monday" or "monthly 15".'
    default:
      return info.value?.parameters ?? ''
  }
})

const udpFields = [
  'udp.deviceId',
  'udp.keyFamily',
  'udp.allowedSuites',
  'udp.responseSuite',
  'udp.sessionMode',
  'udp.ackMode',
  'udp.key',
  'udp.token',
]

async function submit() {
  errors.value = {
    name: name.value.trim() ? undefined : 'A sensor needs a name.',
    parameters: info.value?.parameters && !parameters.value.trim() ? 'This type needs it.' : undefined,
    interval: info.value?.usesInterval && !interval.value ? 'In seconds, more than 0.' : undefined,
    notes: notes.value.length > 4000 ? `At most 4000 characters (${notes.value.length}).` : undefined,
    'udp.allowedSuites': isUdp.value && !udp.value.allowedSuites.length ? 'Allow at least one suite.' : undefined,
    'udp.key': isUdp.value && udp.value.manual ? (secretError(udp.value.key, KEY_BYTES) ?? undefined) : undefined,
    'udp.token': isUdp.value && udp.value.manual ? (secretError(udp.value.token, TOKEN_BYTES) ?? undefined) : undefined,
    ...(isMikrotik.value ? draftErrors(mikrotik.value) : {}),
    'zabbix.zabbixHostId': isZabbix.value && !zabbix.value.host ? 'Pick the Zabbix host.' : undefined,
  }
  if (Object.values(errors.value).some((e) => e)) {
    return
  }
  busy.value = true
  try {
    const request: S['SensorRequest'] = {
      hostId: hostId.value,
      type: type.value,
      name: name.value.trim(),
      enabled: enabled.value,
      ignore: false,
      interval: interval.value ?? 0,
      gracePeriod: grace.value ?? 0,
      parameters: parameters.value.trim() || null,
      textId: textId.value.trim() || null,
      notes: notes.value.trim() ? notes.value : null,
    }
    if (isUdp.value) {
      request.udp = udpRequest(udp.value)
    }
    if (isMikrotik.value) {
      request.mikrotik = mikrotikRequestFromDraft(mikrotik.value)
    }
    if (isZabbix.value && zabbix.value.host) {
      request.zabbix = {
        zabbixHostId: zabbix.value.host.id,
        minSeverity: zabbix.value.minSeverity,
        countAcknowledged: zabbix.value.countAcknowledged,
        countSuppressed: zabbix.value.countSuppressed,
        unavailableFails: zabbix.value.unavailableFails,
      }
    }
    const sensor = await api.sensors.create(request)
    await invalidate([keys.host(hostId.value), keys.hosts, keys.udp])
    notify.success(
      `Sensor ${sensor.name} created`,
      isUdp.value
        ? 'Its endpoint, key and token are on its page.'
        : isMikrotik.value
          ? 'Its join script is on the Setup tab: paste it on the router.'
          : isZabbix.value
            ? "Its Zabbix host's problems are on its page."
            : 'Its URLs are on its page.',
    )
    for (const warning of sensor.udp?.warnings ?? []) {
      notify.info('Note', warning)
    }
    await router.replace({ name: 'sensor', params: { id: sensor.id, tab: isMikrotik.value ? 'setup' : undefined } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        name: e.field('name'),
        parameters: e.field('parameters'),
        interval: e.field('interval'),
        gracePeriod: e.field('gracePeriod'),
        type: e.field('type'),
        textId: e.field('textId'),
        notes: e.field('notes'),
        ...Object.fromEntries(udpFields.map((f) => [f, e.field(f)])),
        ...Object.fromEntries(MIKROTIK_FIELDS.map((f) => [f, e.field(f)])),
        'zabbix.zabbixHostId': e.field('zabbix.zabbixHostId'),
        'zabbix.minSeverity': e.field('zabbix.minSeverity'),
      }
    } else {
      notify.error(e, 'Not created')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader
      :title="isUdp ? 'New UDP node' : isMikrotik ? 'New MikroTik router' : 'New sensor'"
      :crumbs="[
        { label: 'Hosts', to: { name: 'hosts' } },
        { label: host.data.value?.name ?? '…', to: { name: 'host', params: { id: hostId } } },
      ]"
    />
    <form class="panel panel-body form-grid form-box" novalidate @submit.prevent="submit">
      <div class="field">
        <label for="sensor-type">Type</label>
        <Select
          v-model="type"
          input-id="sensor-type"
          :options="creatable"
          option-label="type"
          option-value="type"
          :loading="types.query.isLoading.value"
          :invalid="!!errors.type"
        >
          <template #value="{ value }">
            <span v-if="value" class="type"><i :class="typeIcon(value)" /> {{ typeLabel(value) }}</span>
          </template>
          <template #option="{ option }">
            <span class="type"><i :class="typeIcon(option.type)" /> {{ typeLabel(option.type) }}</span>
          </template>
        </Select>
        <small class="field-hint">{{ info?.description }}</small>
        <small class="field-hint"
          >A Windows or Linux server joins with a token: <em>Add server…</em> in the host's menu (Admin).</small
        >
      </div>
      <div class="field">
        <label for="sensor-name">Name</label>
        <InputText
          id="sensor-name"
          v-model="name"
          autofocus
          :invalid="!!errors.name"
          placeholder="backup, disk /, http"
        />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div v-if="info?.parameters || type === 'PING'" class="field wide">
        <label for="sensor-parameters">{{
          type === 'PING' ? 'Target' : timespan ? 'Timespan' : 'Regular expression'
        }}</label>
        <InputText id="sensor-parameters" v-model="parameters" class="mono" :invalid="!!errors.parameters" />
        <small v-if="errors.parameters" class="field-error">{{ errors.parameters }}</small>
        <small v-else class="field-hint">{{ parameterHint }}</small>
      </div>
      <div v-if="info?.usesInterval" class="field">
        <label for="sensor-interval">Interval (seconds)</label>
        <InputNumber
          v-model="interval"
          input-id="sensor-interval"
          :min="1"
          :use-grouping="false"
          :invalid="!!errors.interval"
        />
        <small v-if="errors.interval" class="field-error">{{ errors.interval }}</small>
        <small v-else class="field-hint">{{
          type === 'PING'
            ? 'How often PNMTD pings.'
            : isUdp
              ? 'How often the node reports: 600 is every 10 min.'
              : isMikrotik
                ? 'How often PNMTD polls the router: 300 is every 5 min, 60 at least.'
                : 'How often a report must arrive: 86400 is daily.'
        }}</small>
      </div>
      <div v-if="heartbeat" class="field">
        <label for="sensor-grace">Grace period (seconds)</label>
        <InputNumber
          v-model="grace"
          input-id="sensor-grace"
          :min="0"
          :use-grouping="false"
          :invalid="!!errors.gracePeriod"
        />
        <small v-if="errors.gracePeriod" class="field-error">{{ errors.gracePeriod }}</small>
        <small v-else class="field-hint">{{
          graceHint ??
          (isMikrotik
            ? 'How long polls may fail before the router counts as unreachable (440).'
            : 'How late a report may be before it counts as missing (460).')
        }}</small>
      </div>
      <UdpNodeFields v-if="isUdp" v-model="udp" :errors="errors" :listener-off="listenerOff" />
      <MikrotikFields
        v-if="isMikrotik"
        v-model="mikrotik"
        :errors="errors"
        :is-admin="session.can('Mikrotik', 'Manage')"
        :wire-guard="!!session.info?.wireGuard"
      />
      <ZabbixFields v-if="isZabbix" v-model="zabbix" :errors="errors" />
      <div class="field">
        <label for="sensor-textid">Text id <span class="muted">(optional)</span></label>
        <InputText id="sensor-textid" v-model="textId" class="mono" :invalid="!!errors.textId" />
        <small v-if="errors.textId" class="field-error">{{ errors.textId }}</small>
        <small v-else class="field-hint">A name of your own for scripts and notifications.</small>
      </div>
      <div class="field wide">
        <label for="sensor-notes">Notes <span class="muted">(optional)</span></label>
        <Textarea id="sensor-notes" v-model="notes" rows="3" auto-resize :invalid="!!errors.notes" />
        <small v-if="errors.notes" class="field-error">{{ errors.notes }}</small>
        <small v-else class="field-hint">{{
          isUdp ? 'Location, SIM or ICCID, wiring, the last battery change…' : 'Anything worth knowing about it.'
        }}</small>
      </div>
      <div class="switch-row wide">
        <ToggleSwitch v-model="enabled" input-id="sensor-enabled" />
        <label for="sensor-enabled">Enabled</label>
      </div>
      <Message v-if="type === 'ENCAPSULADED'" severity="info" :closable="false" class="wide">
        Its reports are a JSON array; each Name in it becomes a child sensor of its own.
      </Message>
      <div class="form-actions wide">
        <Button
          type="submit"
          :label="isUdp ? 'Create UDP node' : isMikrotik ? 'Add router' : 'Create sensor'"
          :loading="busy"
        />
        <Button label="Cancel" severity="secondary" text @click="router.back()" />
      </div>
    </form>
  </div>
</template>

<style scoped>
.form-box {
  max-width: 760px;
}

.type {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.mono :deep(input),
input.mono {
  font-family: var(--pnmt-mono);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
