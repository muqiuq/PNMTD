<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import { ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import InlineField from '@/components/InlineField.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'

/**
 * Key values (§5.6): settings and the tasks' own records. Values are edited in place; the tasks'
 * entries are read-only (they write them again), known keys carry a description.
 */
const queryClient = useQueryClient()
const { run, ask } = useActions()
const table = useServerTable<S['KeyValueItem']>({
  key: keys.keyValues,
  fetch: (q) => api.keyValues.list(q),
  syncUrl: true,
  pageSize: 100,
})

async function save(item: S['KeyValueItem'], patch: Partial<S['KeyValueRequest']>) {
  await api.keyValues.update(item.id, {
    key: item.key,
    value: item.value,
    note: item.note,
    enabled: item.enabled,
    ...patch,
  })
  await queryClient.invalidateQueries({ queryKey: keys.keyValues })
}

const newKey = ref('')
const newValue = ref('')
const newNote = ref('')
const addError = ref<string | null>(null)
async function add() {
  addError.value = newKey.value.trim() ? null : 'A key is needed.'
  if (addError.value) return
  try {
    await api.keyValues.create({ key: newKey.value.trim(), value: newValue.value, note: newNote.value, enabled: true })
    newKey.value = newValue.value = newNote.value = ''
    await queryClient.invalidateQueries({ queryKey: keys.keyValues })
  } catch (e) {
    addError.value = e instanceof ApiError ? (e.field('key') ?? e.message) : String(e)
  }
}

async function remove(item: S['KeyValueItem']) {
  const ok = await ask(
    `Delete ${item.key}?`,
    item.isReadOnly ? 'Its task writes it again on its next run.' : 'This cannot be undone.',
  )
  if (ok)
    await run(() => api.keyValues.remove(item.id), { success: `${item.key} deleted`, invalidate: [keys.keyValues] })
}
</script>

<template>
  <div class="page">
    <PageHeader title="Key values" />
    <FilterBar v-model:search="table.search.value" placeholder="Search key or value" />
    <form class="panel add-row" novalidate @submit.prevent="add">
      <InputText
        v-model="newKey"
        size="small"
        placeholder="new_key"
        class="mono key"
        aria-label="New key"
        :invalid="!!addError"
      />
      <InputText v-model="newValue" size="small" placeholder="Value" class="mono grow" aria-label="New value" />
      <InputText v-model="newNote" size="small" placeholder="Note" class="grow" aria-label="New note" />
      <Button type="submit" label="Add" icon="pi pi-plus" size="small" />
      <small v-if="addError" class="field-error full">{{ addError }}</small>
    </form>
    <ListTable :table="table" what="key values" :row-class="(k) => (k.enabled ? undefined : 'row-muted')">
      <Column field="key" header="Key" sortable style="width: 18rem">
        <template #body="{ data }">
          <span class="key-cell">
            <i v-if="data.isReadOnly" class="pi pi-lock muted" title="Written by a task" aria-label="read-only" />
            <code>{{ data.key }}</code>
          </span>
          <div v-if="data.description" class="muted description">{{ data.description }}</div>
        </template>
      </Column>
      <Column header="Value">
        <template #body="{ data }">
          <InlineField
            :value="data.value"
            label="Value"
            field="value"
            mono
            :readonly="data.isReadOnly"
            :save="(v) => save(data, { value: String(v ?? '') })"
          />
        </template>
      </Column>
      <Column header="Note">
        <template #body="{ data }">
          <InlineField
            :value="data.note"
            label="Note"
            field="note"
            :readonly="data.isReadOnly"
            :save="(v) => save(data, { note: String(v ?? '') })"
          />
        </template>
      </Column>
      <Column header="On" style="width: 4.5rem">
        <template #body="{ data }">
          <InlineField
            :value="data.enabled"
            type="bool"
            :label="`Enabled ${data.key}`"
            :readonly="data.isReadOnly"
            :save="(v) => save(data, { enabled: Boolean(v) })"
          />
        </template>
      </Column>
      <Column header="" style="width: 3rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Delete ${data.key}`"
            @click="remove(data)"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <code>{{ item.key }}</code>
        <div class="mono ellipsis">{{ item.value }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.add-row {
  display: flex;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2);
  flex-wrap: wrap;
  align-items: center;
}

.key {
  width: 16rem;
}

.grow {
  flex: 1;
  min-width: 8rem;
}

.full {
  flex-basis: 100%;
}

.mono {
  font-family: var(--pnmt-mono);
}

.key-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.description {
  font-size: 0.857rem;
}
</style>
