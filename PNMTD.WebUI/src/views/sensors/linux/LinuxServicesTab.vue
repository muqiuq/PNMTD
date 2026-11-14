<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref, toRef } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import SectionBar from '../agent/SectionBar.vue'
import { useLinuxSection } from '../agent/useAgentSection'
import { serviceClassLabel, serviceClassSeverity, serviceRank, specialService } from './linuxFormat'

/**
 * A Linux server's services, timers and cron (docs/linux-sensor-plan.md §5.4): the special ones first (third-party and
 * unpackaged programs, services enabled but not running, failed units), with a filter for everything; each timer with its last
 * and next run; each cron entry with its schedule, user and the program it runs, the full line only where config.yaml collects
 * it.
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useLinuxSection(id, 'services')
const data = computed(() => section.data.value?.services ?? null)
type Show = 'special' | 'running' | 'all'
const show = ref<Show>('special')
const search = ref('')
const all = computed(() =>
  [...(data.value?.services ?? [])].sort((a, b) => serviceRank(a) - serviceRank(b) || a.name.localeCompare(b.name)),
)
const shows = (s: S['LinuxServiceEntry'], what: Show) =>
  what === 'special' ? specialService(s) : what === 'running' ? s.subState === 'running' : true
const options = computed(() =>
  (
    [
      ['Special', 'special'],
      ['Running', 'running'],
      ['All', 'all'],
    ] as const
  ).map(([label, value]) => ({ label: `${label} (${all.value.filter((s) => shows(s, value)).length})`, value })),
)
const matches = (q: string, ...values: (string | null | undefined)[]) =>
  !q || values.some((v) => v?.toLowerCase().includes(q))
const query = computed(() => search.value.trim().toLowerCase())
const services = computed(() =>
  all.value
    .filter((s) => shows(s, show.value))
    .filter((s) => matches(query.value, s.name, s.description, s.program, s.package)),
)
const timers = computed(() => (data.value?.timers ?? []).filter((t) => matches(query.value, t.name, t.unit)))
const cron = computed(() =>
  (data.value?.cron ?? []).filter((c) => matches(query.value, c.program, c.command, c.user, c.file)),
)
const stateSeverity = (s: S['LinuxServiceEntry']) =>
  s.failed ? 'danger' : s.enabledNotRunning ? 'warn' : s.subState === 'running' ? 'success' : 'secondary'
/** Where the program comes from: its package and archive, its snap, or nothing (unpackaged). */
const origin = (s: S['LinuxServiceEntry']) =>
  s.snap
    ? `snap ${s.snap}`
    : s.package
      ? `${s.package}${s.origin ? ` · ${s.origin}` : ''}`
      : s.class === 'Pnmt'
        ? ''
        : 'no package'
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="services"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
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
      <InputText v-model="search" placeholder="Search services, timers and cron" size="small" />
      <span class="muted small">{{ services.length }} shown</span>
      <span v-if="data" class="muted small">· {{ data.packages }} packages installed</span>
    </div>
    <section class="panel">
      <DataTable
        :value="services"
        size="small"
        scrollable
        scroll-height="40rem"
        data-key="name"
        :row-class="(s) => (s.class === 'Unpackaged' ? 'highlight' : '')"
      >
        <template #empty>
          <p v-if="!section.data.value" class="empty muted">Not reported yet.</p>
          <p v-else-if="!all.length" class="empty muted">The agent reported no services.</p>
          <p v-else-if="query" class="empty muted">No service matches “{{ search.trim() }}”.</p>
          <p v-else-if="show === 'special'" class="empty muted">
            Nothing special: every program comes from the distribution, and every enabled service runs.
            <Button label="Show all" link size="small" @click="show = 'all'" />
          </p>
          <p v-else class="empty muted">None.</p>
        </template>
        <Column header="Service">
          <template #body="{ data: s }">
            <strong>{{ s.name }}</strong> <span class="muted small">{{ s.description }}</span>
            <div class="muted small mono ellipsis" :title="s.unitFile ?? undefined">{{ s.program ?? s.unitFile }}</div>
          </template>
        </Column>
        <Column header="Class" style="width: 13rem">
          <template #body="{ data: s }">
            <Tag :value="serviceClassLabel(s.class)" :severity="serviceClassSeverity(s.class)" />
            <div class="muted small">{{ origin(s) }}</div>
          </template>
        </Column>
        <Column header="State" style="width: 12rem">
          <template #body="{ data: s }">
            <Tag :value="s.subState" :severity="stateSeverity(s)" class="small" />
            <span class="muted small"> {{ s.unitFileState ?? s.loadState }}</span>
            <div v-if="s.enabledNotRunning && !s.failed" class="small warn">enabled, not running</div>
          </template>
        </Column>
        <Column header="User" style="width: 8rem">
          <template #body="{ data: s }"
            ><span class="small">{{ s.user ?? 'root' }}</span></template
          >
        </Column>
        <Column header="Unit file" style="width: 7rem">
          <template #body="{ data: s }"
            ><span class="small muted" :title="s.unitFile ?? undefined">{{ s.unitSource ?? '–' }}</span></template
          >
        </Column>
      </DataTable>
    </section>
    <div v-if="data" class="two-columns">
      <section class="panel">
        <div class="panel-header">Timers</div>
        <DataTable :value="timers" size="small" data-key="name">
          <template #empty><p class="empty muted">None.</p></template>
          <Column header="Timer">
            <template #body="{ data: t }">
              <strong>{{ t.name }}</strong>
              <div class="muted small">{{ t.unit }} · {{ t.unitFileState ?? '–' }}</div>
            </template>
          </Column>
          <Column header="Last" style="width: 7rem">
            <template #body="{ data: t }"><RelativeTime v-if="t.lastRun" :value="t.lastRun" /></template>
          </Column>
          <Column header="Next" style="width: 7rem">
            <template #body="{ data: t }"><RelativeTime v-if="t.nextRun" :value="t.nextRun" /></template>
          </Column>
        </DataTable>
      </section>
      <section class="panel">
        <div class="panel-header">Cron</div>
        <DataTable :value="cron" size="small" data-key="lineHash">
          <template #empty><p class="empty muted">None.</p></template>
          <Column header="Schedule" style="width: 8rem">
            <template #body="{ data: c }"
              ><span class="mono small">{{ c.schedule }}</span></template
            >
          </Column>
          <Column header="User" style="width: 6rem">
            <template #body="{ data: c }"
              ><span class="small">{{ c.user }}</span></template
            >
          </Column>
          <Column header="Runs">
            <template #body="{ data: c }">
              <span class="mono small">{{ c.command ?? c.program }}</span>
              <div class="muted small mono">{{ c.file }}</div>
            </template>
          </Column>
        </DataTable>
        <div v-if="!data.cronCommands" class="panel-body small muted note">
          Only the program each line runs: command lines can hold passwords. Root collects them in full with
          <code>collect: { cron_commands: true }</code> in /etc/pnmt-agent/config.yaml.
        </div>
      </section>
    </div>
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

.warn {
  color: var(--pnmt-warn);
}

.empty {
  margin: 0;
  padding: var(--pnmt-space-3) 0;
}

.note {
  border-top: 1px solid var(--pnmt-border);
}

/* Unpackaged programs: highlighted (§5.4). */
:deep(tr.highlight) > td:first-child {
  box-shadow: inset 3px 0 0 var(--pnmt-fail);
}
</style>
