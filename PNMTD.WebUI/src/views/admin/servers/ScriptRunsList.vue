<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useCopy } from '@/composables/useCopy'
import { useServerTable } from '@/composables/useServerTable'
import { runAccount, runStateSeverity, versionLabel } from './scriptFormat'

/** The scripts' runs (docs/windows-sensor-plan.md §7.3): who ran what where, as whom, with the output; arguments can be redacted. */
const props = defineProps<{ sensorId?: string }>()
const filters = computed(() => ({ sensorId: props.sensorId }))
const table = useServerTable<S['AgentScriptRunItem']>({
  key: [...keys.agents, 'script-runs', props.sensorId ?? 'all'],
  fetch: (q) => api.agents.scriptRuns(q),
  pageSize: 50,
  filters,
  refetchInterval: 5000,
})
const selected = ref<string | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const detail = useQuery({
  queryKey: computed(() => [...keys.agents, 'script-run', selected.value]),
  queryFn: () => api.agents.scriptRun(selected.value!),
  enabled: computed(() => selected.value !== null),
  refetchInterval: (q) => (q.state.data?.run.finishedAt ? false : 2000),
})
const { run, ask } = useActions()
const copy = useCopy()
/** A registered script's account as the server names it; a library run's account by its name in PNMT and the account. */
const runAsText = (r: S['AgentScriptRunItem']) =>
  r.versionNumber === 0 ? r.runAsName : `${r.runAsName} (${runAccount(r)})`
async function redact() {
  if (!(await ask('Redact the arguments?', 'They are removed from this run for good.', 'Redact'))) return
  await run(() => api.agents.redactRun(selected.value!), { success: 'Redacted', invalidate: [keys.agents] })
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
          <template v-if="data.function">{{ data.function }} in </template>{{ data.scriptName }}
          <span class="muted small">{{
            data.connector ? `revision ${data.versionNumber}` : versionLabel(data.versionNumber)
          }}</span>
          <div v-if="!sensorId" class="small muted">{{ data.serverName }}</div>
        </template>
      </Column>
      <Column header="As" style="width: 12rem">
        <template #body="{ data }"
          ><span class="small">{{ runAccount(data) }}</span></template
        >
      </Column>
      <Column header="By" style="width: 9rem">
        <template #body="{ data }">
          <!-- docs/connectors-plan.md §13.6: a connector's call, with a link to the connector. -->
          <RouterLink
            v-if="data.connector"
            class="small"
            :to="{ name: 'connector', params: { id: data.connector.id } }"
            @click.stop
            >connector {{ data.connector.name }}</RouterLink
          >
          <span v-else class="small">{{ data.requestedBy }}</span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="small">
          <Tag :value="item.state" :severity="runStateSeverity(item.state)" /> {{ item.scriptName }}
        </div>
        <div class="small muted">{{ item.serverName }} · <RelativeTime :value="item.startedAt" /></div>
      </template>
    </ListTable>
    <Dialog
      v-model:visible="open"
      header="Script run"
      modal
      maximizable
      dismissable-mask
      :style="{ width: '90vw', maxWidth: '80rem' }"
      :breakpoints="{ '700px': '100vw' }"
    >
      <template v-if="detail.data.value">
        <div class="line">
          <Tag :value="detail.data.value.run.state" :severity="runStateSeverity(detail.data.value.run.state)" />
          <strong>{{ detail.data.value.run.scriptName }}</strong>
          <span class="muted">{{ versionLabel(detail.data.value.run.versionNumber) }}</span>
          <span>on {{ detail.data.value.run.serverName }}</span>
        </div>
        <dl class="facts run-facts">
          <dt>As</dt>
          <dd>{{ runAsText(detail.data.value.run) }}</dd>
          <dt>By</dt>
          <dd>{{ detail.data.value.run.requestedBy ?? '–' }}</dd>
          <dt>Started</dt>
          <dd><RelativeTime :value="detail.data.value.run.startedAt" /></dd>
          <dt>Took</dt>
          <dd>
            <template v-if="detail.data.value.run.durationMs !== null"
              >{{ Math.round(detail.data.value.run.durationMs) }} ms</template
            >
            <template v-else>{{ detail.data.value.run.finishedAt ? '–' : 'running…' }}</template>
          </dd>
          <dt>Exit code</dt>
          <dd>{{ detail.data.value.run.exitCode ?? '–' }}</dd>
          <dt>SHA-256</dt>
          <dd class="mono small hash">{{ detail.data.value.hash }}</dd>
        </dl>
        <Message v-if="detail.data.value.run.message" severity="warn" :closable="false" size="small">{{
          detail.data.value.run.message
        }}</Message>
        <div class="section-head">
          <h4>Output</h4>
          <Button
            v-if="detail.data.value.output"
            label="Copy"
            icon="pi pi-copy"
            size="small"
            text
            @click="copy(detail.data.value.output, 'The output')"
          />
        </div>
        <pre class="box output mono">{{
          detail.data.value.output ?? (detail.data.value.run.finishedAt ? '(none)' : 'Running…')
        }}</pre>
        <div class="section-head">
          <h4>Arguments</h4>
          <Button
            v-if="!detail.data.value.run.redacted && detail.data.value.arguments"
            label="Redact the arguments"
            size="small"
            text
            severity="danger"
            @click="redact"
          />
        </div>
        <p v-if="detail.data.value.run.redacted" class="muted">Redacted.</p>
        <pre v-else class="box mono">{{ detail.data.value.arguments ?? 'none' }}</pre>
      </template>
    </Dialog>
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
  border: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  max-height: 30vh;
  overflow: auto;
  font-size: 0.857rem;
  margin: 0;
}

/* The output keeps its columns (tables, aligned numbers): no wrapping, scrolled sideways when wider. */
.box.output {
  white-space: pre;
  min-height: 8rem;
  max-height: 60vh;
}

.run-facts {
  margin: var(--pnmt-space-3) 0;
}

.hash {
  word-break: break-all;
}

.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
}

.section-head h4 {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
