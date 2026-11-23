<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Select from 'primevue/select'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { boardKeys, boardsApi, type BoardDetail, type BoardNetworkRequest } from '@/api/boards'
import { ApiError } from '@/api/client'
import EditingPage from '@/components/markdown/EditingPage.vue'
import ErrorState from '@/components/ErrorState.vue'
import TabNav from '@/components/TabNav.vue'
import { useNotify } from '@/composables/useNotify'
import type { BoardLayout, BoardView } from '@/boards/types'
import { useSession } from '@/stores/session'
import AccessTab from './AccessTab.vue'
import BarTab from './BarTab.vue'
import BoardPreview from './BoardPreview.vue'
import LayoutTab from './LayoutTab.vue'
import LookTab from './LookTab.vue'

/**
 * A board's page (docs/boards-plan.md §10.3): the whole window, as the editing page. Left, the settings in four tabs; right, the preview
 * at a chosen screen's size, fed by the server with the draft (§10.4). Ctrl/⌘ S saves the whole page at once; leaving with unsaved
 * changes asks. With Boards: View only, the same page read-only, without the URL.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()
const queryClient = useQueryClient()
const id = computed(() => String(route.params.id))
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'layout'))
const manage = computed(() => session.can('Boards', 'Manage'))

const detail = useQuery({
  queryKey: computed(() => boardKeys.detail(id.value)),
  queryFn: () => boardsApi.get(id.value),
})
const catalog = useQuery({ queryKey: boardKeys.catalog(), queryFn: boardsApi.catalog, staleTime: 60_000 })

interface Draft {
  name: string
  description: string | null
  enabled: boolean
  networks: BoardNetworkRequest[]
  layout: BoardLayout
}
const draft = ref<Draft | null>(null)
const original = ref('')
function load(board: BoardDetail) {
  draft.value = {
    name: board.name,
    description: board.description ?? null,
    enabled: board.enabled,
    networks: board.networks.map((n) => ({ cidr: n.cidr, label: n.label ?? null })),
    // A copy of its own (the query's data is reactive, which structuredClone refuses).
    layout: JSON.parse(JSON.stringify(board.layout)) as BoardLayout,
  }
  original.value = JSON.stringify(draft.value)
}
watch(
  () => detail.data.value,
  (board) => {
    if (board && (!draft.value || !dirty.value)) load(board)
  },
  { immediate: true },
)
const dirty = computed(() => !!draft.value && JSON.stringify(draft.value) !== original.value)
const selected = ref<string | null>(null)
const tryTheme = ref<string | null>(null)
/** What the preview draws: the draft, with a theme being tried in the picker. */
const shown = computed<BoardLayout | null>(() =>
  draft.value
    ? tryTheme.value
      ? { ...draft.value.layout, look: { ...draft.value.layout.look, theme: tryTheme.value } }
      : draft.value.layout
    : null,
)

// --- the preview (§10.4): after each change (debounced), and every refresh interval while the page is open ---------------
const preview = ref<BoardView | null>(null)
let debounce: ReturnType<typeof setTimeout> | undefined
let interval: ReturnType<typeof setInterval> | undefined
async function refreshPreview() {
  if (!draft.value) return
  try {
    preview.value = await boardsApi.preview({ layout: draft.value.layout, name: draft.value.name })
  } catch (e) {
    // A draft that doesn't validate yet keeps the last preview; the save says what is wrong.
    if (!(e instanceof ApiError && e.status === 400)) preview.value = null
  }
}
watch(
  () => draft.value && JSON.stringify(draft.value.layout),
  () => {
    clearTimeout(debounce)
    debounce = setTimeout(() => void refreshPreview(), 500)
    clearInterval(interval)
    interval = setInterval(() => void refreshPreview(), (draft.value?.layout.refreshSeconds ?? 30) * 1000)
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  clearTimeout(debounce)
  clearInterval(interval)
})
const sizes = [
  { value: 'window', label: 'This window' },
  { value: '1280x720', label: '1280 × 720' },
  { value: '1920x1080', label: '1920 × 1080' },
  { value: '2560x1440', label: '2560 × 1440' },
  { value: '3840x2160', label: '3840 × 2160' },
  { value: '1080x1920', label: 'Portrait 1080 × 1920' },
]
const size = ref('1920x1080')

