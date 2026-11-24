<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Paginator from 'primevue/paginator'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import {
  historyKindLabel,
  responsibilitiesApi,
  responsibilityKeys,
  type ResponsibilityHistoryItem,
} from '@/api/responsibilities'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'

/**
 * A responsibility's history (docs/responsibilities-plan.md §10.5), newest first: who changed what. A description's change
 * shows the text before and after it, and its text from before can be restored, as a location's value can: a change of its own.
 */
const props = defineProps<{ id: string; edit: boolean }>()
const { run, ask, notify } = useActions()

const pageSize = 50
const page = ref(1)
const history = useQuery({
  queryKey: computed(() => responsibilityKeys.history(props.id, page.value)),
  queryFn: () => responsibilitiesApi.history(props.id, page.value, pageSize),
  placeholderData: keepPreviousData,
})
const rows = computed(() => history.data.value?.items ?? [])

const shown = ref<{ id: string; at: string; by: string; old: string; new: string } | null>(null)
async function show(row: ResponsibilityHistoryItem) {
  try {
    const detail = await responsibilitiesApi.historyRow(props.id, row.id)
    shown.value = {
      id: row.id,
      at: detail.at,
      by: detail.byName,
      old: detail.oldValue ?? '',
      new: detail.newValue ?? '',
    }
  } catch (e) {
    notify.error(e, 'Not loaded')
  }
}

async function restore() {
  const row = shown.value
  if (!row) return
  if (
    !(await ask(
      'Restore the description?',
      'The description goes back to its text from before this change. That is a new change of its own.',
      'Restore',
    ))
  ) {
    return
  }
  const restored = await run(() => responsibilitiesApi.restore(props.id, row.id), {
    success: 'Description restored',
    failure: 'Not restored',
    invalidate: [responsibilityKeys.all],
  })
  if (restored) shown.value = null
}
</script>

<template>
  <ErrorState v-if="history.isError.value" :error="history.error.value" what="the history" @retry="history.refetch()" />
  <section v-else class="panel" aria-label="History">
    <ul v-if="rows.length" class="rows">
      <li v-for="row in rows" :key="row.id" data-test="history-row">
        <div class="what">
          <span class="strong">{{ historyKindLabel[row.kind] }}</span>
          <span v-if="row.detail" class="muted detail">{{ row.detail }}</span>
          <Tag v-if="row.via === 'mcp'" value="via MCP" severity="secondary" />
        </div>
        <div class="when muted">
          {{ row.byName }} · <span :title="formatAbsolute(row.at)"><RelativeTime :value="row.at" /></span>
        </div>
        <Button v-if="row.kind === 'DescriptionChanged'" label="Show" size="small" text @click="show(row)" />
      </li>
    </ul>
    <EmptyState v-else-if="!history.isLoading.value" icon="pi pi-history" title="No changes yet." />
    <Paginator
      v-if="(history.data.value?.totalCount ?? 0) > pageSize"
      :rows="pageSize"
      :total-records="history.data.value?.totalCount ?? 0"
      :first="(page - 1) * pageSize"
      @page="(e) => (page = e.page + 1)"
    />
  </section>

  <Dialog
    :visible="!!shown"
    header="Description changed"
    modal
    :style="{ width: 'min(64rem, 95vw)' }"
    @update:visible="(v) => !v && (shown = null)"
  >
    <template v-if="shown">
      <p class="muted">{{ shown.by }}, {{ formatAbsolute(shown.at) }}</p>
      <div class="compare">
        <div>
          <div class="label muted">Before</div>
          <MarkdownView v-if="shown.old.trim()" :source="shown.old" />
          <p v-else class="muted">Empty.</p>
        </div>
        <div>
          <div class="label muted">After</div>
          <MarkdownView v-if="shown.new.trim()" :source="shown.new" />
          <p v-else class="muted">Empty.</p>
        </div>
      </div>
    </template>
    <template v-if="edit" #footer>
      <Button label="Restore the text from before" icon="pi pi-undo" severity="secondary" @click="restore" />
    </template>
  </Dialog>
</template>

<style scoped>
.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
  border-bottom: 1px solid var(--p-content-border-color);
}

.rows li:last-child {
  border-bottom: none;
}

.what {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
}

.detail {
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.when {
  font-size: 0.857rem;
  white-space: nowrap;
}

.compare {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-4);
}

.label {
  font-size: 0.857rem;
  text-transform: uppercase;
  margin-bottom: var(--pnmt-space-2);
}

@media (max-width: 899px) {
  .compare {
    grid-template-columns: 1fr;
  }
}
</style>
