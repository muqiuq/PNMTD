<script setup lang="ts">
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import DiffView from '@/components/DiffView.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { lineDiff } from '@/components/markdown/lineDiff'
import { useActions } from '@/composables/useActions'
import { formatValue, isEmpty, type FieldDefinition } from './fieldValues'

/**
 * A location's history (docs/locations-knowledge-plan.md §3.5), newest first: its creation, renames and descriptions,
 * moves, every field's value with the old and the new, and hosts added or removed. A Markdown change shows its diff;
 * restoring an old value is a new change (Users).
 */
const props = defineProps<{
  location: S['LocationDetail']
  fields: readonly FieldDefinition[]
  canWrite: boolean
}>()

const page = ref(1)
const query = useQuery({
  queryKey: computed(() => [...keys.location(props.location.id), 'history', page.value]),
  queryFn: () => api.locations.history(props.location.id, { page: page.value, pageSize: 50 }),
  placeholderData: keepPreviousData,
})
const rows = computed(() => query.data.value?.items ?? [])
const pages = computed(() => Math.max(1, Math.ceil((query.data.value?.totalCount ?? 0) / 50)))
const fieldsById = computed(() => new Map(props.fields.map((f) => [f.id, f])))
const open = ref<Record<string, boolean>>({})

type Row = S['LocationHistoryItem']
const asText = (v: unknown) => (typeof v === 'string' ? v : '')
const fieldOf = (row: Row) =>
  (row.fieldId && fieldsById.value.get(row.fieldId)) ||
  ({ type: row.fieldType ?? 'Text', options: null, label: row.fieldLabel ?? 'A field' } as Pick<
    FieldDefinition,
    'type' | 'options' | 'label'
  >)
const shown = (row: Row, v: unknown) => (isEmpty(v) ? 'empty' : formatValue(fieldOf(row), v))
const diff = (row: Row) => lineDiff(asText(row.oldValue), asText(row.newValue))
const lines = (row: Row) => {
  const d = diff(row)
  return `+${d.added} −${d.removed} lines`
}

function describe(row: Row): string {
  switch (row.kind) {
    case 'Created':
      return row.detail ? `Created as ${row.detail}` : 'Created'
    case 'Renamed':
      return `Renamed from ${asText(row.oldValue)} to ${asText(row.newValue)}`
    case 'DescriptionChanged':
      return isEmpty(row.newValue) ? 'Description removed' : `Description: ${asText(row.newValue)}`
    case 'Moved':
      return `Moved from ${asText(row.oldValue)} to ${asText(row.newValue)}`
    case 'HostsAdded':
      return `Hosts added: ${row.detail ?? ''}`
    case 'HostsRemoved':
      return `Hosts removed: ${row.detail ?? ''}`
    default:
      return row.fieldLabel ?? 'A field'
  }
}

const queryClient = useQueryClient()
const { run, ask } = useActions()
async function restore(row: Row) {
  const label = row.fieldLabel ?? 'the field'
  if (
    !(await ask(
      `Restore ${label}?`,
      `${label} goes back to its value from before this change. That is a new change of its own.`,
      'Restore',
    ))
  ) {
    return
  }
  const current = props.location.values.find((v) => v.fieldId === row.fieldId)
  const saved = await run(
    () =>
      api.locations.setValue(props.location.id, row.fieldId!, {
        value: row.oldValue as S['LocationValueRequest']['value'],
        revision: current?.revision ?? 0,
      }),
    { success: `${label} restored`, failure: 'Not restored', invalidate: [keys.locations] },
  )
  if (saved) {
    await queryClient.invalidateQueries({ queryKey: keys.location(props.location.id) })
  }
}
</script>

<template>
  <section class="panel history" aria-label="History">
    <ErrorState
      v-if="query.isError.value && !query.data.value"
      :error="query.error.value"
      what="the history"
      @retry="query.refetch()"
    />
    <div v-else-if="!query.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <EmptyState v-else-if="!rows.length" icon="pi pi-history" title="No changes yet." />
    <ol v-else class="rows">
      <li v-for="row in rows" :key="row.id">
        <div class="line">
          <i
            :class="
              row.kind === 'ValueChanged'
                ? 'pi pi-pencil'
                : row.kind === 'Moved'
                  ? 'pi pi-arrows-alt'
                  : row.kind.startsWith('Hosts')
                    ? 'pi pi-server'
                    : 'pi pi-map-marker'
            "
            class="muted"
            aria-hidden="true"
          />
          <span class="what">
            <span class="strong">{{ describe(row) }}</span>
            <template v-if="row.kind === 'ValueChanged'">
              <template v-if="row.fieldType === 'Markdown'">
                <span class="muted"> · {{ lines(row) }}</span>
              </template>
              <template v-else>
                <span class="muted">: </span>
                <span class="old">{{ shown(row, row.oldValue) }}</span>
                <span class="muted"> → </span>
                <span>{{ shown(row, row.newValue) }}</span>
              </template>
            </template>
          </span>
          <span class="who muted">{{ row.byName }} · <RelativeTime :value="row.at" /></span>
        </div>
        <div v-if="row.kind === 'ValueChanged'" class="row-actions">
          <Button
            v-if="row.fieldType === 'Markdown'"
            :label="open[row.id] ? 'Hide changes' : 'Show changes'"
            size="small"
            text
            @click="open[row.id] = !open[row.id]"
          />
          <Button
            v-if="canWrite && row.fieldId && fieldsById.has(row.fieldId)"
            label="Restore the old value"
            size="small"
            text
            severity="secondary"
            @click="restore(row)"
          />
        </div>
        <DiffView v-if="open[row.id]" :diff="diff(row)" class="diff" />
      </li>
    </ol>
    <div v-if="pages > 1" class="pager">
      <Button icon="pi pi-angle-left" text size="small" :disabled="page <= 1" aria-label="Newer" @click="page--" />
      <span class="muted">{{ page }} / {{ pages }}</span>
      <Button icon="pi pi-angle-right" text size="small" :disabled="page >= pages" aria-label="Older" @click="page++" />
    </div>
  </section>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.line {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.what {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.strong {
  font-weight: 500;
}

.old {
  text-decoration: line-through;
  color: var(--pnmt-muted);
}

.who {
  white-space: nowrap;
  font-size: 0.857rem;
}

.row-actions {
  display: flex;
  gap: var(--pnmt-space-1);
  padding-left: 1.4rem;
}

.diff {
  margin: var(--pnmt-space-2) 0 0 1.4rem;
}

.pager {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2);
}

@media (max-width: 599px) {
  .line {
    flex-wrap: wrap;
  }
}
</style>
