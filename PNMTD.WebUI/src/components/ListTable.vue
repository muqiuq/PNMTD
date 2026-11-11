<script setup lang="ts" generic="T extends { id: string }">
import Button from 'primevue/button'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import { computed } from 'vue'
import { useMedia } from '@/composables/useMedia'
import type { useServerTable } from '@/composables/useServerTable'
import EmptyState from './EmptyState.vue'
import ErrorState from './ErrorState.vue'

/**
 * A server-paged list (§5.3): a compact DataTable on a desktop; below 600 px a list of two-line
 * rows (the #mobile slot) with a pager, so nothing scrolls sideways.
 */
const props = withDefaults(
  defineProps<{
    table: ReturnType<typeof useServerTable<T>>
    what: string
    emptyText?: string
    rowClass?: (item: T) => string | object | undefined
    selectable?: boolean
  }>(),
  { emptyText: undefined, rowClass: undefined, selectable: false },
)
const emit = defineEmits<{ select: [item: T] }>()
const phone = useMedia('(max-width: 599px)')
const q = computed(() => props.table.query)
const pages = computed(() => Math.max(1, Math.ceil(props.table.total.value / props.table.pageSize.value)))
</script>

<template>
  <ErrorState v-if="q.isError.value && !q.data.value" :error="q.error.value" :what="what" @retry="q.refetch()" />
  <div v-else-if="phone" class="mobile-list panel">
    <div v-if="!q.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <EmptyState v-else-if="!table.items.value.length" :title="emptyText ?? `No ${what}.`" />
    <ul v-else>
      <li
        v-for="item in table.items.value"
        :key="item.id"
        :class="rowClass?.(item)"
        :tabindex="selectable ? 0 : undefined"
        @click="selectable && emit('select', item)"
        @keydown.enter="selectable && emit('select', item)"
      >
        <slot name="mobile" :item="item" />
      </li>
    </ul>
    <div v-if="pages > 1" class="pager">
      <Button
        icon="pi pi-angle-left"
        text
        size="small"
        :disabled="table.page.value <= 1"
        aria-label="Previous page"
        @click="table.goTo(table.page.value - 1)"
      />
      <span class="muted">{{ table.page.value }} / {{ pages }} · {{ table.total.value }}</span>
      <Button
        icon="pi pi-angle-right"
        text
        size="small"
        :disabled="table.page.value >= pages"
        aria-label="Next page"
        @click="table.goTo(table.page.value + 1)"
      />
    </div>
  </div>
  <DataTable
    v-else
    v-bind="table.tableProps.value"
    size="small"
    data-key="id"
    class="list-table panel"
    :row-class="rowClass"
    :row-hover="selectable"
    paginator-template="FirstPageLink PrevPageLink PageLinks NextPageLink LastPageLink RowsPerPageDropdown CurrentPageReport"
    current-page-report-template="{first}–{last} of {totalRecords}"
    @page="table.onPage"
    @sort="table.onSort"
    @row-click="(e: { data: T }) => selectable && emit('select', e.data)"
  >
    <slot />
    <template #empty>
      <EmptyState :title="emptyText ?? `No ${what}.`" />
    </template>
  </DataTable>
</template>

<style scoped>
.list-table {
  overflow: hidden;
}

.list-table :deep(.p-datatable-table-container) {
  border-radius: var(--p-border-radius-md);
}

.list-table :deep(.row-muted) {
  opacity: 0.6;
}

.list-table :deep(tr.p-datatable-selectable-row),
.list-table :deep(tr[data-p-selectable-row='true']) {
  cursor: pointer;
}

.mobile-list ul {
  list-style: none;
  margin: 0;
  padding: 0;
}

.mobile-list li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.mobile-list li.row-muted {
  opacity: 0.6;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.pager {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1);
}
</style>
