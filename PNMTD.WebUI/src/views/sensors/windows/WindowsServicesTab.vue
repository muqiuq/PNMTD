<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import type { S } from '@/api/resources'
import SectionBar from './SectionBar.vue'
import { useWindowsSection } from './useWindowsSection'

/**
 * A server's services (docs/windows-sensor-plan.md §4.5): the special ones first (third-party and unsigned), with a filter
 * for everything; automatic services that don't run are flagged.
 */
const props = defineProps<{ server: S['WindowsServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useWindowsSection(id, 'services')
const show = ref<'special' | 'running' | 'all'>('special')
const search = ref('')
const options = [
  { label: 'Special', value: 'special' },
  { label: 'Running', value: 'running' },
  { label: 'All', value: 'all' },
]
const services = computed(() =>
  (section.data.value?.services?.services ?? [])
    .filter((s) =>
      show.value === 'special'
        ? s.class === 'ThirdParty' || s.class === 'Unsigned' || s.autoNotRunning
        : show.value === 'running'
          ? s.state === 'Running'
          : true,
    )
    .filter((s) => {
      const q = search.value.trim().toLowerCase()
      return !q || s.name.toLowerCase().includes(q) || s.displayName.toLowerCase().includes(q)
    }),
)
const classLabel = (c: S['ServiceClass']) =>
  c === 'ThirdParty' ? 'third-party' : c === 'Pnmt' ? 'PNMT' : c.toLowerCase()
const classSeverity = (c: S['ServiceClass']) =>
  c === 'Unsigned' ? 'danger' : c === 'ThirdParty' ? 'warn' : c === 'Microsoft' ? 'info' : 'secondary'
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="services"
      :view="section.data.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <div class="toolbar">
      <SelectButton
        v-model="show"
        :options="options"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      />
      <InputText v-model="search" placeholder="Search services" size="small" />
      <span class="muted small">{{ services.length }} shown</span>
    </div>
    <section class="panel">
      <DataTable :value="services" size="small" scrollable scroll-height="40rem" data-key="name">
        <Column header="Service">
          <template #body="{ data }">
            <strong>{{ data.displayName }}</strong> <span class="muted small mono">{{ data.name }}</span>
            <div class="muted small mono ellipsis">{{ data.serviceDll ?? data.path }}</div>
          </template>
        </Column>
        <Column header="Class" style="width: 9rem">
          <template #body="{ data }"
            ><Tag :value="classLabel(data.class)" :severity="classSeverity(data.class)"
          /></template>
        </Column>
        <Column header="State" style="width: 11rem">
          <template #body="{ data }">
            {{ data.state }} · {{ data.startType }}
            <div v-if="data.autoNotRunning"><Tag value="automatic, not running" severity="warn" class="small" /></div>
          </template>
        </Column>
        <Column header="Account" style="width: 12rem">
          <template #body="{ data }"
            ><span class="small">{{ data.account ?? '–' }}</span></template
          >
        </Column>
        <Column header="Signer" style="width: 14rem">
          <template #body="{ data }">
            <span class="small">{{ data.signer ?? 'unsigned' }}</span>
            <div class="muted small">{{ data.company }} {{ data.version }}</div>
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

.toolbar {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.small {
  font-size: 0.857rem;
}
</style>
