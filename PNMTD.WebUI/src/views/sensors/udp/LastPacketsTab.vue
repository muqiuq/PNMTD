<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PacketInspector from '@/components/PacketInspector.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useMedia } from '@/composables/useMedia'
import { formatAbsolute } from '@/format'
import { asciiPreview, fromBase64 } from '@/format/bytes'
import { suiteInfo, suiteName, toggleRow, typeName, words } from '@/format/udp'

/**
 * The last packet of each type and the recent rejections (docs/udp-iot-plan.md §6.3). There is no
 * packet history: heartbeats and data are sensor events, commands are in the command log. A row
 * opens the packet inspector with the answer PNMTD sent. Refreshes every 5 s while it is shown.
 */
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const phone = useMedia('(max-width: 599px)')

const packets = useQuery({
  queryKey: computed(() => [...keys.udpNode(props.sensor.id), 'last-packets']),
  queryFn: () => api.udp.lastPackets(props.sensor.id),
  refetchInterval: 5_000,
})
const rejections = useQuery({
  queryKey: computed(() => [...keys.udpNode(props.sensor.id), 'rejections']),
  queryFn: () => api.udp.rejections(props.sensor.id),
  refetchInterval: 5_000,
})

const preview = (p: { payload?: string | null }) => asciiPreview(fromBase64(p.payload), 24)
const size = (b64: string | null) => fromBase64(b64).length
/** The answer's type, as the server's codec names it in the first field ("version 1, ACK (0x8), 1-byte id"). */
function answer(p: S['UdpPacketView']): string | null {
  if (!p.response) return null
  const first = p.responseFields?.[0]?.value ?? ''
  return first.split(', ')[1]?.split(' (')[0] ?? 'sent'
}

