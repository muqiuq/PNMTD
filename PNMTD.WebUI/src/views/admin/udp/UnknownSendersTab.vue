<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import { computed, ref } from 'vue'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PacketInspector from '@/components/PacketInspector.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useMedia } from '@/composables/useMedia'
import { asciiPreview, fromBase64, hexPreview } from '@/format/bytes'
import { suiteName, toggleRow } from '@/format/udp'
import HostPickerDialog from './HostPickerDialog.vue'

/**
 * Unknown senders (docs/udp-iot-plan.md §4.7, §6.6): datagrams for device ids PNMT does not know,
 * or that do not parse, grouped by id and address, in memory since the restart. A device that is
 * already sending can be adopted: "Create node with id N" opens the sensor form with that id.
 */
const phone = useMedia('(max-width: 599px)')
const senders = useQuery({
  queryKey: [...keys.udp, 'unknown-senders'],
  queryFn: api.udp.unknownSenders,
  refetchInterval: 10_000,
})
const rows = computed(() => (senders.data.value ?? []).map((s) => ({ ...s, key: `${s.deviceId ?? '-'}@${s.address}` })))
const expanded = ref<Record<string, boolean>>({})
const adopt = ref<number | null>(null)
const adoptOpen = computed({ get: () => adopt.value !== null, set: (v) => !v && (adopt.value = null) })
const bytes = (b64: string) => fromBase64(b64)
</script>

<template>
  <section class="panel" aria-labelledby="unknown-title">
    <div id="unknown-title" class="panel-header">
      Unknown senders <span class="muted note">since the restart, the last 200; none of them got an answer</span>
    </div>
    <ErrorState
      v-if="senders.isError.value && !senders.data.value"
      :error="senders.error.value"
      what="the unknown senders"
      @retry="senders.refetch()"
    />
    <EmptyState
      v-else-if="senders.data.value && !rows.length"
      icon="pi pi-check"
      title="No unknown sender."
      text="Packets for a device id PNMT does not know, and datagrams that do not parse, show up here."
    />
    <ul v-else-if="phone" class="mobile-list">
      <li v-for="s in rows" :key="s.key">
        <button type="button" class="row" @click="expanded = toggleRow(expanded, s.key)">
          <strong>{{ s.deviceId ?? '?' }}</strong>
          <span class="mono ellipsis">{{ s.address }}</span>
          <span class="muted">×{{ s.count }}</span>
          <RelativeTime :value="s.lastSeen" class="muted right" />
        </button>
        <div v-if="expanded[s.key]" class="inspect">
          <p v-if="s.problem" class="muted">{{ s.problem }}</p>
          <PacketInspector :datagram="s.lastDatagram" title="Last datagram" />
          <Button
            v-if="s.deviceId && !s.nodeExists"
            :label="`Create node with id ${s.deviceId}`"
            icon="pi pi-plus"
            size="small"
            @click="adopt = s.deviceId"
          />
        </div>
      </li>
    </ul>
    <DataTable
      v-else
      v-model:expanded-rows="expanded"
      :value="rows"
      data-key="key"
      size="small"
      row-hover
      :loading="senders.isLoading.value"
      class="clickable"
      @row-click="(e) => (expanded = toggleRow(expanded, e.data.key))"
    >
      <Column expander style="width: 2.5rem" />
      <Column header="Device id" style="width: 7rem">
        <template #body="{ data }">
          <span v-if="data.deviceId !== null" class="mono">{{ data.deviceId }}</span>
          <span v-else class="muted">–</span>
        </template>
      </Column>
      <Column header="Address">
        <template #body="{ data }"
          ><span class="mono">{{ data.address }}</span></template
        >
      </Column>
      <Column header="First seen" style="width: 7rem">
        <template #body="{ data }"><RelativeTime :value="data.firstSeen" /></template>
      </Column>
      <Column header="Last seen" style="width: 7rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSeen" /></template>
      </Column>
      <Column header="Count" style="width: 5rem">
        <template #body="{ data }">{{ data.count.toLocaleString() }}</template>
      </Column>
      <Column header="Last suite" style="width: 9rem">
        <template #body="{ data }">{{ data.lastSuite ? suiteName(data.lastSuite) : '–' }}</template>
      </Column>
      <Column header="Problem">
        <template #body="{ data }">
          <span class="ellipsis cell" :title="data.problem ?? ''">{{
            data.problem ?? (data.nodeExists ? 'The node exists now' : 'Unknown device id')
          }}</span>
        </template>
      </Column>
      <Column header="Last datagram">
        <template #body="{ data }">
          <span class="bytes-cell">
            <code class="ellipsis">{{ hexPreview(bytes(data.lastDatagram), 8) }}</code>
            <code class="ellipsis muted">{{ asciiPreview(bytes(data.lastDatagram), 16) }}</code>
          </span>
        </template>
      </Column>
      <Column header="" style="width: 12rem">
        <template #body="{ data }">
          <Button
            v-if="data.deviceId && !data.nodeExists"
            :label="`Create node with id ${data.deviceId}`"
            size="small"
            text
            @click="adopt = data.deviceId"
          />
        </template>
      </Column>
      <template #expansion="{ data }">
        <div class="inspect"><PacketInspector :datagram="data.lastDatagram" title="Last datagram" /></div>
      </template>
    </DataTable>
    <HostPickerDialog v-model:visible="adoptOpen" :device-id="adopt" />
  </section>
</template>

<style scoped>
.note {
  font-weight: 400;
  font-size: 0.857rem;
  margin-left: auto;
}

.clickable :deep(tbody > tr:not(.p-datatable-row-expansion)) {
  cursor: pointer;
}

.cell {
  display: block;
  max-width: 16rem;
}

.bytes-cell {
  display: flex;
  flex-direction: column;
  min-width: 0;
  max-width: 14rem;
}

.inspect {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) 0;
  max-width: 60rem;
}

.inspect p {
  margin: 0;
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
</style>
