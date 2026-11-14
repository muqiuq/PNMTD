<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import RunScriptDialog from './RunScriptDialog.vue'
import ScriptEditor from './ScriptEditor.vue'
import ScriptRunsList from './ScriptRunsList.vue'
import { languageLabel, languages, parseSeverity, runnable } from './scriptFormat'

/**
 * Scripts (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8): the library of both languages with a language filter,
 * each script's parse state, the editor (a new script per language, from its template), Run…, and every run. PowerShell runs on
 * Windows servers where an administrator allowed scripts in the agent's window; bash on Linux servers whose config.yaml allows
 * them, as the accounts it names.
 */
const language = ref<S['ScriptLanguage'] | null>(null)
const filters = computed(() => ({ language: language.value ?? undefined }))
const table = useServerTable<S['AgentScriptItem']>({
  key: [...keys.agents, 'scripts'],
  fetch: (q) => api.agents.scripts(q),
  pageSize: 50,
  filters,
})
const languageOptions = languages.map((l) => ({ label: languageLabel(l), value: l }))
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
      <Select
        v-model="language"
        :options="languageOptions"
        option-label="label"
        option-value="value"
        show-clear
        size="small"
        placeholder="Every language"
        aria-label="Language"
      />
      <span class="muted small hint"
        >PowerShell for Windows servers, bash for Linux servers. They run only where allowed: in the agent's window on
        Windows, in /etc/pnmt-agent/config.yaml on Linux.</span
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
      <Column header="Language" style="width: 8rem">
        <template #body="{ data }">
          <span class="line"
            ><i :class="data.language === 'Bash' ? 'pi pi-server' : 'pi pi-microsoft'" aria-hidden="true" />{{
              languageLabel(data.language)
            }}</span
          >
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
          <Button label="Run…" size="small" text :disabled="!runnable(data)" @click="runIt(data.id)" />
          <Button icon="pi pi-trash" size="small" text severity="danger" aria-label="Delete" @click="remove(data)" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <a href="#" @click.prevent="edit(item.id)">{{ item.name }}</a>
        <div class="small muted">
          {{ languageLabel(item.language) }} · v{{ item.currentVersion }} · {{ item.parseState }}
        </div>
      </template>
    </ListTable>
    <section class="panel">
      <div class="panel-header">Runs</div>
      <div class="panel-body"><ScriptRunsList /></div>
    </section>
    <ScriptEditor v-model:visible="editing" :script-id="scriptId" :new-language="language" @run="runIt" />
    <RunScriptDialog v-model:visible="running" :script-id="runScriptId" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar,
.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.spacer {
  flex: 1;
}

.hint {
  flex: 1 1 20rem;
}

.toolbar :deep(.p-button) {
  white-space: nowrap;
  flex: none;
}

.small {
  font-size: 0.857rem;
}
</style>