// --- saving -------------------------------------------------------------------------------------------------------------
const saving = ref(false)
const problems = ref<string[]>([])
async function save() {
  const board = detail.data.value
  if (!draft.value || !board) return
  saving.value = true
  problems.value = []
  try {
    const saved = await boardsApi.save(board.id, { ...draft.value, revision: board.revision })
    queryClient.setQueryData(boardKeys.detail(board.id), saved)
    load(saved)
    await queryClient.invalidateQueries({ queryKey: boardKeys.list() })
    notify.success('Saved', 'Screens show it at their next poll.')
  } catch (e) {
    if (e instanceof ApiError && e.code === 'validation_failed') {
      problems.value = Object.entries(e.errors).map(([field, messages]) => `${field}: ${messages.join(' ')}`)
      const element = Object.keys(e.errors)
        .map((f) => /columns\[(\d+)\]\.elements\[(\d+)\]/.exec(f))
        .find((m) => m)
      if (element)
        selected.value = draft.value.layout.columns[Number(element[1])]?.elements[Number(element[2])]?.id ?? null
    } else if (e instanceof ApiError && e.code === 'edit_conflict') {
      problems.value = [e.message]
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}
async function cancel() {
  await router.push({ name: 'boards' })
}
async function openBoard() {
  try {
    window.open((await boardsApi.url(id.value)).url, '_blank', 'noopener,noreferrer')
  } catch (e) {
    notify.error(e, 'The board can’t be opened')
  }
}
const tabs = computed(() =>
  [
    ['layout', 'Layout'],
    ['bar', 'Status bar'],
    ['look', 'Look'],
    ['access', 'Access'],
  ].map(([key, label]) => ({ key: key!, label: label!, to: { name: 'board', params: { id: id.value, tab: key } } })),
)
</script>

<template>
  <ErrorState v-if="detail.isError.value" :error="detail.error.value" what="the board" @retry="detail.refetch()" />
  <EditingPage
    v-else-if="draft && detail.data.value && shown"
    back-label="Boards"
    :dirty="dirty"
    :saving="saving"
    :can-save="manage && dirty"
    @save="save"
    @cancel="cancel"
    @discard="detail.data.value && load(detail.data.value)"
  >
    <template #title>{{ draft.name }}</template>
    <template #status>
      <span class="muted">{{ dirty ? 'unsaved' : 'saved' }}</span>
    </template>
    <template #actions>
      <Select
        v-model="size"
        :options="sizes"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Preview at"
      />
      <a v-if="manage" href="#" class="open" @click.prevent="openBoard"
        >Open board <i class="pi pi-external-link" aria-hidden="true"
      /></a>
    </template>
    <template v-if="problems.length" #notices>
      <ul class="problems" role="alert">
        <li v-for="p in problems" :key="p">{{ p }}</li>
      </ul>
    </template>
    <div class="editor">
      <aside class="settings">
        <TabNav :items="tabs" :active="tab" label="Board" />
        <div class="tab-body">
          <LayoutTab
            v-if="tab === 'layout'"
            v-model:layout="draft.layout"
            v-model:name="draft.name"
            v-model:description="draft.description"
            v-model:selected="selected"
            :catalog="catalog.data.value ?? null"
            :manage="manage"
          />
          <BarTab
            v-else-if="tab === 'bar'"
            v-model:bar="draft.layout.bar"
            v-model:header="draft.layout.header"
            :catalog="catalog.data.value ?? null"
            :manage="manage"
          />
          <LookTab
            v-else-if="tab === 'look'"
            v-model:look="draft.layout.look"
            v-model:refresh="draft.layout.refreshSeconds"
            :manage="manage"
            @try-theme="(t) => (tryTheme = t)"
          />
          <AccessTab
            v-else
            v-model:enabled="draft.enabled"
            v-model:networks="draft.networks"
            :board="detail.data.value"
            :manage="manage"
          />
        </div>
      </aside>
      <BoardPreview
        :layout="shown"
        :view="preview"
        :name="draft.name"
        :size="size"
        :selected="selected"
        @select="
          (e) => ((selected = e), tab !== 'layout' && router.replace({ name: 'board', params: { id, tab: 'layout' } }))
        "
      />
    </div>
  </EditingPage>
</template>

<style scoped>
.editor {
  display: flex;
  gap: var(--pnmt-space-3);
  height: 100%;
  min-height: 0;
  padding: var(--pnmt-space-3);
}

.settings {
  flex: 0 0 25rem;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.tab-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: var(--pnmt-space-3) var(--pnmt-space-1) var(--pnmt-space-3) 0;
}

.open {
  font-size: 0.9rem;
  white-space: nowrap;
}

.problems {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
  color: var(--pnmt-fail);
}

@media (max-width: 900px) {
  .editor {
    flex-direction: column;
    overflow-y: auto;
  }

  .settings {
    flex: 0 0 auto;
  }
}
</style>
