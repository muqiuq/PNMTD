<script setup lang="ts">
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { logKindLabel, logKindSeverity, logKinds } from './agentFormat'

/**
 * The agents' permanent log (docs/windows-sensor-plan.md §8.3): joins, connections, problems, crashes, failures,
 * revocations. For every agent, or one server's (its Agent tab). Admin.
 */
const props = defineProps<{ sensorId?: string }>()
const kind = ref<S['AgentLogKind'] | null>(null)
const filters = computed(() => ({ kind: kind.value ?? undefined }))
const table = useServerTable<S['AgentLogItem']>({
  key: [...keys.agents, 'log', props.sensorId ?? 'all'],
  fetch: (q) => (props.sensorId ? api.agents.serverLog(props.sensorId, q) : api.agents.agentLog(q)),
  pageSize: 50,
  filters,
  refetchInterval: 15_000,
})
const expanded = ref<Record<string, boolean>>({})
function details(item: S['AgentLogItem']): string {
  if (!item.details) return ''
  try {
    return JSON.stringify(JSON.parse(item.details), null, 2)
  } catch {
    return item.details
  }
}
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select
        v-model="kind"
        :options="logKinds"
        :option-label="(k: S['AgentLogKind']) => logKindLabel(k)"
        show-clear
        size="small"
        placeholder="Every kind"
        aria-label="Kind"
      />
    </div>
    <ListTable :table="table" what="log entries" empty-text="Nothing logged yet.">
      <Column header="When" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.at" /></template>
      </Column>
      <Column header="Kind" style="width: 8rem">
        <template #body="{ data }"
          ><Tag :value="logKindLabel(data.kind)" :severity="logKindSeverity(data.kind)"
        /></template>
      </Column>
      <Column v-if="!sensorId" header="Server" style="width: 14rem">
        <template #body="{ data }">
          <RouterLink v-if="data.sensorId" :to="{ name: 'sensor', params: { id: data.sensorId, tab: 'agent' } }">{{
            data.agentName
          }}</RouterLink>
          <span v-else class="muted">{{ data.agentName }}</span>
        </template>
      </Column>
      <Column header="Message">
        <template #body="{ data }">
          <span class="message">{{ data.message }}</span>
          <button v-if="data.details" type="button" class="link small" @click="expanded[data.id] = !expanded[data.id]">
            {{ expanded[data.id] ? 'less' : 'details' }}
          </button>
          <pre v-if="expanded[data.id]" class="details mono">{{ details(data) }}</pre>
          <div v-if="data.address" class="small muted mono">{{ data.address }}</div>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="logKindLabel(item.kind)" :severity="logKindSeverity(item.kind)" />
          <RelativeTime :value="item.at" />
        </div>
        <div class="small">{{ item.message }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar,
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.message {
  white-space: pre-wrap;
  word-break: break-word;
}

.link {
  background: none;
  border: none;
  color: var(--p-primary-color);
  cursor: pointer;
  padding: 0 var(--pnmt-space-1);
}

.details {
  white-space: pre-wrap;
  background: var(--pnmt-bg);
  padding: var(--pnmt-space-2);
  border-radius: var(--p-border-radius-md);
  max-height: 20rem;
  overflow: auto;
  font-size: 0.857rem;
}

.small {
  font-size: 0.857rem;
}
</style>
