<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { useServerTable } from '@/composables/useServerTable'
import AptLog from './AptLog.vue'
import {
  countChips,
  runCommand,
  runDuration,
  runStateLabel,
  runStateSeverity,
  runWhat,
  triggerLabel,
} from './osUpdatesFormat'
import { osUpdatesInvalidate } from './useOsUpdates'

/**
 * The log of runs (docs/linux-os-updates-plan.md §10.1 5): updates, upgrades and the runs outside PNMT from history.log, newest
 * first, with when, what, who or what started it, how long it took, its result and its state. A run opens its summary and its
 * full log (AptLog); a failed upgrade can be marked handled (Servers: Run), which ends its 467.
 */
const props = defineProps<{ sensorId: string; canRun: boolean }>()
const { run } = useActions()
const now = useNow()

const kind = ref<S['OsUpdateKind'] | null>(null)
const kinds = [
  { label: 'All', value: null },
  { label: 'Updates', value: 'Update' },
  { label: 'Upgrades', value: 'Upgrade' },
]
const filters = computed(() => ({ kind: kind.value ?? undefined }))
const table = useServerTable<S['OsUpdateRunItem']>({
  key: [...keys.osUpdatesServer(props.sensorId), 'runs'],
  fetch: (q) => api.linux.osUpdates.runs(props.sensorId, q),
  pageSize: 25,
  filters,
  refetchInterval: 15_000,
})

const selected = ref<string | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const detail = useQuery({
  queryKey: computed(() => [...keys.osUpdatesServer(props.sensorId), 'run', selected.value]),
  queryFn: () => api.linux.osUpdates.run(props.sensorId, selected.value!),
  enabled: computed(() => selected.value !== null),
  refetchInterval: (q) => (q.state.data && !q.state.data.run.finishedAt ? 3000 : false),
})
const d = computed(() => (detail.data.value?.run.id === selected.value ? detail.data.value : undefined))
const canDismiss = (r: S['OsUpdateRunItem']) =>
  props.canRun && r.kind === 'Upgrade' && r.state === 'Failed' && !r.dismissed

async function dismiss(r: S['OsUpdateRunItem']) {
  await run(() => api.linux.osUpdates.dismiss(props.sensorId, r.id), {
    success: 'Marked handled',
    invalidate: osUpdatesInvalidate(props.sensorId),
  })
}
const finished = () => void detail.refetch()
/** A summary's packages: "openssl 3.0.14 → 3.0.15". */
const change = (p: S['PackageChange']) =>
  `${p.name}${p.arch ? `:${p.arch}` : ''} ${p.from ?? ''}${p.from && p.to ? ' → ' : ''}${p.to ?? ''}`.trim()
</script>

