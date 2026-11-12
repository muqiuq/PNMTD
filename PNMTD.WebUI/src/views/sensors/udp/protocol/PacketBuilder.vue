<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import PacketInspector from '@/components/PacketInspector.vue'
import { parseHexInput, toBase64, utf8Encode } from '@/format/bytes'
import {
  DOWNLINK_TYPES,
  suiteInfo,
  suiteName,
  typeName,
  UPLINK_TYPES,
  type PacketType,
  type SecuritySuite,
} from '@/format/udp'

/**
 * The packet builder (docs/udp-iot-plan.md §6.5): a type, one of the node's suites, a counter, ACK
 * and a payload (text or hex); the server builds the datagram with its own codec and the node's
 * key (the reference key for Viewers and without a node). It only builds, never sends, so a
 * firmware developer can compare it with what the device sends.
 */
const props = defineProps<{ sensorId?: string | null; examples: S['UdpProtocolExamples'] }>()

const type = ref<PacketType>('Heartbeat')
const suites = computed(() =>
  props.sensorId
    ? props.examples.allowedSuites
    : props.examples.suites
        .filter((s) => s.family === null || s.family === props.examples.keyFamily)
        .map((s) => s.suite),
)
const suite = ref<SecuritySuite>(props.examples.allowedSuites[0] ?? 'HmacSha256_32')
watch(suites, (list) => {
  if (!list.includes(suite.value) && list.length) suite.value = list[0]
})
const fcnt = ref<number | null>(1)
const ack = ref(false)
const mode = ref<'text' | 'hex'>('text')
const input = ref('')

const types = computed(() => [
  ...UPLINK_TYPES.map((t) => ({ label: `${typeName(t)} ↑`, value: t })),
  ...DOWNLINK_TYPES.map((t) => ({ label: `${typeName(t)} ↓`, value: t })),
])
const downlink = computed(() => DOWNLINK_TYPES.includes(type.value))
const payload = computed(() =>
  mode.value === 'text' ? { bytes: utf8Encode(input.value), error: null } : parseHexInput(input.value),
)
const examplePayloads: Partial<Record<PacketType, string>> = {
  Data: '21.5C',
  CmdResult: '01 00 4F 4B (CMD_ID 1, OK, "OK")',
  Boot: '01 31 2E 30 2E 30 (power on, "1.0.0")',
  Join: 'A1 B2 C3 D4 (DEV_NONCE)',
  Ota: '03 01 00 00 01 C0 (update 3, GET at 448)',
  Command: '01 02 02 58 (CMD_ID 1, then the command)',
  Event: '00 00 C8 6F 6B (channel 0, code 200, "ok")',
}
const placeholder = computed(() => examplePayloads[type.value] ?? 'empty')

// Debounced, so typing does not send a request per key.
const params = ref<Parameters<typeof api.udp.build>[0] | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined
watch(
  [type, suite, fcnt, ack, payload, () => props.sensorId],
  () => {
    clearTimeout(timer)
    const bytes = payload.value.bytes
    if (!bytes || fcnt.value === null) {
      params.value = null
      return
    }
    timer = setTimeout(() => {
      params.value = {
        sensorId: props.sensorId ?? undefined,
        type: type.value,
        suite: suite.value,
        fcnt: fcnt.value ?? 0,
        ack: ack.value && !downlink.value,
        payload: bytes.length ? toBase64(bytes) : undefined,
      }
    }, 300)
  },
  { immediate: true },
)
const built = useQuery({
  queryKey: computed(() => [...keys.udp, 'protocol', 'build', params.value]),
  queryFn: () => api.udp.build(params.value!),
  enabled: computed(() => params.value !== null),
  placeholderData: keepPreviousData,
  retry: false,
})
const error = computed(() => {
  const e = built.error.value
  if (!e) return null
  return e instanceof ApiError ? (Object.values(e.errors)[0]?.[0] ?? e.message) : String(e)
})
</script>

<template>
  <div class="builder">
    <div class="controls">
      <div class="field">
        <label for="build-type">Type</label>
        <Select v-model="type" input-id="build-type" :options="types" option-label="label" option-value="value" />
      </div>
      <div class="field">
        <label for="build-suite">Suite</label>
        <Select
          v-model="suite"
          input-id="build-suite"
          :options="
            suites.map((s) => ({ label: suiteName(s) + (suiteInfo(s)?.encrypted ? ' (encrypted)' : ''), value: s }))
          "
          option-label="label"
          option-value="value"
        />
      </div>
      <div class="field">
        <label for="build-fcnt">{{ downlink ? "Uplink's counter" : 'Counter (FCNT)' }}</label>
        <InputNumber v-model="fcnt" input-id="build-fcnt" :min="0" :max="4294967295" :use-grouping="false" />
      </div>
      <div class="field ack">
        <span class="label">ACK requested</span>
        <div class="switch-row">
          <ToggleSwitch v-model="ack" input-id="build-ack" :disabled="downlink" />
          <label for="build-ack">{{ ack && !downlink ? 'A = 1' : 'A = 0' }}</label>
        </div>
      </div>
      <div class="field wide">
        <div class="label-row">
          <label for="build-payload">Payload</label>
          <SelectButton
            v-model="mode"
            :options="[
              { label: 'Text', value: 'text' },
              { label: 'Hex', value: 'hex' },
            ]"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-label="Payload input"
          />
        </div>
        <InputText
          id="build-payload"
          v-model="input"
          class="mono"
          spellcheck="false"
          :placeholder="placeholder"
          :invalid="!!payload.error"
        />
        <small v-if="payload.error" class="field-error">{{ payload.error }}</small>
        <small v-else class="field-hint"
          >The payload as the type defines it (the placeholder shows an example). At most 480 bytes.</small
        >
      </div>
    </div>
    <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
    <template v-else-if="built.data.value">
      <p v-if="built.data.value.referenceKey" class="muted note">Built with the reference key 00 01 … 0F.</p>
      <PacketInspector
        title="Built"
        :datagram="built.data.value.datagram"
        :fields="built.data.value.fields"
        :payload="suiteInfo(suite)?.encrypted && payload.bytes ? payload.bytes : null"
        :encrypted="suiteInfo(suite)?.encrypted"
        :payload-label="suiteInfo(suite)?.encrypted ? 'Payload (before encryption)' : undefined"
      />
      <CopyField :value="built.data.value.send" label="pnmtd udp send" class="code" />
    </template>
  </div>
</template>

<style scoped>
.builder {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.controls {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--pnmt-space-3);
}

.controls .wide {
  grid-column: 1 / -1;
}

.label,
.label-row label {
  font-weight: 500;
}

.label-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 34px;
}

.mono {
  font-family: var(--pnmt-mono);
}

.note {
  margin: 0;
}

.code :deep(.value) {
  white-space: pre-wrap;
}

@media (max-width: 959px) {
  .controls {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 599px) {
  .controls {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
