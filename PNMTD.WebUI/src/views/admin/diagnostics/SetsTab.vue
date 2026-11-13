<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import AssignDialog from './AssignDialog.vue'
import SetEditor from './SetEditor.vue'

/** Diagnostics sets (docs/windows-sensor-plan.md §10.3): the list, an editor, Assign…, and delete (refused while assigned). */
const table = useServerTable<S['DiagnosticSetItem']>({
  key: [...keys.diagnostics, 'sets'],
  fetch: (q) => api.diagnostics.sets(q),
  pageSize: 50,
})
const editing = ref(false)
const current = ref<S['DiagnosticSetDetail'] | null>(null)
const assigning = ref(false)
const assignSet = ref<string | null>(null)
function assign(item: S['DiagnosticSetItem']) {
  assignSet.value = item.id
  assigning.value = true
}
const { run, ask, done } = useActions()
async function edit(item: S['DiagnosticSetItem'] | null) {
  current.value = item ? ((await run(() => api.diagnostics.set(item.id))) ?? null) : null
  editing.value = true
}
async function remove(item: S['DiagnosticSetItem']) {
  if (!(await ask('Delete the set?', `${item.name} and its ${item.checks} checks.`))) return
  await done(() => api.diagnostics.deleteSet(item.id), { success: 'Deleted', invalidate: [keys.diagnostics] })
}
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <span class="spacer" />
      <Button label="New set" icon="pi pi-plus" size="small" @click="edit(null)" />
    </div>
    <ListTable :table="table" what="sets" empty-text="No diagnostics set yet: a set is a list of ping and DNS checks.">
      <Column header="Set">
        <template #body="{ data }">
          <a href="#" @click.prevent="edit(data)">{{ data.name }}</a>
          <div class="muted small">{{ data.description }}</div>
        </template>
      </Column>
      <Column header="Checks" field="checks" style="width: 6rem" />
      <Column header="Assigned to" style="width: 8rem">
        <template #body="{ data }">{{ data.assignments }} servers</template>
      </Column>
      <Column header="Changed" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.updatedAt" /></template>
      </Column>
      <Column style="width: 12rem">
        <template #body="{ data }">
          <Button label="Assign…" size="small" text @click="assign(data)" />
          <Button
            label="Delete"
            size="small"
            text
            severity="danger"
            :disabled="data.assignments > 0"
            @click="remove(data)"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <a href="#" @click.prevent="edit(item)">{{ item.name }}</a>
        <div class="small muted">{{ item.checks }} checks · {{ item.assignments }} servers</div>
      </template>
    </ListTable>
    <SetEditor v-model:visible="editing" :set="current" />
    <AssignDialog v-model:visible="assigning" :set-id="assignSet" />
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
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
