<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import ProgressBar from 'primevue/progressbar'
import Select from 'primevue/select'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, sensorRequest, type KeyFamily, type S, type SensorState } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatPercent, formatRelative } from '@/format'
import {
  ackLabel,
  FAMILIES,
  familyLabel,
  isWeak,
  percent,
  secretSource,
  suiteInfo,
  suiteName,
  words,
} from '@/format/udp'
import OtaDialog from '@/views/sensors/udp/OtaDialog.vue'
import SecretsDialog from '@/views/sensors/udp/SecretsDialog.vue'
import { useNodeActions } from '@/views/sensors/udp/useNodeActions'

/**
 * Every UDP node defined (docs/udp-iot-plan.md §6.6), wherever its host is, disabled ones too:
 * settings, key source, session and ACK, last seen, counter, open commands, firmware and update,
 * the last 24 hours, notes. Searched (name, host, device id, notes), filtered and sorted by the
 * server. Row actions as on the sensor page; several rows: "Update firmware…".
 */
type Row = S['UdpNodeListItem'] & { id: string }

const router = useRouter()
const now = useNow()
const { run } = useActions()
const nodeActions = useNodeActions()

const state = ref<SensorState | ''>('')
const family = ref<KeyFamily | ''>('')
const enabled = ref<'' | 'true' | 'false'>('')
const firmware = ref('')
const states: { label: string; value: SensorState | '' }[] = [
  { label: 'Any state', value: '' },
  { label: 'OK', value: 'Ok' },
  { label: 'Failing', value: 'Failing' },
  { label: 'No data', value: 'NoData' },
  { label: 'Ignored', value: 'Ignored' },
  { label: 'Disabled', value: 'Disabled' },
]
const families = [{ label: 'Any family', value: '' }, ...FAMILIES.map((f) => ({ label: f.label, value: f.value }))]
const enabledOptions = [
  { label: 'Enabled or not', value: '' },
  { label: 'Enabled', value: 'true' },
  { label: 'Disabled', value: 'false' },
]
const filters = computed(() => ({
  state: state.value || undefined,
  keyFamily: family.value || undefined,
  enabled: enabled.value === '' ? undefined : enabled.value === 'true',
  firmwareVersion: firmware.value.trim() || undefined,
}))
const active = computed(() => [state.value, family.value, enabled.value, firmware.value.trim()].filter(Boolean).length)

const table = useServerTable<Row>({
  key: [...keys.udp, 'nodes'],
  fetch: async (q) => {
    const page = await api.udp.nodes(q as Parameters<typeof api.udp.nodes>[0])
    return { ...page, items: page.items.map((n) => ({ ...n, id: n.sensorId })) }
  },
  defaultSort: 'name',
  filters,
  refetchInterval: 15_000,
})

// --- selection (several nodes: "Update firmware…") ------------------------------------------
const selected = ref<Map<string, string>>(new Map())
const allOnPage = computed(
  () => table.items.value.length > 0 && table.items.value.every((n) => selected.value.has(n.sensorId)),
)
function select(n: Row, on: boolean) {
  const next = new Map(selected.value)
  if (on) next.set(n.sensorId, n.name)
  else next.delete(n.sensorId)
  selected.value = next
}
function selectPage(on: boolean) {
  const next = new Map(selected.value)
  for (const n of table.items.value) {
    if (on) next.set(n.sensorId, n.name)
    else next.delete(n.sensorId)
  }
  selected.value = next
}

// --- dialogs ----------------------------------------------------------------------------------
const secrets = ref<{ id: string; name: string; mode: 'manual' | 'regenerate' } | null>(null)
const secretsOpen = computed({ get: () => secrets.value !== null, set: (v) => !v && (secrets.value = null) })
const ota = ref<{ ids: string[]; names: string[] } | null>(null)
const otaOpen = computed({ get: () => ota.value !== null, set: (v) => !v && (ota.value = null) })

async function toggleEnabled(n: Row) {
  await run(
    async () => {
      const detail = await api.sensors.get(n.sensorId)
      await api.sensors.update(n.sensorId, sensorRequest(detail, { enabled: !detail.enabled }))
    },
    {
      success: `${n.name} ${n.enabled ? 'disabled' : 'enabled'}`,
      invalidate: [keys.udp, keys.sensor(n.sensorId), keys.host(n.host.id), keys.hosts],
    },
  )
}

