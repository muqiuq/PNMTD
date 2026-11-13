<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Drawer from 'primevue/drawer'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { runStateSeverity } from './scriptFormat'

/** The scripts' runs (docs/windows-sensor-plan.md §7.3): who ran what where, as whom, with the output; arguments can be redacted. */
const props = defineProps<{ sensorId?: string }>()
const filters = computed(() => ({ sensorId: props.sensorId }))
const table = useServerTable<S['WindowsScriptRunItem']>({
  key: [...keys.windows, 'script-runs', props.sensorId ?? 'all'],
  fetch: (q) => api.windows.scriptRuns(q),
  pageSize: 50,
  filters,
  refetchInterval: 5000,
})
const selected = ref<string | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const detail = useQuery({
  queryKey: computed(() => [...keys.windows, 'script-run', selected.value]),
  queryFn: () => api.windows.scriptRun(selected.value!),
  enabled: computed(() => selected.value !== null),
  refetchInterval: (q) => (q.state.data?.run.finishedAt ? false : 2000),
})
const { run, ask } = useActions()
async function redact() {
  if (!(await ask('Redact the arguments?', 'They are removed from this run for good.', 'Redact'))) return
  await run(() => api.windows.redactRun(selected.value!), { success: 'Redacted', invalidate: [keys.windows] })
}
</script>

<template>
  <div class="tab">
    <ListTable :table="table" what="runs" selectable empty-text="No script run yet." @select="(r) => (selected = r.id)">
      <Column header="When" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.startedAt" /></template>
      </Column>
      <Column header="State" style="width: 9rem">
        <template #body="{ data }">
          <Tag :value="data.state" :severity="runStateSeverity(data.state)" />
          <span v-if="data.exitCode !== null" class="mono small"> {{ data.exitCode }}</span>
        </template>
      </Column>
      <Column header="Script">
        <template #body="{ data }">
          {{ data.scriptName }} <span class="muted small">v{{ data.versionNumber }}</span>
          <div v-if="!sensorId" class="small muted">{{ data.serverName }}</div>
        </template>
      </Column>
      <Column header="As" style="width: 12rem">
        <template #body="{ data }"
          ><span class="small">{{ data.account ?? 'LocalSystem' }}</span></template
        >
      </Column>
      <Column header="By" style="width: 9rem">
        <template #body="{ data }"
          ><span class="small">{{ data.requestedBy }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="small">
          <Tag :value="item.state" :severity="runStateSeverity(item.state)" /> {{ item.scriptName }}
        </div>
        <div class="small muted">{{ item.serverName }} · <RelativeTime :value="item.startedAt" /></div>
      </template>
    </ListTable>
    <Drawer v-model:visible="open" position="right" header="Script run" class="script-run-drawer">
      <template v-if="detail.data.value">
        <p class="line">
          <Tag :value="detail.data.value.run.state" :severity="runStateSeverity(detail.data.value.run.state)" />
          <strong>{{ detail.data.value.run.scriptName }}</strong> v{{ detail.data.value.run.versionNumber }} on
          {{ detail.data.value.run.serverName }}
        </p>
        <p class="small muted">
          as {{ detail.data.value.run.account ?? 'LocalSystem' }} ({{ detail.data.value.run.runAsName }}) · by
          {{ detail.data.value.run.requestedBy }} · <RelativeTime :value="detail.data.value.run.startedAt" />
          <template v-if="detail.data.value.run.durationMs !== null">
            · {{ Math.round(detail.data.value.run.durationMs) }} ms</template
          >
          <template v-if="detail.data.value.run.exitCode !== null">
            · exit code {{ detail.data.value.run.exitCode }}</template
          >
        </p>
        <p v-if="detail.data.value.run.message">{{ detail.data.value.run.message }}</p>
        <h4>Arguments</h4>
        <p v-if="detail.data.value.run.redacted" class="muted">Redacted.</p>
        <pre v-else class="box mono">{{ detail.data.value.arguments ?? 'none' }}</pre>
        <Button
          v-if="!detail.data.value.run.redacted && detail.data.value.arguments"
          label="Redact the arguments"
          size="small"
          text
          severity="danger"
          @click="redact"
        />
        <h4>Output</h4>
        <pre class="box mono">{{
          detail.data.value.output ?? (detail.data.value.run.finishedAt ? '(none)' : 'Running…')
        }}</pre>
        <p class="small muted mono">{{ detail.data.value.hash }}</p>
      </template>
    </Drawer>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.box {
  white-space: pre-wrap;
  background: var(--pnmt-bg);
  padding: var(--pnmt-space-2);
  border-radius: var(--p-border-radius-md);
  max-height: 50vh;
  overflow: auto;
  font-size: 0.857rem;
}

.small {
  font-size: 0.857rem;
}
</style>

<style>
.script-run-drawer {
  width: min(48rem, 100vw);
}
</style>
