<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import FieldCard from '@/views/locations/FieldCard.vue'
import { sampleTarget, sampleValue, typeIcon, typeLabel, type FieldDefinition } from '@/views/locations/fieldValues'
import { useLocationFields } from '@/views/locations/useLocations'
import FieldDialog from './FieldDialog.vue'
import TypicalFieldsDialog from './TypicalFieldsDialog.vue'

/**
 * *Admin → Location fields* (docs/locations-knowledge-plan.md §4, §10.5): one set of fields for every location, in order
 * (drag the rows), each with its type, group, and whether it is on the overview, inherited and sensitive, and how many
 * locations filled it. A dialog per field with a preview, *Add typical fields…*, archive and restore, and deleting an
 * archived field with its values. Beside the list, a location's overview as the current fields show it.
 */
const { query, active } = useLocationFields()
const archived = computed(() => (query.data.value?.items ?? []).filter((f) => f.archivedAt))
const groups = computed(() => [
  ...new Set((query.data.value?.items ?? []).map((f) => f.group).filter((g): g is string => !!g)),
])

// The order as shown, while a drag is saved.
const rows = ref<FieldDefinition[]>([])
watch(active, (list) => (rows.value = [...list]), { immediate: true })

const editing = ref<FieldDefinition | null>(null)
const dialog = ref(false)
const typical = ref(false)
const showArchived = ref(false)
function edit(field: FieldDefinition | null) {
  editing.value = field
  dialog.value = true
}

const { run, done, ask } = useActions()
async function reorder(event: { value: FieldDefinition[] }) {
  rows.value = event.value
  await run(() => api.locationFields.order(event.value.map((f) => f.id)), {
    failure: 'Order not saved',
    invalidate: [keys.locationFields, keys.locations],
  })
}
const archive = (f: FieldDefinition) =>
  done(() => api.locationFields.archive(f.id), {
    success: `${f.label} archived`,
    invalidate: [keys.locationFields, keys.locations],
  })
const restore = (f: FieldDefinition) =>
  done(() => api.locationFields.restore(f.id), {
    success: `${f.label} restored`,
    invalidate: [keys.locationFields, keys.locations],
  })
async function remove(f: FieldDefinition) {
  const values = f.filled === 1 ? '1 location has a value' : `${f.filled} locations have a value`
  if (
    await ask(
      `Delete ${f.label}?`,
      `${values}. The field, its values and their history are deleted. This cannot be undone.`,
    )
  ) {
    await done(() => api.locationFields.remove(f.id), {
      success: `${f.label} deleted`,
      invalidate: [keys.locationFields, keys.locations],
    })
  }
}
const menu = (f: FieldDefinition) => [
  { label: 'Edit…', icon: 'pi pi-pencil', command: () => edit(f) },
  { label: 'Archive', icon: 'pi pi-box', command: () => archive(f) },
]

// An example location's overview: Cage B, part of Zurich DC, with a value in every field (§10.5).
const overview = computed(() => active.value.filter((f) => f.overview))
const example = (f: FieldDefinition): S['LocationValueItem'] => ({
  fieldId: f.id,
  value: sampleValue(f),
  inheritedFrom: f.inherited ? { id: 'example', name: 'Zurich DC' } : null,
  revision: 0,
  updatedAt: null,
  updatedByName: null,
  target: sampleTarget(f.type),
})
</script>