function actions(n: Row): MenuItem[] {
  return [
    {
      label: 'Open',
      icon: 'pi pi-arrow-right',
      command: () => router.push({ name: 'sensor', params: { id: n.sensorId } }),
    },
    {
      label: 'Edit settings',
      icon: 'pi pi-pencil',
      command: () => router.push({ name: 'sensor', params: { id: n.sensorId } }),
    },
    {
      label: 'Set key or token…',
      icon: 'pi pi-key',
      command: () => (secrets.value = { id: n.sensorId, name: n.name, mode: 'manual' }),
    },
    {
      label: 'Regenerate key or token…',
      icon: 'pi pi-refresh',
      command: () => (secrets.value = { id: n.sensorId, name: n.name, mode: 'regenerate' }),
    },
    { label: 'Reset counter…', icon: 'pi pi-undo', command: () => nodeActions.resetCounter(n.sensorId, n.name) },
    { label: 'Reset session…', icon: 'pi pi-sign-out', command: () => nodeActions.resetSession(n.sensorId, n.name) },
    { label: n.enabled ? 'Disable' : 'Enable', icon: 'pi pi-power-off', command: () => toggleEnabled(n) },
    {
      label: 'Update firmware…',
      icon: 'pi pi-upload',
      disabled: !!n.openUpdate,
      command: () => (ota.value = { ids: [n.sensorId], names: [n.name] }),
    },
  ]
}

function updateSelected() {
  ota.value = { ids: [...selected.value.keys()], names: [...selected.value.values()] }
}

const lastSeenTitle = (n: Row) =>
  n.lastSeenAt ? [formatAbsolute(n.lastSeenAt), n.lastEndpoint, suiteName(n.lastSuite)].filter(Boolean).join(' · ') : ''
