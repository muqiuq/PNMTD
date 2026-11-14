<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import RunScriptDialog from '@/views/admin/windows/RunScriptDialog.vue'
import ScriptRunsList from '@/views/admin/windows/ScriptRunsList.vue'

/**
 * A server's scripts (docs/windows-sensor-plan.md §7.2, §7.3): the scripts an administrator registered in the agent's window
 * there, with their account, schedule and task, each runnable from here; Run… for the library; and the server's runs. Admin.
 */
const props = defineProps<{ server: S['WindowsServerDetail'] }>()
const id = computed(() => props.server.server.sensorId)
const allowed = computed(() => props.server.capabilities.includes('scripts.v1'))
const local = useQuery({
  queryKey: computed(() => [...keys.windows, 'local-scripts', id.value]),
  queryFn: () => api.windows.localScripts(id.value),
  refetchInterval: 60_000,
})
const registered = computed(() => local.data.value?.scripts ?? [])
const mode = computed(() => {
  const l = local.data.value
  if (!l?.collectedAt) return null
  if (!l.allowed) return 'PNMT may not run scripts here.'
  if (!l.anyScript) return 'PNMT may run these scripts here, each as its own account, and nothing else.'
  return `PNMT may run these, and any script from the library as ${l.runAs.length ? l.runAs.join(', ') : 'any account but the service'}.`
})
const running = ref(false)
const localName = ref<string | null>(null)
const runDialog = (name: string | null) => {
  localName.value = name
  running.value = true
}
const result = (code: number | null | undefined) =>
  code === null || code === undefined ? '' : code === 0 ? 'ok' : code === 0x41301 ? 'running' : `result ${code}`

const { run } = useActions()
const addToLibrary = (l: S['WindowsLocalScript']) =>
  run(
    () =>
      api.windows.createScript({
        name: l.script.name,
        description: `Registered on ${props.server.server.hostname}.`,
        text: l.script.text!,
      }),
    { success: 'Added to the library', invalidate: [keys.windows] },
  )
</script>

<template>
  <div class="tab">
    <Message v-if="!allowed" severity="info" :closable="false" size="small">
      Scripts are not allowed on this server: an administrator turns them on in the agent's window there (Scripts from
      PNMT).
    </Message>
    <section class="panel">
      <div class="panel-header">
        Registered on this server
        <span v-if="local.data.value?.collectedAt" class="muted small header-note"
          >reported <RelativeTime :value="local.data.value.collectedAt"
        /></span>
      </div>
      <div class="panel-body">
        <p class="muted small intro">
          Scripts an administrator added in the agent's window there (Scripts, Add script…): a copy with its SHA-256,
          the account it runs as, and perhaps a schedule. {{ mode ?? '' }}
        </p>
        <p v-if="!local.data.value?.collectedAt && !local.isPending.value" class="muted">
          Not reported yet: an agent from before registered scripts, or one that hasn't reported since.
        </p>
        <DataTable v-else :value="registered" size="small" data-key="script.name">
          <template #empty><span class="muted">None registered.</span></template>
          <Column header="Script">
            <template #body="{ data }">
              <strong>{{ data.script.name }}</strong>
              <div class="muted small mono" :title="data.script.sha256">{{ data.script.sha256.slice(0, 16) }}…</div>
            </template>
          </Column>
          <Column header="Runs as" style="width: 14rem">
            <template #body="{ data }">{{ data.runAsName }}</template>
          </Column>
          <Column header="Schedule" style="width: 16rem">
            <template #body="{ data }">
              <template v-if="data.script.schedule">
                {{ data.script.schedule }}
                <div v-if="data.script.task" class="muted small">
                  <template v-if="data.script.task.lastRun"
                    >last <RelativeTime :value="data.script.task.lastRun" />
                    {{ result(data.script.task.lastResult) }}</template
                  >
                  <template v-else>not run yet</template>
                </div>
                <div v-else class="small warn-text">the task is missing</div>
              </template>
              <span v-else class="muted">on request</span>
            </template>
          </Column>
          <Column header="State" style="width: 12rem">
            <template #body="{ data }">
              <div class="tag-line">
                <Tag v-if="!data.script.matches" value="file changed" severity="danger" />
                <Tag v-else-if="data.script.parsed?.errors.length" value="syntax errors" severity="danger" />
                <Tag v-else value="ok" severity="success" />
                <Tag v-if="data.libraryScriptName" :value="`library: ${data.libraryScriptName}`" severity="secondary" />
              </div>
            </template>
          </Column>
          <Column style="width: 15rem">
            <template #body="{ data }">
              <div class="actions">
                <Button
                  label="Run…"
                  icon="pi pi-play"
                  size="small"
                  :disabled="!allowed || !data.script.matches || !data.script.parsed"
                  @click="runDialog(data.script.name)"
                />
                <Button
                  v-if="!data.libraryScriptId && data.script.text"
                  v-tooltip.top="'A copy in PNMT\'s library: it runs on other servers that allow it'"
                  label="To library"
                  size="small"
                  severity="secondary"
                  @click="addToLibrary(data)"
                />
              </div>
            </template>
          </Column>
        </DataTable>
      </div>
    </section>
    <div class="toolbar">
      <span class="muted small">From PNMT's library:</span>
      <span class="spacer" />
      <Button label="Run a script…" icon="pi pi-play" size="small" :disabled="!allowed" @click="runDialog(null)" />
    </div>
    <ScriptRunsList :sensor-id="server.server.sensorId" />
    <RunScriptDialog
      v-model:visible="running"
      :script-id="null"
      :sensor-id="server.server.sensorId"
      :local-name="localName"
    />
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
  align-items: center;
}

.spacer {
  flex: 1;
}

.intro {
  margin: 0 0 var(--pnmt-space-2);
}

.header-note {
  margin-left: var(--pnmt-space-2);
  font-weight: normal;
}

.tag-line,
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.warn-text {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}
</style>
