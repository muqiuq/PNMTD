<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Select from 'primevue/select'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  kindLabels,
  licenseKeys,
  licensesApi,
  typeLabels,
  typePlurals,
  type LicenseTemplate,
  type LicenseType,
} from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import { useActions } from '@/composables/useActions'
import { formatDay } from '@/format'
import { formatSort, parseSort } from './licenceList'
import TemplateDialog from './TemplateDialog.vue'
import { matchesTemplate, priceText, sortTemplates, templateSortFields, termText, type TemplateSort } from './templates'

/**
 * The templates (docs/licences-plan.md §8.6): sortable and searchable as the lists, the search, type and sort in the URL (?q, ?type,
 * ?sort). A row opens the template (read-only without Manage); *New template* (Manage), *Make an item from it* (Edit), *Delete*
 * (Manage: its items stay as they are).
 */
const props = defineProps<{ manage: boolean; edit: boolean }>()
const route = useRoute()
const router = useRouter()
const { done, ask } = useActions()

const one = (key: string) => (typeof route.query[key] === 'string' ? (route.query[key] as string) : '')
const types: LicenseType[] = ['License', 'Service', 'Hardware']
const search = ref(one('q'))
const q = ref(search.value)
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => (q.value = value), 250)
})
onBeforeUnmount(() => clearTimeout(timer))
const type = ref<LicenseType | null>(types.find((t) => t === one('type')) ?? null)
const sort = ref<TemplateSort | null>(parseSort(one('sort'), templateSortFields))
watch([q, type, sort], () => {
  const query = {
    ...route.query,
    q: q.value.trim() || undefined,
    type: type.value ?? undefined,
    sort: formatSort(sort.value),
  }
  void router.replace({ query })
})
// The tab's link (or a link here) sets the URL: the list follows.
watch(
  () => route.query,
  () => {
    if (route.params.tab !== 'templates') return
    if (one('q').trim() !== q.value.trim()) search.value = q.value = one('q')
    if ((types.find((t) => t === one('type')) ?? null) !== type.value)
      type.value = types.find((t) => t === one('type')) ?? null
    if (formatSort(parseSort(one('sort'), templateSortFields)) !== formatSort(sort.value))
      sort.value = parseSort(one('sort'), templateSortFields)
  },
)
function onSort(event: { sortField?: unknown; sortOrder?: number | null }) {
  const field = typeof event.sortField === 'string' ? event.sortField : ''
  sort.value =
    field && event.sortOrder ? parseSort(`${event.sortOrder === -1 ? '-' : ''}${field}`, templateSortFields) : null
}

const templates = useQuery({ queryKey: licenseKeys.templates, queryFn: licensesApi.templates })
const shown = computed(() =>
  sortTemplates(
    (templates.data.value ?? []).filter((t) => (!type.value || t.type === type.value) && matchesTemplate(t, q.value)),
    sort.value,
  ),
)
const typeOptions = types.map((t) => ({ value: t, label: typePlurals[t] }))

const dialog = ref(false)
const opened = ref<LicenseTemplate | null>(null)
function open(t: LicenseTemplate | null) {
  opened.value = t
  dialog.value = true
}
const makeItem = (t: LicenseTemplate) => router.push({ name: 'licence-new', query: { type: t.type, template: t.id } })

async function remove(t: LicenseTemplate) {
  const keep =
    t.followEndOfLife > 0 ? ` The ${t.followEndOfLife} that follow its end of life keep it as their own.` : ''
  const message = t.items
    ? `Its ${t.items} item${t.items === 1 ? '' : 's'} stay as they are.${keep}`
    : 'Nothing was made from it.'
  if (!(await ask(`Delete the template ${t.name}?`, message))) return
  await done(() => licensesApi.deleteTemplate(t.id), {
    success: `Template ${t.name} deleted`,
    failure: 'Not deleted',
    invalidate: [licenseKeys.all],
  })
}
</script>