const firstLine = (text: string | null) => (text ?? '').split('\n')[0]
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Name, host, device id, notes" :active="active">
      <Select
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="State"
        placeholder="Any state"
      />
      <Select
        v-model="family"
        :options="families"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Key family"
        placeholder="Any family"
      />
      <Select
        v-model="enabled"
        :options="enabledOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Enabled"
        placeholder="Enabled or not"
      />
      <InputText
        v-model="firmware"
        size="small"
        placeholder="Firmware version"
        aria-label="Firmware version"
        class="fw"
      />
      <template #end>
        <Button
          v-if="selected.size"
          :label="`Update firmware… (${selected.size})`"
          icon="pi pi-upload"
          size="small"
          @click="updateSelected"
        />
        <Button
          v-if="selected.size"
          icon="pi pi-times"
          size="small"
          text
          severity="secondary"
          aria-label="Clear the selection"
          @click="selected = new Map()"
        />
      </template>
    </FilterBar>

    <ListTable
      :table="table"
      what="UDP nodes"
      empty-text="No UDP node matches."
      :row-class="(n) => (n.enabled ? undefined : 'row-muted')"
    >
      <Column style="width: 2.5rem">
        <template #header>
          <Checkbox
            :model-value="allOnPage"
            binary
            aria-label="Select the nodes on this page"
            @update:model-value="selectPage"
          />
        </template>
        <template #body="{ data }">
          <Checkbox
            :model-value="selected.has(data.sensorId)"
            binary
            :aria-label="`Select ${data.name}`"
            @update:model-value="(on: boolean) => select(data, on)"
          />
        </template>
      </Column>
      <Column field="name" header="Name" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }" class="strong">{{
            data.name
          }}</RouterLink>
        </template>
      </Column>
      <Column field="host" header="Host" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'host', params: { id: data.host.id } }" class="host">{{
            data.host.name
          }}</RouterLink>
        </template>
      </Column>
      <Column field="deviceId" header="Id" sortable style="width: 6rem">
        <template #body="{ data }">
          <span class="nowrap"
            >{{ data.deviceId }} <span class="muted mono small">· {{ data.deviceIdBytes }}</span></span
          >
        </template>
      </Column>
      <Column field="state" header="State" sortable style="width: 7rem">
        <template #body="{ data }">
          <StatusBadge :status="data.state" />
          <i
            v-if="data.badTagWarning"
            class="pi pi-exclamation-triangle warn"
            title="Many packets with a bad tag recently"
          />
        </template>
      </Column>
      <Column header="On" style="width: 3.5rem">
        <template #body="{ data }">{{ data.enabled ? 'Yes' : 'No' }}</template>
      </Column>
      <Column header="Key">
        <template #body="{ data }">
          <div :title="data.keyChangedBy ? `by ${data.keyChangedBy}` : undefined">
            <div>{{ familyLabel(data.keyFamily) }}</div>
            <div class="muted small nowrap">{{ secretSource(data.keySource, data.keyChangedAt, null) }}</div>
          </div>
        </template>
      </Column>
      <Column header="Suites">
        <template #body="{ data }">
          <span class="suites">
            <span
              v-for="s in data.allowedSuites"
              :key="s"
              class="suite"
              :class="{ weak: isWeak(s), legacy: suiteInfo(s)?.legacy, encrypted: suiteInfo(s)?.encrypted }"
              :title="suiteName(s)"
              >{{ suiteInfo(s)?.short ?? s }}</span
            >
          </span>
        </template>
      </Column>
      <Column header="Session / ACK" style="width: 7rem">
        <template #body="{ data }">
          <span class="nowrap"
            >{{ data.sessionMode === 'JoinRequired' ? (data.sessionActive ? 'JOIN ✓' : 'JOIN') : '–' }} ·
            {{ ackLabel(data.ackMode) }}</span
          >
        </template>
      </Column>
      <Column field="lastSeen" header="Last seen" sortable style="width: 7rem">
        <template #body="{ data }">
          <span :title="lastSeenTitle(data)" class="nowrap" :class="{ muted: !data.lastSeenAt }">{{
            data.lastSeenAt ? formatRelative(data.lastSeenAt, now) : 'never'
          }}</span>
        </template>
      </Column>
      <Column header="FCNT" style="width: 5rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.fcnt ?? '–' }}</span></template
        >
      </Column>
      <Column header="Cmds" style="width: 4rem">
        <template #body="{ data }">
          <span :class="{ muted: !data.openCommands }" title="Open commands">{{ data.openCommands }}</span>
        </template>
      </Column>
      <Column header="Firmware" style="width: 8rem">
        <template #body="{ data }">
          <span class="mono">{{ data.firmwareVersion ?? '–' }}</span>
          <div
            v-if="data.openUpdate"
            :title="`${words(data.openUpdate.state)}: ${data.openUpdate.firmwareName} ${data.openUpdate.firmwareVersion}`"
          >
            <ProgressBar :value="percent(data.openUpdate.progress)" :show-value="false" class="bar" />
          </div>
        </template>
      </Column>
      <Column header="24 h" style="width: 9rem">
        <template #body="{ data }">
          <span class="nowrap small" title="Packets · loss · rejected"
            >{{ data.packets24Hours.toLocaleString() }} · {{ formatPercent(data.loss24Hours, 1) }} ·
            <span :class="{ bad: data.rejected24Hours > 0 }">{{ data.rejected24Hours }}</span></span
          >
        </template>
      </Column>
      <Column header="Notes">
        <template #body="{ data }">
          <span class="ellipsis notes muted" :title="data.notes ?? ''">{{ firstLine(data.notes) }}</span>
        </template>
      </Column>
      <Column header="" style="width: 3rem">
        <template #body="{ data }">
          <MoreMenu :items="actions(data)" :label="`Actions for ${data.name}`" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Checkbox
            :model-value="selected.has(item.sensorId)"
            binary
            :aria-label="`Select ${item.name}`"
            @update:model-value="(on: boolean) => select(item, on)"
          />
          <StatusBadge :status="item.state" icon-only />
          <RouterLink :to="{ name: 'sensor', params: { id: item.sensorId } }" class="strong">{{
            item.name
          }}</RouterLink>
          <span class="muted small">{{ item.deviceId }}</span>
          <span class="muted right small">{{ item.lastSeenAt ? formatRelative(item.lastSeenAt, now) : 'never' }}</span>
          <MoreMenu :items="actions(item)" :label="`Actions for ${item.name}`" />
        </div>
        <div class="muted small ellipsis">
          {{ item.host.name }} · {{ familyLabel(item.keyFamily) }} · {{ item.firmwareVersion ?? 'no version' }}
        </div>
      </template>
    </ListTable>

    <SecretsDialog
      v-if="secrets"
      v-model:visible="secretsOpen"
      :sensor-id="secrets.id"
      :name="secrets.name"
      :mode="secrets.mode"
    />
    <OtaDialog v-model:visible="otaOpen" :sensor-ids="ota?.ids ?? []" :names="ota?.names ?? []" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.fw {
  width: 9rem;
}

.host {
  color: var(--pnmt-text);
}

.strong {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.warn {
  color: var(--pnmt-warn);
  margin-left: var(--pnmt-space-1);
}

.bad {
  color: var(--pnmt-fail);
}

.suites {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 2px;
}

.suite {
  font-family: var(--pnmt-mono);
  font-size: 0.75rem;
  padding: 0 0.3rem;
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
}

.suite.weak {
  background: var(--pnmt-warn-soft);
  color: var(--pnmt-warn);
}

.suite.legacy {
  font-style: italic;
}

.suite.encrypted {
  background: var(--pnmt-ok-soft);
  color: var(--pnmt-ok);
}

.bar {
  height: 5px;
  margin-top: 3px;
}

.notes {
  display: block;
  max-width: 12rem;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.right {
  margin-left: auto;
  white-space: nowrap;
}
</style>
