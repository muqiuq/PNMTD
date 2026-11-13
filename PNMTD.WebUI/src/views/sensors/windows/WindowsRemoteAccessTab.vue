<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import SectionBar from './SectionBar.vue'
import { useWindowsSection } from './useWindowsSection'

/** Remote access (docs/windows-sensor-plan.md §4.6): each way in, whether it is active, listening, and let in by the firewall. */
const props = defineProps<{ server: S['WindowsServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useWindowsSection(id, 'remote-access')
const remote = computed(() => section.data.value?.remoteAccess ?? null)
const features = computed(() =>
  [...(remote.value?.features ?? [])].sort(
    (a, b) => Number(b.active) - Number(a.active) || a.name.localeCompare(b.name),
  ),
)
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="remote-access"
      :view="section.data.value"
      :loading="section.loading.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <section v-if="remote" class="panel">
      <div class="panel-header">{{ remote.summary }}</div>
      <DataTable :value="features" size="small" data-key="key">
        <Column header="Feature">
          <template #body="{ data }">
            <strong :class="{ muted: !data.active }">{{ data.name }}</strong>
            <span v-if="!data.installed" class="muted small"> · not installed</span>
          </template>
        </Column>
        <Column header="Active" style="width: 7rem">
          <template #body="{ data }"
            ><Tag :value="data.active ? 'active' : 'off'" :severity="data.active ? 'warn' : 'secondary'"
          /></template>
        </Column>
        <Column header="Ports" style="width: 9rem">
          <template #body="{ data }">
            <span class="mono small">{{ data.ports.join(', ') || '–' }}</span>
            <span v-if="data.listening" class="small"> · listening</span>
          </template>
        </Column>
        <Column header="Firewall" style="width: 14rem">
          <template #body="{ data }">
            <template v-if="data.firewallOpen">open: {{ data.firewallProfiles.join(', ') }}</template>
            <span v-else class="muted">closed</span>
          </template>
        </Column>
        <Column header="Notes">
          <template #body="{ data }">
            <Tag
              v-for="n in data.notes"
              :key="n"
              :value="n"
              :severity="/off|SMB1|not required|full control/.test(n) ? 'warn' : 'secondary'"
              class="small"
            />
          </template>
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

.small {
  font-size: 0.857rem;
}
</style>