<template>
  <ErrorState
    v-if="templates.isError.value && !templates.data.value"
    :error="templates.error.value"
    what="templates"
    @retry="templates.refetch()"
  />
  <div v-else class="list">
    <FilterBar v-model:search="search" placeholder="Name, product, manufacturer, vendor…">
      <Select
        v-model="type"
        :options="typeOptions"
        option-label="label"
        option-value="value"
        placeholder="Any type"
        show-clear
        size="small"
        aria-label="Type"
      />
      <template v-if="props.manage" #end>
        <Button label="New template" icon="pi pi-plus" size="small" data-test="new-template" @click="open(null)" />
      </template>
    </FilterBar>
    <DataTable
      :value="shown"
      :loading="templates.isLoading.value"
      size="small"
      class="panel table"
      data-key="id"
      row-hover
      lazy
      removable-sort
      :sort-field="sort?.field"
      :sort-order="sort?.order ?? 0"
      data-test="templates-table"
      @sort="onSort"
      @row-click="(e) => open(e.data)"
    >
      <Column header="Name" field="name" sortable>
        <template #body="{ data }"
          ><span class="strong">{{ data.name }}</span></template
        >
      </Column>
      <Column header="Type" field="type" sortable>
        <template #body="{ data }">{{ typeLabels[data.type as LicenseType] }}</template>
      </Column>
      <Column header="Category" field="category" sortable>
        <template #body="{ data }">{{ data.categoryName }}</template>
      </Column>
      <Column header="Manufacturer" field="manufacturer" sortable>
        <template #body="{ data }">{{ data.manufacturer ?? '–' }}</template>
      </Column>
      <Column header="Product" field="product" sortable>
        <template #body="{ data }">{{ data.productName }}</template>
      </Column>
      <Column header="Edition" field="edition" sortable>
        <template #body="{ data }">{{ data.edition ?? '–' }}</template>
      </Column>
      <Column header="Vendor" field="vendor" sortable>
        <template #body="{ data }">{{ data.vendor ?? '–' }}</template>
      </Column>
      <Column header="Kind" field="kind" sortable>
        <template #body="{ data }">{{ data.kind ? kindLabels[data.kind as keyof typeof kindLabels] : '–' }}</template>
      </Column>
      <Column header="Default term" field="term" sortable>
        <template #body="{ data }">{{ termText(data) }}</template>
      </Column>
      <Column header="Billing and price" field="price" sortable>
        <template #body="{ data }"
          ><span class="nowrap">{{ priceText(data) }}</span></template
        >
      </Column>
      <Column header="End of life" field="eol" sortable>
        <template #body="{ data }">
          <span class="nowrap" :title="data.endOfLifeNote ?? undefined">{{
            data.endOfLifeOn ? formatDay(data.endOfLifeOn) : '–'
          }}</span>
        </template>
      </Column>
      <Column header="Items" field="items" sortable style="width: 5rem">
        <template #body="{ data }">{{ data.items }}</template>
      </Column>
      <Column v-if="edit || manage" style="width: 6rem">
        <template #body="{ data }">
          <span class="actions">
            <Button
              v-if="edit"
              icon="pi pi-plus-circle"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Make an item from ${data.name}`"
              title="Make an item from it"
              @click.stop="makeItem(data)"
            />
            <Button
              v-if="manage"
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :aria-label="`Delete the template ${data.name}`"
              title="Delete"
              @click.stop="remove(data)"
            />
          </span>
        </template>
      </Column>
      <template #empty>
        <EmptyState
          v-if="templates.data.value?.length"
          icon="pi pi-search"
          title="No template matches."
          text="Try other words, or another type."
        />
        <EmptyState
          v-else
          icon="pi pi-clone"
          title="No templates yet."
          text="A template fills in a new item's product, vendor, category and defaults: make one for the products and services you buy most."
        />
      </template>
    </DataTable>
    <TemplateDialog
      v-model:visible="dialog"
      :template="opened"
      :type="type ?? 'License'"
      :manage="manage"
      :edit="edit"
      @make="makeItem"
    />
  </div>
</template>

<style scoped>
.list {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.table {
  overflow: hidden;
}

.table :deep(tr) {
  cursor: pointer;
}

.strong {
  font-weight: 500;
}

.actions {
  display: inline-flex;
}
</style>
