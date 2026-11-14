<script setup lang="ts">
import Button from 'primevue/button'
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
type Show = 'special' | 'running' | 'all'
const show = ref<Show>('special')
const search = ref('')
const all = computed(() => section.data.value?.services?.services ?? [])
const special = (s: S['ServiceEntry']) => s.class === 'ThirdParty' || s.class === 'Unsigned' || s.autoNotRunning
const shows = (s: S['ServiceEntry'], what: Show) =>
  what === 'special' ? special(s) : what === 'running' ? s.state === 'Running' : true
const options = computed(() =>
  (
    [
      ['Special', 'special'],
      ['Running', 'running'],
      ['All', 'all'],
    ] as const
  ).map(([label, value]) => ({ label: `${label} (${all.value.filter((s) => shows(s, value)).length})`, value })),
)
const services = computed(() =>
  all.value
    .filter((s) => shows(s, show.value))
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
      :loading="section.loading.value"
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
        <template #empty>
          <p v-if="!section.data.value" class="empty muted">
            Not reported yet. A first collection checks every service's signature and can take a few minutes; if it
            fails, the overview lists it under the agent's recent problems.
          </p>
          <p v-else-if="!all.length" class="empty muted">The agent reported no services.</p>
          <p v-else-if="search.trim()" class="empty muted">No service matches “{{ search.trim() }}”.</p>
          <p v-else-if="show === 'special'" class="empty muted">
            Nothing special: no third-party or unsigned services, and every automatic service runs.
            <Button label="Show all" link size="small" @click="show = 'all'" />
          </p>
          <p v-else class="empty muted">None.</p>
        </template>
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

.empty {
  margin: 0;
  padding: var(--pnmt-space-3) 0;
}
</style>