<template>
  <div class="page">
    <PageHeader title="Location fields">
      <template #status>
        <span class="muted">{{ active.length }}</span>
      </template>
      <template #actions>
        <Button
          label="Add typical fields…"
          icon="pi pi-list-check"
          size="small"
          severity="secondary"
          outlined
          :disabled="!query.data.value"
          @click="typical = true"
        />
        <Button label="New field" icon="pi pi-plus" size="small" @click="edit(null)" />
      </template>
    </PageHeader>
    <ErrorState
      v-if="query.isError.value && !query.data.value"
      :error="query.error.value"
      what="the fields"
      @retry="query.refetch()"
    />
    <div v-else-if="!query.data.value" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <div v-else class="layout">
      <div class="lists">
        <DataTable :value="rows" data-key="id" size="small" class="panel" @row-reorder="reorder">
          <Column row-reorder style="width: 2.5rem" :pt="{ rowReorderIcon: { 'aria-label': 'Drag to reorder' } }" />
          <Column header="Field">
            <template #body="{ data }">
              <button type="button" class="link-button strong" @click="edit(data)">{{ data.label }}</button>
              <div v-if="data.help" class="muted small">{{ data.help }}</div>
            </template>
          </Column>
          <Column header="Type" style="width: 9rem">
            <template #body="{ data }">
              <span class="type"
                ><i :class="typeIcon(data.type)" class="muted" aria-hidden="true" /> {{ typeLabel(data.type) }}</span
              >
            </template>
          </Column>
          <Column header="Group" style="width: 8rem">
            <template #body="{ data }"
              ><span class="muted">{{ data.group }}</span></template
            >
          </Column>
          <Column header="Overview" style="width: 6rem">
            <template #body="{ data }"
              ><i v-if="data.overview" class="pi pi-check" aria-label="On the overview"
            /></template>
          </Column>
          <Column header="Inherited" style="width: 6rem">
            <template #body="{ data }"><i v-if="data.inherited" class="pi pi-check" aria-label="Inherited" /></template>
          </Column>
          <Column header="Sensitive" style="width: 6rem">
            <template #body="{ data }"><i v-if="data.sensitive" class="pi pi-lock" aria-label="Sensitive" /></template>
          </Column>
          <Column header="Filled" style="width: 7rem">
            <template #body="{ data }">{{ data.filled }} location{{ data.filled === 1 ? '' : 's' }}</template>
          </Column>
          <Column style="width: 3rem">
            <template #body="{ data }"
              ><MoreMenu :items="menu(data)" :label="`More actions for ${data.label}`"
            /></template>
          </Column>
          <template #empty>
            <EmptyState title="No fields." text="Add one, or some typical ones." />
          </template>
        </DataTable>

        <section v-if="archived.length" class="panel" aria-labelledby="archived-title">
          <button
            id="archived-title"
            type="button"
            class="panel-header toggle"
            :aria-expanded="showArchived"
            @click="showArchived = !showArchived"
          >
            <i :class="showArchived ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
            Archived ({{ archived.length }})
            <span class="muted small">Hidden everywhere, their values kept.</span>
          </button>
          <ul v-if="showArchived" class="archived">
            <li v-for="f in archived" :key="f.id">
              <i :class="typeIcon(f.type)" class="muted" aria-hidden="true" />
              <span class="strong">{{ f.label }}</span>
              <span class="muted small"
                >{{ f.filled }} location{{ f.filled === 1 ? '' : 's' }} · archived <RelativeTime :value="f.archivedAt"
              /></span>
              <span class="spacer" />
              <Button label="Restore" size="small" text @click="restore(f)" />
              <Button label="Delete…" size="small" text severity="danger" @click="remove(f)" />
            </li>
          </ul>
        </section>
      </div>
      <aside class="preview" aria-labelledby="preview-title">
        <div id="preview-title" class="preview-title muted">A location's overview</div>
        <div class="panel">
          <header class="panel-header">Cage B <span class="muted small">Zurich DC › Cage B</span></header>
          <EmptyState v-if="!overview.length" title="No field is on the overview." />
          <FieldCard v-for="f in overview" :key="f.id" :field="f" :value="example(f)" preview />
        </div>
      </aside>
    </div>
    <FieldDialog v-model:visible="dialog" :field="editing" :groups="groups" />
    <TypicalFieldsDialog v-if="query.data.value" v-model:visible="typical" :typical="query.data.value.typical" />
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.layout {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(16rem, 1fr);
  gap: var(--pnmt-space-3);
  align-items: start;
}

.lists {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.panel {
  overflow: hidden;
}

.strong {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}

.type {
  white-space: nowrap;
}

.link-button {
  all: unset;
  cursor: pointer;
  color: var(--p-primary-color);
}

.link-button:hover {
  text-decoration: underline;
}

.toggle {
  all: unset;
  box-sizing: border-box;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  cursor: pointer;
}

.archived {
  list-style: none;
  margin: 0;
  padding: 0;
}

.archived li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) var(--pnmt-space-3);
  border-top: 1px solid var(--pnmt-border);
}

.spacer {
  flex: 1;
}

.preview {
  position: sticky;
  top: calc(var(--pnmt-header-height) + var(--pnmt-space-3));
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.preview-title {
  font-size: 0.786rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  font-weight: 600;
}

@media (max-width: 1199px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }

  .preview {
    position: static;
  }
}
</style>
