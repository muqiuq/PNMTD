<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import SectionBar from '../agent/SectionBar.vue'
import { useAgentSection } from '../agent/useAgentSection'

/** The event log (docs/windows-sensor-plan.md §4.8): the newest 100 of System and Application at Critical, Error or Warning. */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useAgentSection(id, 'event-log')
const level = ref<'all' | 'Critical' | 'Error' | 'Warning'>('all')
const log = ref<'all' | 'System' | 'Application'>('all')
const entries = computed(() =>
  (section.data.value?.eventLog?.entries ?? [])
    .filter((e) => level.value === 'all' || e.level === level.value)
    .filter((e) => log.value === 'all' || e.log === log.value),
)
const severity = (l: string) => (l === 'Critical' ? 'danger' : l === 'Error' ? 'danger' : 'warn')
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="event-log"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <div class="toolbar">
      <SelectButton
        v-model="level"
        :options="['all', 'Critical', 'Error', 'Warning']"
        :allow-empty="false"
        size="small"
      />
      <SelectButton v-model="log" :options="['all', 'System', 'Application']" :allow-empty="false" size="small" />
    </div>
    <section class="panel">
      <DataTable :value="entries" size="small" scrollable scroll-height="40rem">
        <Column header="When" style="width: 8rem">
          <template #body="{ data }"><RelativeTime :value="data.at" /></template>
        </Column>
        <Column header="Level" style="width: 7rem">
          <template #body="{ data }"><Tag :value="data.level" :severity="severity(data.level)" /></template>
        </Column>
        <Column header="Source" style="width: 14rem">
          <template #body="{ data }">
            {{ data.source }} <span class="muted mono small">{{ data.eventId }}</span>
            <div class="muted small">
              {{ data.log }}<template v-if="data.user"> · {{ data.user }}</template>
            </div>
          </template>
        </Column>
        <Column header="Message">
          <template #body="{ data }"
            ><span class="message">{{ data.message }}</span></template
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
}

.message {
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 0.929rem;
}

.small {
  font-size: 0.857rem;
}
</style>
