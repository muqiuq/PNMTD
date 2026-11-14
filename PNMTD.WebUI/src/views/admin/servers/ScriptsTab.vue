<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import RunScriptDialog from './RunScriptDialog.vue'
import ScriptEditor from './ScriptEditor.vue'
import ScriptRunsList from './ScriptRunsList.vue'
import { parseSeverity } from './scriptFormat'

/**
 * Scripts (docs/windows-sensor-plan.md §7): the library with each script's parse state, the editor, Run…, and every run. They
 * run only on servers where an administrator allowed scripts in the agent's window.
 */
const table = useServerTable<S['AgentScriptItem']>({
  key: [...keys.agents, 'scripts'],
  fetch: (q) => api.agents.scripts(q),
  pageSize: 50,
})
const editing = ref(false)
const scriptId = ref<string | null>(null)
const running = ref(false)
const runScriptId = ref<string | null>(null)
function edit(id: string | null) {
  scriptId.value = id
  editing.value = true
}
function runIt(id: string | null) {
  runScriptId.value = id
  editing.value = false
  running.value = true
}
const { ask, done } = useActions()
async function remove(item: S['AgentScriptItem']) {
  if (
    !(await ask(
      'Delete the script?',
      `${item.name} and its ${item.currentVersion} versions; its runs stay in the history.`,
    ))
  )
    return
  await done(() => api.agents.deleteScript(item.id), { success: 'Deleted', invalidate: [keys.agents] })
}
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <span class="muted small"
        >PowerShell scripts for Windows servers. They run only where an administrator allowed them in the agent's window
        on the server.</span
      >
      <span class="spacer" />
      <Button label="Run…" icon="pi pi-play" size="small" severity="secondary" @click="runIt(null)" />
      <Button label="New script" icon="pi pi-plus" size="small" @click="edit(null)" />
    </div>
    <ListTable :table="table" what="scripts" empty-text="No script yet: a new one starts from a template.">
      <Column header="Script">
        <template #body="{ data }">
          <a href="#" @click.prevent="edit(data.id)">{{ data.name }}</a>
          <div class="muted small">{{ data.description }}</div>
        </template>
      </Column>
      <Column header="Version" style="width: 10rem">
        <template #body="{ data }">
          v{{ data.currentVersion }}
          <Tag :value="data.parseState.toLowerCase()" :severity="parseSeverity(data.parseState)" />
        </template>
      </Column>
      <Column header="Parameters" field="parameters" style="width: 7rem" />
      <Column header="Warnings" style="width: 7rem">
        <template #body="{ data }"
          ><Tag v-if="data.findings" :value="String(data.findings)" severity="warn"
        /></template>
      </Column>
      <Column header="Changed" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.updatedAt" /></template>
      </Column>
      <Column style="width: 10rem">
        <template #body="{ data }">
          <Button label="Run…" size="small" text :disabled="data.parseState !== 'Parsed'" @click="runIt(data.id)" />
          <Button icon="pi pi-trash" size="small" text severity="danger" aria-label="Delete" @click="remove(data)" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <a href="#" @click.prevent="edit(item.id)">{{ item.name }}</a>
        <div class="small muted">v{{ item.currentVersion }} · {{ item.parseState }}</div>
      </template>
    </ListTable>
    <section class="panel">
      <div class="panel-header">Runs</div>
      <div class="panel-body"><ScriptRunsList /></div>
    </section>
    <ScriptEditor v-model:visible="editing" :script-id="scriptId" @run="runIt" />
    <RunScriptDialog v-model:visible="running" :script-id="runScriptId" />
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
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
