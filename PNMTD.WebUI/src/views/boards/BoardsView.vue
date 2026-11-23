<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { boardKeys, boardsApi, type BoardItem } from '@/api/boards'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useNotify } from '@/composables/useNotify'
import { themeName } from '@/boards/themes/registry'
import { useSession } from '@/stores/session'
import NewBoardDialog from './NewBoardDialog.vue'

/**
 * Boards (docs/boards-plan.md §10.2): monitoring dashboards for a wall, each at a URL of its own. The list: columns and elements, the
 * theme, the networks (⚠ anywhere), the screens that show it now, and on or off. A row opens the board's page.
 */
const router = useRouter()
const session = useSession()
const notify = useNotify()
const { ask } = useActions()
const queryClient = useQueryClient()
const manage = computed(() => session.can('Boards', 'Manage'))
const list = useQuery({ queryKey: boardKeys.list(), queryFn: boardsApi.list, refetchInterval: 30_000 })
const items = computed(() => list.data.value ?? [])
const creating = ref(false)
const open = (b: BoardItem) => router.push({ name: 'board', params: { id: b.id } })

async function duplicate(b: BoardItem) {
  try {
    const copy = await boardsApi.duplicate(b.id)
    await queryClient.invalidateQueries({ queryKey: boardKeys.all })
    await router.push({ name: 'board', params: { id: copy.id } })
  } catch (e) {
    notify.error(e, 'Not duplicated')
  }
}

async function remove(b: BoardItem) {
  if (!(await ask(`Delete ${b.name}?`, 'Its URL stops working at once, on every screen that shows it.'))) return
  try {
    await boardsApi.remove(b.id)
    await queryClient.invalidateQueries({ queryKey: boardKeys.all })
    notify.success(`${b.name} deleted`)
  } catch (e) {
    notify.error(e, 'Not deleted')
  }
}

function seen(b: BoardItem): string | null {
  return b.screensNow > 0 ? (b.screensNow === 1 ? '1 screen now' : `${b.screensNow} screens now`) : null
}
</script>

<template>
  <div class="page">
    <PageHeader title="Boards">
      <template #actions>
        <Button
          v-if="manage"
          label="New board"
          icon="pi pi-plus"
          size="small"
          data-test="new-board"
          @click="creating = true"
        />
      </template>
    </PageHeader>
    <ErrorState v-if="list.isError.value" :error="list.error.value" what="boards" @retry="list.refetch()" />
    <EmptyState
      v-else-if="list.data.value && !items.length"
      icon="pi pi-objects-column"
      title="No boards yet"
      text="A board shows chosen parts of PNMT, Redmine and Zabbix on a wall, at a URL of its own."
    />
    <DataTable
      v-else
      :value="items"
      :loading="list.isLoading.value"
      size="small"
      class="panel"
      data-key="id"
      row-hover
      @row-click="(e) => open(e.data)"
    >
      <Column header="Name">
        <template #body="{ data }">
          <RouterLink :to="{ name: 'board', params: { id: data.id } }" class="strong">{{ data.name }}</RouterLink>
          <div v-if="data.description" class="muted small">{{ data.description }}</div>
        </template>
      </Column>
      <Column header="Columns" style="width: 6rem">
        <template #body="{ data }">{{ data.columns }}</template>
      </Column>
      <Column header="Elements" style="width: 6rem">
        <template #body="{ data }">{{ data.elements }}</template>
      </Column>
      <Column header="Theme" style="width: 10rem">
        <template #body="{ data }">{{ themeName(data.theme) }}</template>
      </Column>
      <Column header="Networks" style="width: 9rem">
        <template #body="{ data }">
          <span v-if="data.anywhere" class="warn-text"
            ><i class="pi pi-exclamation-triangle" aria-hidden="true" /> Anywhere</span
          >
          <span v-else>{{ data.networks === 1 ? '1 network' : `${data.networks} networks` }}</span>
        </template>
      </Column>
      <Column header="Seen" style="width: 10rem">
        <template #body="{ data }">
          <span v-if="seen(data)" class="ok-text">{{ seen(data) }}</span>
          <RelativeTime v-else-if="data.lastSeenAt" :value="data.lastSeenAt" />
          <span v-else class="muted">Never</span>
        </template>
      </Column>
      <Column header="On" style="width: 5rem">
        <template #body="{ data }">
          <Tag :value="data.enabled ? 'on' : 'off'" :severity="data.enabled ? 'success' : 'secondary'" />
        </template>
      </Column>
      <Column v-if="manage" style="width: 6rem">
        <template #body="{ data }">
          <div class="row-actions" @click.stop>
            <Button
              icon="pi pi-copy"
              text
              size="small"
              severity="secondary"
              :aria-label="`Duplicate ${data.name}`"
              @click="duplicate(data)"
            />
            <Button
              icon="pi pi-trash"
              text
              size="small"
              severity="danger"
              :aria-label="`Delete ${data.name}`"
              @click="remove(data)"
            />
          </div>
        </template>
      </Column>
    </DataTable>
    <NewBoardDialog v-model:visible="creating" :boards="items" />
  </div>
</template>

<style scoped>
.small {
  font-size: 0.85rem;
}

.row-actions {
  display: flex;
  justify-content: flex-end;
}

.warn-text {
  color: var(--pnmt-warn);
}

.ok-text {
  color: var(--pnmt-ok);
  font-weight: 600;
}
</style>
