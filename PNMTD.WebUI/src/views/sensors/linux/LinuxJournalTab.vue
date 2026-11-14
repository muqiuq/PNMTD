<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import SectionBar from '../agent/SectionBar.vue'
import { useLinuxSection } from '../agent/useAgentSection'
import { entrySource, priorityTag } from './linuxFormat'

/**
 * The journal (docs/linux-sensor-plan.md §5.7): the newest 100 entries at warning or above, from this boot and the one before,
 * every unit except the login sources. Priority colours, a filter by priority and by unit or identifier, a search.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useLinuxSection(id, 'journal')
const journal = computed(() => section.data.value?.journal ?? null)
type Level = 'all' | 'critical' | 'err' | 'warning'
const level = ref<Level>('all')
const levels = [
  { label: 'All', value: 'all' },
  { label: 'emerg to crit', value: 'critical' },
  { label: 'err', value: 'err' },
  { label: 'warning', value: 'warning' },
]
const source = ref<string | null>(null)
const search = ref('')
const all = computed(() => [...(journal.value?.entries ?? [])].sort((a, b) => b.at.localeCompare(a.at)))
const sources = computed(() => [...new Set(all.value.map(entrySource))].sort())
const atLevel = (e: S['JournalEntry']) =>
  level.value === 'all' ||
  (level.value === 'critical' ? e.priority <= 2 : level.value === 'err' ? e.priority === 3 : e.priority === 4)
const entries = computed(() => {
  const q = search.value.trim().toLowerCase()
  return all.value
    .filter(atLevel)
    .filter((e) => !source.value || entrySource(e) === source.value)
    .filter((e) => !q || e.message.toLowerCase().includes(q) || entrySource(e).toLowerCase().includes(q))
})
const boots = computed(() => [...new Set(all.value.map((e) => e.boot).filter(Boolean))])
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="journal"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <Message v-if="journal && !journal.persistent" severity="warn" :closable="false" size="small">
      The journal is not persistent: it is lost at every reboot, and the boot before can't be read. The Access tab's
      logging check has the command that fixes it.
    </Message>
    <div class="toolbar">
      <SelectButton
        v-model="level"
        :options="levels"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      />
      <Select
        v-model="source"
        :options="sources"
        show-clear
        filter
        size="small"
        placeholder="Every unit"
        aria-label="Unit or identifier"
      />
      <InputText v-model="search" placeholder="Search messages" size="small" />
      <span class="muted small">{{ entries.length }} of {{ all.length }}</span>
    </div>
    <section class="panel">
      <DataTable :value="entries" size="small" scrollable scroll-height="40rem" data-key="cursor">
        <template #empty>
          <p class="empty muted">
            {{
              !section.data.value
                ? 'Not reported yet.'
                : all.length
                  ? 'No entry matches.'
                  : 'No entry at warning or above.'
            }}
          </p>
        </template>
        <Column header="When" style="width: 8rem">
          <template #body="{ data }">
            <RelativeTime :value="data.at" />
            <div v-if="boots.length > 1 && data.boot !== boots[0]" class="muted small">boot before</div>
          </template>
        </Column>
        <Column header="Priority" style="width: 7rem">
          <template #body="{ data }">
            <Tag
              :value="priorityTag(data.priority).label"
              :severity="priorityTag(data.priority).severity"
              :icon="priorityTag(data.priority).icon"
            />
          </template>
        </Column>
        <Column header="Unit" style="width: 14rem">
          <template #body="{ data }">
            {{ entrySource(data) }}
            <div class="muted small">
              <template v-if="data.unit && data.identifier">{{ data.identifier }}</template><template v-if="data.pid">
                [{{ data.pid }}]</template
              >
            </div>
          </template>
        </Column>
        <Column header="Message">
          <template #body="{ data }"
            ><span class="message mono">{{ data.message }}</span></template
          >
        </Column>
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

.toolbar {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  align-items: center;
}

.message {
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 0.857rem;
}

.empty {
  margin: 0;
  padding: var(--pnmt-space-3) 0;
}

.small {
  font-size: 0.857rem;
}
</style>
