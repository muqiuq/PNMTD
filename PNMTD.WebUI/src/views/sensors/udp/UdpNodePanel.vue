<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tab from 'primevue/tab'
import TabList from 'primevue/tablist'
import TabPanel from 'primevue/tabpanel'
import TabPanels from 'primevue/tabpanels'
import Tabs from 'primevue/tabs'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { useCopy } from '@/composables/useCopy'
import { parseHexInput, toCArray, toHex } from '@/format/bytes'
import {
  ackLabel,
  endpointParts,
  familyLabel,
  idLength,
  isWeak,
  secretSource,
  suiteInfo,
  suiteName,
  type SecuritySuite,
} from '@/format/udp'

/**
 * How a device reports to a UDP node (docs/udp-iot-plan.md §6.2), in place of the sensor URLs: the
 * endpoint, the device id with its header bytes, the key and the token (hidden until revealed,
 * User and Admin only) with where they came from, and ready-made packets built by the server's
 * codec for this node: a heartbeat with counter 1, the xxd | nc line, the pnmtd udp send line.
 * Its settings in one line; "Edit settings…" opens all of them (UdpSettingsDialog).
 */
const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean }>()
const emit = defineEmits<{ edit: []; manual: []; regenerate: [] }>()

const copy = useCopy()
const udp = computed(() => props.sensor.udp!)
const secrets = computed(() => props.sensor.integration?.udp ?? null)
const revealed = ref(false)
const where = computed(() => endpointParts(udp.value.endpoint))

const spaced = (hex: string) => (parseHexInput(hex).bytes ? toHex(parseHexInput(hex).bytes!) : hex)
const keyBytes = computed(() => (secrets.value ? (parseHexInput(secrets.value.key).bytes ?? null) : null))
const tokenBytes = computed(() => (secrets.value ? (parseHexInput(secrets.value.token).bytes ?? null) : null))

// The example's suite: the first signature the node allows (not encryption: the bytes would say little).
const mainSuite = computed<SecuritySuite>(() => {
  const allowed = udp.value.allowedSuites
  return (
    allowed.find((s) => suiteInfo(s)?.keyed && !suiteInfo(s)?.encrypted) ??
    allowed.find((s) => !isWeak(s)) ??
    allowed[0]
  )
})
const weakSuite = computed<SecuritySuite | null>(() =>
  udp.value.allowedSuites.includes('Token32') ? 'Token32' : udp.value.allowedSuites.includes('None') ? 'None' : null,
)

const heartbeat = (suite: () => SecuritySuite | null) =>
  useQuery({
    queryKey: computed(() => [...keys.udpNode(props.sensor.id), 'example', suite(), udp.value.deviceId]),
    queryFn: () =>
      api.udp.build({ sensorId: props.sensor.id, type: 'Heartbeat', suite: suite()!, fcnt: 1, ack: false }),
    enabled: computed(() => suite() !== null),
    staleTime: 60_000,
  })
const main = heartbeat(() => mainSuite.value)
const weak = heartbeat(() => weakSuite.value)

const settings = computed(() => [
  udp.value.allowedSuites.map(suiteName).join(', '),
  `responses ${udp.value.responseSuite ? `in ${suiteName(udp.value.responseSuite)}` : 'as received'}`,
  `ACK ${ackLabel(udp.value.ackMode)}`,
  udp.value.sessionMode === 'JoinRequired' ? 'JOIN required' : 'no JOIN',
  `replay protection ${udp.value.replayProtection ? 'on' : 'off'}`,
])

const shell = (hex: string) => `echo ${hex} | xxd -r -p | nc -u -w1 ${where.value.host} ${where.value.port}`
</script>