<template>
  <section class="panel" aria-labelledby="runs-title">
    <div class="panel-header">
      <span id="runs-title">Runs</span>
      <span class="spacer" />
      <SelectButton
        v-model="kind"
        :options="kinds"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Kind of run"
      />
    </div>
    <ListTable
      :table="table"
      what="runs"
      selectable
      empty-text="No run yet: the first daily update comes in its window."
      class="runs"
      @select="(r) => (selected = r.id)"
    >
      <Column header="When" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.startedAt ?? data.requestedAt" /></template>
      </Column>
      <Column header="What" style="width: 8rem">
        <template #body="{ data }"
          ><span :title="data.command ?? runCommand(data)">{{ runWhat(data) }}</span></template
        >
      </Column>
      <Column header="Started by" style="width: 13rem">
        <template #body="{ data }"
          ><span class="small">{{ triggerLabel(data) }}</span></template
        >
      </Column>
      <Column header="Took" style="width: 6rem">
        <template #body="{ data }"
          ><span class="small">{{ runDuration(data, now) }}</span></template
        >
      </Column>
      <Column header="Result">
        <template #body="{ data }">
          <span class="chips">
            <Tag v-for="c in countChips(data.counts)" :key="c.label" :value="c.label" :severity="c.severity" />
          </span>
        </template>
      </Column>
      <Column header="State" style="width: 10rem">
        <template #body="{ data }">
          <span class="chips">
            <Tag :value="runStateLabel(data.state)" :severity="runStateSeverity(data.state)" />
            <span v-if="data.exitCode !== null && data.exitCode !== 0" class="mono small"
              >exit {{ data.exitCode }}</span
            >
            <Tag v-if="data.takingLong" value="taking long" severity="warn" />
            <Tag v-if="data.dismissed" value="handled" severity="secondary" />
          </span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="chips">
          <Tag :value="runStateLabel(item.state)" :severity="runStateSeverity(item.state)" />
          <span>{{ runWhat(item) }}</span>
          <span class="muted small">{{ triggerLabel(item) }}</span>
        </div>
        <div class="small muted">
          <RelativeTime :value="item.startedAt ?? item.requestedAt" /> · {{ runDuration(item, now) }}
        </div>
      </template>
    </ListTable>
    <Dialog
      v-model:visible="open"
      :header="d ? runCommand(d.run) : 'Run'"
      modal
      maximizable
      dismissable-mask
      :style="{ width: '90vw', maxWidth: '72rem' }"
      :breakpoints="{ '700px': '100vw' }"
    >
      <div v-if="d" class="detail">
        <div class="chips">
          <Tag :value="runStateLabel(d.run.state)" :severity="runStateSeverity(d.run.state)" />
          <span>{{ runWhat(d.run) }}, {{ d.run.trigger === 'Person' ? 'by ' : '' }}{{ triggerLabel(d.run) }}</span>
          <span class="muted">· <RelativeTime :value="d.run.startedAt ?? d.run.requestedAt" /></span>
          <span class="muted">· {{ runDuration(d.run, now) }}</span>
          <span v-if="d.run.exitCode !== null" class="muted mono">· exit {{ d.run.exitCode }}</span>
          <Tag v-if="d.run.takingLong" value="taking long" severity="warn" />
          <Tag v-if="d.run.dismissed" value="marked handled" severity="secondary" />
          <span class="spacer" />
          <Button
            v-if="canDismiss(d.run)"
            label="Mark handled"
            icon="pi pi-check"
            size="small"
            severity="secondary"
            @click="dismiss(d.run)"
          />
        </div>
        <Message
          v-if="d.run.message"
          :severity="d.run.state === 'Failed' || d.run.state === 'Refused' ? 'error' : 'secondary'"
          :closable="false"
          size="small"
          >{{ d.run.message }}</Message
        >
        <p v-if="d.run.command" class="small muted mono command">{{ d.run.command }}</p>
        <div v-if="d.summary" class="summary">
          <div v-if="d.summary.upgraded.length">
            <h4>Upgraded ({{ d.summary.upgraded.length }})</h4>
            <ul class="packages mono small">
              <li v-for="p in d.summary.upgraded" :key="`${p.name}:${p.arch}`">{{ change(p) }}</li>
            </ul>
          </div>
          <div v-if="d.summary.installed.length">
            <h4>Installed ({{ d.summary.installed.length }})</h4>
            <ul class="packages mono small">
              <li v-for="p in d.summary.installed" :key="`${p.name}:${p.arch}`">{{ change(p) }}</li>
            </ul>
          </div>
          <div v-if="d.summary.removed.length">
            <h4 class="bad">Removed ({{ d.summary.removed.length }})</h4>
            <ul class="packages mono small bad">
              <li v-for="p in d.summary.removed" :key="`${p.name}:${p.arch}`">{{ change(p) }}</li>
            </ul>
          </div>
          <div v-if="d.summary.configFilesKept.length">
            <h4>Configuration files kept ({{ d.summary.configFilesKept.length }})</h4>
            <p class="small muted note">The local file stays; the maintainer's version is next to it as .dpkg-dist.</p>
            <ul class="packages mono small">
              <li v-for="f in d.summary.configFilesKept" :key="f">{{ f }}</li>
            </ul>
          </div>
          <div v-if="d.summary.servicesToRestart.length">
            <h4>Services to restart ({{ d.summary.servicesToRestart.length }})</h4>
            <p class="small muted note">needrestart's list, only reported: PNMT restarts nothing.</p>
            <ul class="packages mono small">
              <li v-for="s in d.summary.servicesToRestart" :key="s">{{ s }}</li>
            </ul>
          </div>
          <div v-if="d.summary.errors.length || d.summary.warnings.length">
            <h4>Errors and warnings</h4>
            <ul class="packages mono small">
              <li v-for="e in d.summary.errors" :key="`e-${e}`" class="bad">{{ e }}</li>
              <li v-for="w in d.summary.warnings" :key="`w-${w}`" class="warn">{{ w }}</li>
            </ul>
          </div>
          <p class="small muted facts-line">
            <template v-if="d.summary.rebootRequired"><span class="warn">A reboot is required.</span> </template
            ><template v-if="d.summary.repositoriesReached || d.summary.repositoriesFailed"
              >{{ d.summary.repositoriesReached }} repositories reached, {{ d.summary.repositoriesFailed }} failed.
            </template>
            <template v-if="d.summary.fetched">{{ d.summary.fetched }}</template>
          </p>
        </div>
        <h4>Log</h4>
        <AptLog :sensor-id="sensorId" :run="d.run" height="50vh" @finished="finished" />
      </div>
      <p v-else-if="detail.isError.value" class="bad">The run could not be read.</p>
      <p v-else class="muted">Loading…</p>
    </Dialog>
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.runs {
  border: 0;
  border-radius: 0 0 var(--p-border-radius-md) var(--p-border-radius-md);
}

.chips {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.detail {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.summary {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(18rem, 1fr));
  gap: var(--pnmt-space-3);
}

.summary .facts-line {
  grid-column: 1 / -1;
}

h4 {
  margin: 0 0 var(--pnmt-space-1);
  font-size: 0.929rem;
}

.packages {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 14rem;
  overflow: auto;
  overflow-wrap: anywhere;
}

.note,
.command,
.facts-line {
  margin: 0 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}

.bad {
  color: var(--pnmt-fail);
}

.warn {
  color: var(--pnmt-warn);
}
</style>
