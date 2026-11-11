<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type SensorType } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'
import { typeIcon, typeLabel, useSensorTypes } from '@/composables/useLookups'

/**
 * A new sensor on a host (§5.6): the type decides which fields there are, each with its help. The
 * server validates the parameters (a regex, a ping target, a timespan such as "daily 8"). After the
 * creation the sensor's page shows its URLs.
 */
const route = useRoute()
const router = useRouter()
const types = useSensorTypes()
const { invalidate, notify } = useActions()

const hostId = computed(() => String(route.params.id))
const host = useQuery({ queryKey: computed(() => keys.host(hostId.value)), queryFn: () => api.hosts.get(hostId.value) })

const type = ref<SensorType>('SIMPLE')
const name = ref('')
const interval = ref<number | null>(null)
const grace = ref<number | null>(null)
const parameters = ref('')
const enabled = ref(true)
const textId = ref('')
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

const creatable = computed(() => types.list.value.filter((t) => t.canCreate))
const info = computed(() => types.info(type.value))
const heartbeat = computed(() => type.value === 'HEARTBEAT' || type.value === 'HEARTBEAT_VALUECHECK')
const timespan = computed(() => type.value === 'ONE_WITHIN_TIMESPAN' || type.value === 'ALL_WITHIN_TIMESPAN')

// Sensible defaults per type: a ping every minute, a daily heartbeat with an hour of grace.
watch(type, (t) => {
  interval.value = t === 'PING' ? 60 : heartbeat.value ? 86_400 : null
  grace.value = heartbeat.value ? 3_600 : null
  errors.value = {}
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

async function submit() {
  errors.value = {
    name: name.value.trim() ? undefined : 'A sensor needs a name.',
    parameters: info.value?.parameters && !parameters.value.trim() ? 'This type needs it.' : undefined,
    interval: info.value?.usesInterval && !interval.value ? 'In seconds, more than 0.' : undefined,
  }
  if (Object.values(errors.value).some((e) => e)) {
    return
  }
  busy.value = true
  try {
    const sensor = await api.sensors.create({
      hostId: hostId.value,
      type: type.value,
      name: name.value.trim(),
      enabled: enabled.value,
      ignore: false,
      interval: interval.value ?? 0,
      gracePeriod: grace.value ?? 0,
      parameters: parameters.value.trim() || null,
      textId: textId.value.trim() || null,
    })
    await invalidate([keys.host(hostId.value), keys.hosts])
    notify.success(`Sensor ${sensor.name} created`, 'Its URLs are on its page.')
    await router.replace({ name: 'sensor', params: { id: sensor.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        name: e.field('name'),
        parameters: e.field('parameters'),
        interval: e.field('interval'),
        gracePeriod: e.field('gracePeriod'),
        type: e.field('type'),
        textId: e.field('textId'),
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
      title="New sensor"
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
          type === 'PING' ? 'How often PNMTD pings.' : 'How often a report must arrive: 86400 is daily.'
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
        <small v-else class="field-hint">How late a report may be before it counts as missing (460).</small>
      </div>
      <div class="field">
        <label for="sensor-textid">Text id <span class="muted">(optional)</span></label>
        <InputText id="sensor-textid" v-model="textId" class="mono" :invalid="!!errors.textId" />
        <small v-if="errors.textId" class="field-error">{{ errors.textId }}</small>
        <small v-else class="field-hint">A name of your own for scripts and notifications.</small>
      </div>
      <div class="switch-row wide">
        <ToggleSwitch v-model="enabled" input-id="sensor-enabled" />
        <label for="sensor-enabled">Enabled</label>
      </div>
      <Message v-if="type === 'ENCAPSULADED'" severity="info" :closable="false" class="wide">
        Its reports are a JSON array; each Name in it becomes a child sensor of its own.
      </Message>
      <div class="form-actions wide">
        <Button type="submit" label="Create sensor" :loading="busy" />
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