const rows = computed(() => (rejections.data.value ?? []).map((r, i) => ({ ...r, key: `${r.at}-${i}` })))
const openPackets = ref<Record<string, boolean>>({})
const openRejections = ref<Record<string, boolean>>({})
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="last-title">
      <div id="last-title" class="panel-header">
        Last packet of each type <span class="muted refresh">refreshes every 5 s</span>
      </div>
      <ErrorState
        v-if="packets.isError.value && !packets.data.value"
        :error="packets.error.value"
        what="the last packets"
        @retry="packets.refetch()"
      />
      <div v-else-if="!packets.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
      <EmptyState
        v-else-if="!packets.data.value.length"
        icon="pi pi-wifi"
        title="No packet yet."
        text="The node's first accepted packet shows here, with its bytes."
      />
      <ul v-else-if="phone" class="mobile-list">
        <li v-for="p in packets.data.value" :key="p.type">
          <button type="button" class="row" @click="openPackets = toggleRow(openPackets, p.type)">
            <strong>{{ typeName(p.type) }}</strong>
            <span class="muted mono">#{{ p.fcnt }}</span>
            <span class="ellipsis mono">{{ preview(p) }}</span>
            <RelativeTime :value="p.receivedAt" class="muted right" />
          </button>
          <div v-if="openPackets[p.type]" class="inspect">
            <PacketInspector
              title="Received"
              :datagram="p.datagram"
              :fields="p.fields"
              :payload="suiteInfo(p.suite)?.encrypted ? p.payload : null"
              :encrypted="suiteInfo(p.suite)?.encrypted"
            />
            <PacketInspector v-if="p.response" title="Answer" :datagram="p.response" :fields="p.responseFields" />
          </div>
        </li>
      </ul>
      <DataTable
        v-else
        v-model:expanded-rows="openPackets"
        :value="packets.data.value"
        data-key="type"
        size="small"
        row-hover
        class="clickable"
        @row-click="(e) => (openPackets = toggleRow(openPackets, e.data.type))"
      >
        <Column expander style="width: 2.5rem" />
        <Column header="Type">
          <template #body="{ data }"
            ><strong>{{ typeName(data.type) }}</strong></template
          >
        </Column>
        <Column header="Received">
          <template #body="{ data }"
            ><RelativeTime :value="data.receivedAt" />&nbsp;<span class="muted small">{{
              formatAbsolute(data.receivedAt)
            }}</span></template
          >
        </Column>
        <Column header="FCNT">
          <template #body="{ data }"
            ><span class="mono">{{ data.fcnt }}</span></template
          >
        </Column>
        <Column header="Suite">
          <template #body="{ data }">{{ suiteName(data.suite) }}</template>
        </Column>
        <Column header="Bytes">
          <template #body="{ data }">{{ size(data.datagram) }}</template>
        </Column>
        <Column header="Preview">
          <template #body="{ data }"
            ><code class="ellipsis preview">{{ preview(data) }}</code></template
          >
        </Column>
        <Column header="Answer">
          <template #body="{ data }">
            <span v-if="answer(data)">{{ answer(data) }}</span>
            <span v-else class="muted">–</span>
          </template>
        </Column>
        <template #expansion="{ data }">
          <div class="inspect">
            <PacketInspector
              title="Received"
              :datagram="data.datagram"
              :fields="data.fields"
              :payload="suiteInfo(data.suite)?.encrypted ? data.payload : null"
              :encrypted="suiteInfo(data.suite)?.encrypted"
            />
            <PacketInspector
              v-if="data.response"
              :title="`Answer: ${answer(data)}`"
              :datagram="data.response"
              :fields="data.responseFields"
            />
          </div>
        </template>
      </DataTable>
    </section>

    <section class="panel" aria-labelledby="rejections-title">
      <div id="rejections-title" class="panel-header">
        Recent rejections <span class="muted refresh">since the restart, the last 20; no answer was sent</span>
      </div>
      <EmptyState
        v-if="rejections.data.value && !rows.length"
        icon="pi pi-check"
        title="Nothing rejected."
        text="Packets with a bad tag, a replayed counter or a suite the node does not allow show up here."
      />
      <ul v-else-if="phone" class="mobile-list">
        <li v-for="r in rows" :key="r.key">
          <button type="button" class="row" @click="openRejections = toggleRow(openRejections, r.key)">
            <Tag :value="words(r.reason)" severity="danger" />
            <span class="mono ellipsis">{{ r.endpoint }}</span>
            <RelativeTime :value="r.at" class="muted right" />
          </button>
          <div v-if="openRejections[r.key]" class="inspect">
            <p v-if="r.detail" class="muted detail">{{ r.detail }}</p>
            <PacketInspector :datagram="r.datagram" :fields="r.fields" />
          </div>
        </li>
      </ul>
      <DataTable
        v-else-if="rows.length"
        v-model:expanded-rows="openRejections"
        :value="rows"
        data-key="key"
        size="small"
        row-hover
        class="clickable"
        @row-click="(e) => (openRejections = toggleRow(openRejections, e.data.key))"
      >
        <Column expander style="width: 2.5rem" />
        <Column header="Time">
          <template #body="{ data }"><RelativeTime :value="data.at" /></template>
        </Column>
        <Column header="Reason">
          <template #body="{ data }"><Tag :value="words(data.reason)" severity="danger" /></template>
        </Column>
        <Column header="Address">
          <template #body="{ data }"
            ><span class="mono">{{ data.endpoint }}</span></template
          >
        </Column>
        <Column header="Bytes">
          <template #body="{ data }">{{ size(data.datagram) }}</template>
        </Column>
        <Column header="Detail">
          <template #body="{ data }"
            ><span class="ellipsis detail-cell" :title="data.detail ?? ''">{{ data.detail ?? '–' }}</span></template
          >
        </Column>
        <template #expansion="{ data }">
          <div class="inspect">
            <PacketInspector :datagram="data.datagram" :fields="data.fields" />
          </div>
        </template>
      </DataTable>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.refresh {
  font-weight: 400;
  margin-left: auto;
  font-size: 0.857rem;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.clickable :deep(tbody > tr:not(.p-datatable-row-expansion)) {
  cursor: pointer;
}

.small {
  font-size: 0.857rem;
}

.preview {
  display: block;
  max-width: 16rem;
}

.detail-cell {
  display: block;
  max-width: 24rem;
}

.inspect {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
  padding: var(--pnmt-space-2) 0;
  max-width: 60rem;
}

.mobile-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.mobile-list li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.row {
  all: unset;
  box-sizing: border-box;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  min-width: 0;
  cursor: pointer;
}

.right {
  margin-left: auto;
  white-space: nowrap;
}

.detail {
  margin: 0;
}
</style>