<template>
  <div class="udp-panel">
    <dl class="facts">
      <dt>Endpoint</dt>
      <dd><CopyField :value="udp.endpoint" :display="`${udp.endpoint}/udp`" label="UDP endpoint" /></dd>
      <dt>Device id</dt>
      <dd>
        <span class="strong">{{ udp.deviceId }}</span>
        <span class="muted">
          · header bytes <code>{{ udp.deviceIdBytes }}</code> (IDL {{ idLength(udp.deviceId) }} byte{{
            idLength(udp.deviceId) === 1 ? '' : 's'
          }})</span
        >
      </dd>
      <dt>Key</dt>
      <dd>
        <div class="secret">
          <code v-if="secrets" class="value" :class="{ hidden: !revealed }">{{
            revealed ? spaced(secrets.key) : '•• •• •• •• •• •• •• •• •• •• •• •• •• •• •• ••'
          }}</code>
          <span v-else class="muted">Shown to User and Admin only.</span>
          <template v-if="secrets && keyBytes">
            <Button
              :icon="revealed ? 'pi pi-eye-slash' : 'pi pi-eye'"
              size="small"
              text
              severity="secondary"
              :aria-label="revealed ? 'Hide the key and token' : 'Reveal the key and token'"
              @click="revealed = !revealed"
            />
            <Button
              v-tooltip.top="'Copy as hex'"
              icon="pi pi-copy"
              size="small"
              text
              severity="secondary"
              aria-label="Copy the key as hex"
              @click="copy(toHex(keyBytes, ''), 'Key (hex)')"
            />
            <Button
              v-tooltip.top="'Copy as a C array'"
              label="C"
              size="small"
              text
              severity="secondary"
              aria-label="Copy the key as a C array"
              @click="copy(toCArray(keyBytes), 'Key (C array)')"
            />
          </template>
        </div>
        <small class="field-hint"
          >{{ familyLabel(udp.keyFamily) }} ·
          {{ secretSource(udp.keySource, udp.keyChangedAt, udp.keyChangedBy) }}</small
        >
      </dd>
      <dt>Token</dt>
      <dd>
        <div class="secret">
          <code v-if="secrets" class="value">{{ revealed ? spaced(secrets.token) : '•• •• •• ••' }}</code>
          <span v-else class="muted">Shown to User and Admin only.</span>
          <template v-if="secrets && tokenBytes">
            <Button
              v-tooltip.top="'Copy as hex'"
              icon="pi pi-copy"
              size="small"
              text
              severity="secondary"
              aria-label="Copy the token as hex"
              @click="copy(toHex(tokenBytes, ''), 'Token (hex)')"
            />
            <Button
              v-tooltip.top="'Copy as a C array'"
              label="C"
              size="small"
              text
              severity="secondary"
              aria-label="Copy the token as a C array"
              @click="copy(toCArray(tokenBytes), 'Token (C array)')"
            />
          </template>
        </div>
        <small class="field-hint">{{ secretSource(udp.tokenSource, udp.tokenChangedAt, udp.tokenChangedBy) }}</small>
      </dd>
      <dt>Settings</dt>
      <dd class="settings">{{ settings.join(' · ') }}</dd>
    </dl>
    <div v-if="canWrite" class="actions">
      <Button label="Edit settings…" icon="pi pi-sliders-h" size="small" @click="emit('edit')" />
      <Button
        label="Set manually…"
        icon="pi pi-pencil"
        size="small"
        severity="secondary"
        outlined
        @click="emit('manual')"
      />
      <Button
        label="Regenerate…"
        icon="pi pi-refresh"
        size="small"
        severity="secondary"
        outlined
        @click="emit('regenerate')"
      />
    </div>

    <Tabs value="bytes" class="examples">
      <TabList>
        <Tab value="bytes">Heartbeat</Tab>
        <Tab value="shell">Shell</Tab>
        <Tab value="cli">pnmtd udp send</Tab>
      </TabList>
      <TabPanels>
        <TabPanel value="bytes">
          <template v-if="main.data.value">
            <p class="note muted">
              A heartbeat with counter 1 in {{ suiteName(mainSuite) }}, built by PNMTD's codec<template
                v-if="main.data.value.referenceKey"
              >
                with the reference key (you do not see this node's key)</template
              >. The next one needs counter 2: a keyed suite never reuses a counter.
            </p>
            <CopyField :value="main.data.value.hex" label="Heartbeat bytes" />
            <CopyField :value="main.data.value.cArray" label="Heartbeat as a C array" class="code" />
          </template>
          <p v-else-if="main.isError.value" class="note muted">The example could not be built.</p>
        </TabPanel>
        <TabPanel value="shell">
          <template v-if="weakSuite && weak.data.value">
            <p class="note muted">
              {{ suiteName(weakSuite) }} needs no crypto. The bytes are the same every time, which is fine: the counter
              is not enforced for {{ suiteName(weakSuite) }}, and sends less than 5 min apart count as retransmissions.
            </p>
            <CopyField :value="shell(weak.data.value.hex)" label="Shell example" class="code" />
          </template>
          <template v-else-if="main.data.value">
            <p class="note muted">
              {{ suiteName(mainSuite) }} with counter 1: it is accepted once. Every further packet needs a higher
              counter and a new tag, so a script computes it (Protocol tab), or uses <code>pnmtd udp send</code>.
            </p>
            <CopyField :value="shell(main.data.value.hex)" label="Shell example" class="code" />
          </template>
        </TabPanel>
        <TabPanel value="cli">
          <p class="note muted">
            Sends one packet and prints what went out and what came back, decoded. Add
            <code>--counter-file /tmp/node{{ udp.deviceId }}.fcnt</code> to count up by itself.
          </p>
          <CopyField v-if="main.data.value" :value="main.data.value.send" label="pnmtd udp send" class="code" />
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>

<style scoped>
.udp-panel {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.facts {
  align-items: start;
}

.facts > dt {
  padding-top: 4px;
}

.strong {
  font-weight: 600;
}

.settings {
  padding-top: 4px;
}

.secret {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  flex-wrap: wrap;
  min-height: 30px;
}

.secret .value {
  overflow-wrap: anywhere;
}

.secret .hidden {
  color: var(--pnmt-muted);
  letter-spacing: 0.02em;
}

.actions {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.examples :deep(.p-tabpanels) {
  padding: var(--pnmt-space-2) 0 0;
}

.examples :deep(.p-tab) {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.examples :deep(.p-tabpanel) {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.note {
  margin: 0;
  font-size: 0.929rem;
}

.code :deep(.value) {
  white-space: pre-wrap;
}
</style>
