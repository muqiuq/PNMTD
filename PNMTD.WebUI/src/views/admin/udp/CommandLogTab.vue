<script setup lang="ts">
import Column from 'primevue/column'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S, type UdpLogKind } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import HexDump from '@/components/HexDump.vue'
import ListTable from '@/components/ListTable.vue'
import PacketInspector from '@/components/PacketInspector.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { asciiPreview, fromBase64, hexPreview } from '@/format/bytes'
import { kindLabel, kindSeverity, LOG_KINDS, resultStatusText, suiteInfo, suiteName, toggleRow } from '@/format/udp'

/**
 * The permanent command log of every node (docs/udp-iot-plan.md §6.6), deleted nodes too (by the
 * name and device id they had): what users queued and cancelled, every command sent and resent,
 * every answer, and the firmware milestones. Newest first; a row opens its packet.
 */
const kind = ref<UdpLogKind | ''>('')
const deviceId = ref<number | null>(null)
const range = ref('all')
const ranges = [
  { label: '24 h', value: '24h', ms: 86_400_000 },
  { label: '7 d', value: '7d', ms: 7 * 86_400_000 },
  { label: '30 d', value: '30d', ms: 30 * 86_400_000 },
  { label: 'All', value: 'all', ms: 0 },
]
const kinds = [{ label: 'All kinds', value: '' }, ...LOG_KINDS.map((k) => ({ label: kindLabel(k), value: k }))]
const filters = computed(() => ({
  kind: kind.value || undefined,
  deviceId: deviceId.value ?? undefined,
  range: range.value,
}))
const active = computed(() => (kind.value ? 1 : 0) + (deviceId.value ? 1 : 0) + (range.value !== 'all' ? 1 : 0))

const table = useServerTable<S['UdpLogItem']>({
  key: [...keys.udp, 'command-log'],
  fetch: ({ range: r, ...q }) => {
    const ms = ranges.find((x) => x.value === r)?.ms ?? 0
    return api.udp.log({
      ...(q as Parameters<typeof api.udp.log>[0]),
      from: ms ? new Date(Date.now() - ms).toISOString() : undefined,
    })
  },
  filters,
  refetchInterval: 15_000,
})
const expanded = ref<Record<string, boolean>>({})

const bytesOf = (l: S['UdpLogItem']) => fromBase64(l.payload ?? l.datagram)
const encrypted = (l: S['UdpLogItem']) => !!suiteInfo(l.suite)?.encrypted
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Node name or note" :active="active">
      <SelectButton
        v-model="range"
        :options="ranges"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Time range"
      />
      <Select
        v-model="kind"
        :options="kinds"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Kind"
        placeholder="All kinds"
      />
      <InputNumber
        v-model="deviceId"
        :min="1"
        :use-grouping="false"
        size="small"
        placeholder="Device id"
        aria-label="Device id"
        class="device"
      />
    </FilterBar>
    <ListTable
      v-model:expanded="expanded"
      :table="table"
      what="log entries"
      empty-text="Nothing logged in this range."
      selectable
      @select="(l) => (expanded = toggleRow(expanded, l.id))"
    >
      <Column expander style="width: 2.5rem" />
      <Column header="Time" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.created" /></template>
      </Column>
      <Column header="Node">
        <template #body="{ data }">
          <RouterLink v-if="data.sensorId" :to="{ name: 'sensor', params: { id: data.sensorId } }">{{
            data.nodeName
          }}</RouterLink>
          <span v-else class="muted" title="The node was deleted">{{ data.nodeName }} (deleted)</span>
          <span class="muted small"> · {{ data.deviceId }}</span>
        </template>
      </Column>
      <Column header="Kind" style="width: 10rem">
        <template #body="{ data }"><Tag :value="kindLabel(data.kind)" :severity="kindSeverity(data.kind)" /></template>
      </Column>
      <Column header="CMD_ID" style="width: 5rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.cmdId ?? '–' }}</span></template
        >
      </Column>
      <Column header="Attempt" style="width: 5rem">
        <template #body="{ data }">{{ data.attempt ?? '–' }}</template>
      </Column>
      <Column header="Dir" style="width: 3.5rem">
        <template #body="{ data }">
          <span
            :title="data.direction === 'Up' ? 'from the device' : data.direction === 'Down' ? 'to the device' : ''"
            >{{ data.direction === 'Up' ? '↑' : data.direction === 'Down' ? '↓' : '–' }}</span
          >
        </template>
      </Column>
      <Column header="Suite" style="width: 9rem">
        <template #body="{ data }">{{ data.suite ? suiteName(data.suite) : '–' }}</template>
      </Column>
      <Column header="Preview">
        <template #body="{ data }">
          <span v-if="data.payload || data.datagram" class="bytes-cell">
            <code class="ellipsis">{{ hexPreview(bytesOf(data), 8) }}</code>
            <code class="ellipsis muted">{{ asciiPreview(bytesOf(data), 16) }}</code>
          </span>
          <span v-else class="muted ellipsis note" :title="data.note ?? ''">{{ data.note ?? '–' }}</span>
        </template>
      </Column>
      <template #expansion="{ data }">
        <div class="detail">
          <dl class="facts small">
            <dt>Time</dt>
            <dd>{{ formatAbsolute(data.created) }}</dd>
            <template v-if="data.userName">
              <dt>By</dt>
              <dd>{{ data.userName }}</dd>
            </template>
            <template v-if="data.endpoint">
              <dt>{{ data.direction === 'Up' ? 'From' : 'To' }}</dt>
              <dd class="mono">{{ data.endpoint }}</dd>
            </template>
            <template v-if="data.fcnt !== null">
              <dt>FCNT</dt>
              <dd class="mono">{{ data.fcnt }}</dd>
            </template>
            <template v-if="data.resultStatus !== null">
              <dt>Status</dt>
              <dd :class="{ bad: data.resultStatus !== 0 }">
                {{ data.resultStatus }} {{ resultStatusText(data.resultStatus) }}
              </dd>
            </template>
            <template v-if="data.note">
              <dt>Note</dt>
              <dd>{{ data.note }}</dd>
            </template>
          </dl>
          <PacketInspector
            v-if="data.datagram"
            :datagram="data.datagram"
            :fields="data.fields"
            :payload="encrypted(data) ? data.payload : null"
            :encrypted="encrypted(data)"
          />
          <div v-else-if="data.payload">
            <div class="sub">Command bytes</div>
            <HexDump :bytes="fromBase64(data.payload)" label="Command bytes" />
          </div>
        </div>
      </template>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="kindLabel(item.kind)" :severity="kindSeverity(item.kind)" />
          <span class="ellipsis">{{ item.nodeName }}</span>
          <span v-if="item.cmdId !== null" class="mono muted">#{{ item.cmdId }}</span>
          <RelativeTime :value="item.created" class="muted right" />
        </div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.device {
  width: 8rem;
}

.device :deep(input) {
  width: 100%;
}

.small {
  font-size: 0.857rem;
}

.bytes-cell {
  display: flex;
  flex-direction: column;
  min-width: 0;
  max-width: 18rem;
}

.note {
  display: block;
  max-width: 18rem;
}

.detail {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-2) 0;
  max-width: 60rem;
}

.facts.small {
  gap: var(--pnmt-space-1) var(--pnmt-space-4);
}

.sub {
  font-weight: 500;
  margin-bottom: var(--pnmt-space-1);
}

.bad {
  color: var(--pnmt-fail);
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
