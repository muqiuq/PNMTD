<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import ProgressBar from 'primevue/progressbar'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, keys, type S, type UdpOtaState } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatDuration } from '@/format'
import { formatBytes } from '@/format/bytes'
import { downloadRate, isOpenUpdate, OTA_STATES, otaSeverity, percent, toggleRow, words } from '@/format/udp'
import OtaJobDetail from './OtaJobDetail.vue'

/**
 * Every firmware update (docs/udp-iot-plan.md §6.6), newest first: node, image, state, progress,
 * rate and time left while downloading, re-fetches, start and end. A row opens the job with its
 * command-log timeline (from the node's updates); open ones can be cancelled.
 */
const { ask, done } = useActions()
const state = ref<UdpOtaState | ''>('')
const states = [{ label: 'Any state', value: '' }, ...OTA_STATES.map((s) => ({ label: words(s), value: s }))]
const filters = computed(() => ({ state: state.value || undefined }))
// Every 5 s while something downloads, so progress moves.
const fast = ref(false)
const table = useServerTable<S['UdpOtaJobItem']>({
  key: [...keys.udp, 'ota-jobs'],
  fetch: (q) => api.udp.jobs(q as Parameters<typeof api.udp.jobs>[0]),
  filters,
  refetchInterval: computed(() => (fast.value ? 5_000 : 30_000)),
})
watch(
  () => table.items.value.some((j) => j.state === 'Downloading'),
  (downloading) => (fast.value = downloading),
  { immediate: true },
)
const expanded = ref<Record<string, boolean>>({})

async function cancel(job: S['UdpOtaJobItem']) {
  const ok = await ask(
    `Cancel the update of ${job.nodeName} to ${job.firmwareVersion}?`,
    'An offer not yet answered is not sent again; a running download gets the abort answer with its next GET.',
    'Cancel update',
  )
  if (ok) {
    await done(() => api.udp.cancelUpdate(job.id), {
      success: 'Update cancelled',
      invalidate: [keys.udp, ...(job.sensorId ? [keys.sensor(job.sensorId)] : [])],
    })
  }
}
</script>

<template>
  <div class="tab">
    <FilterBar v-model:search="table.search.value" placeholder="Node or image" :active="state ? 1 : 0">
      <Select
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="State"
        placeholder="Any state"
      />
    </FilterBar>
    <ListTable
      v-model:expanded="expanded"
      :table="table"
      what="updates"
      empty-text="No firmware update yet."
      selectable
      @select="(j) => (expanded = toggleRow(expanded, j.id))"
    >
      <Column expander style="width: 2.5rem" />
      <Column header="Node">
        <template #body="{ data }">
          <RouterLink v-if="data.sensorId" :to="{ name: 'sensor', params: { id: data.sensorId, tab: 'firmware' } }">{{
            data.nodeName
          }}</RouterLink>
          <span v-else class="muted">{{ data.nodeName }} (deleted)</span>
          <span class="muted small"> · {{ data.deviceId }}</span>
        </template>
      </Column>
      <Column header="Image">
        <template #body="{ data }">
          {{ data.firmwareName }} <span class="mono">{{ data.firmwareVersion }}</span>
        </template>
      </Column>
      <Column header="State" style="width: 8rem">
        <template #body="{ data }"><Tag :value="words(data.state)" :severity="otaSeverity(data.state)" /></template>
      </Column>
      <Column header="Progress" style="width: 13rem">
        <template #body="{ data }">
          <ProgressBar :value="percent(data.progress)" :show-value="false" class="bar" />
          <div class="muted small">{{ formatBytes(data.highestOffset) }} of {{ formatBytes(data.firmwareSize) }}</div>
        </template>
      </Column>
      <Column header="Rate" style="width: 9rem">
        <template #body="{ data }">
          <template v-if="downloadRate(data)">
            <div class="small">{{ formatBytes(Math.round(downloadRate(data)!.bytesPerSecond)) }}/s</div>
            <div class="muted small">{{ formatDuration(downloadRate(data)!.secondsLeft * 1000) }} left</div>
          </template>
          <span v-else class="muted">–</span>
        </template>
      </Column>
      <Column header="Re-fetches" style="width: 6rem">
        <template #body="{ data }">{{ data.chunkRetransmissions.toLocaleString() }}</template>
      </Column>
      <Column header="Started" style="width: 7rem">
        <template #body="{ data }"><RelativeTime :value="data.startedAt" empty="–" /></template>
      </Column>
      <Column header="Finished" style="width: 7rem">
        <template #body="{ data }"><RelativeTime :value="data.finishedAt" empty="–" /></template>
      </Column>
      <Column header="" style="width: 3rem">
        <template #body="{ data }">
          <Button
            v-if="isOpenUpdate(data.state)"
            v-tooltip.top="'Cancel'"
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Cancel the update of ${data.nodeName}`"
            @click="cancel(data)"
          />
        </template>
      </Column>
      <template #expansion="{ data }">
        <div class="detail"><OtaJobDetail :job="data" @cancel="cancel" /></div>
      </template>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="words(item.state)" :severity="otaSeverity(item.state)" />
          <span class="ellipsis">{{ item.nodeName }} · {{ item.firmwareVersion }}</span>
          <span class="muted right small">{{ percent(item.progress) }} %</span>
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

.small {
  font-size: 0.857rem;
}

.mono {
  font-family: var(--pnmt-mono);
}

.bar {
  height: 6px;
}

.detail {
  padding: var(--pnmt-space-2) 0;
  max-width: 60rem;
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
