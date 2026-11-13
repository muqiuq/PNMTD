<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { stateSeverity } from './aiFormat'

/**
 * The AI's work (§3.5): open items first. A failed attempt is retried every minute 5 times, then after 1 hour, 3 hours
 * and every 24 hours; a spent budget makes items wait for the next period. An Admin retries now, or cancels.
 */
const state = ref<S['AiWorkState'] | ''>('')
const filters = computed(() => ({ state: state.value || undefined }))
const table = useServerTable<S['AiWorkItem']>({
  key: [...keys.ai, 'work'],
  fetch: (q) => api.ai.work(q),
  filters,
  refetchInterval: 5_000,
})
const states = [
  { label: 'Every state', value: '' },
  ...(['Queued', 'Running', 'WaitingRetry', 'WaitingBudget', 'Succeeded', 'Failed', 'Cancelled'] as const).map((s) => ({
    label: s,
    value: s,
  })),
]
const open = (s: S['AiWorkState']) => s === 'Queued' || s === 'WaitingRetry' || s === 'WaitingBudget'
const { done } = useActions()
const retry = (w: S['AiWorkItem']) => done(() => api.ai.retry(w.id), { success: 'Retrying now', invalidate: [keys.ai] })
const cancel = (w: S['AiWorkItem']) => done(() => api.ai.cancel(w.id), { success: 'Cancelled', invalidate: [keys.ai] })
</script>

<template>
  <div>
    <FilterBar :active="state ? 1 : 0">
      <Select
        v-model="state"
        :options="states"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="State"
      />
    </FilterBar>
    <ListTable :table="table" what="work items" empty-text="No AI work yet.">
      <Column header="Purpose" style="width: 11rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.purpose }}</span></template
        >
      </Column>
      <Column header="State" style="width: 9rem">
        <template #body="{ data }"><Tag :value="data.state" :severity="stateSeverity(data.state)" /></template>
      </Column>
      <Column header="Failed attempts" style="width: 8rem">
        <template #body="{ data }">{{ data.attempts }}</template>
      </Column>
      <Column header="Next attempt" style="width: 9rem">
        <template #body="{ data }"
          ><RelativeTime v-if="open(data.state)" :value="data.nextAttemptAt" /><span v-else class="muted"
            >–</span
          ></template
        >
      </Column>
      <Column header="Last error">
        <template #body="{ data }"
          ><span class="muted ellipsis">{{ data.lastError ?? '' }}</span></template
        >
      </Column>
      <Column header="" style="width: 11rem">
        <template #body="{ data }">
          <Button
            v-if="open(data.state) || data.state === 'Failed'"
            label="Retry now"
            size="small"
            text
            @click="retry(data)"
          />
          <Button v-if="open(data.state)" label="Cancel" size="small" text severity="secondary" @click="cancel(data)" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="item.state" :severity="stateSeverity(item.state)" />
          <span class="mono">{{ item.purpose }}</span>
        </div>
        <div v-if="item.lastError" class="muted ellipsis">{{ item.lastError }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
