<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Paginator from 'primevue/paginator'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  kbAiWrittenIcon,
  kbApi,
  kbKeys,
  kbLinkIcon,
  kbNumber,
  kbStatusLabel,
  kbStatusOptions,
  kbStatusSeverity,
  type KbLinkKind,
} from '@/api/kb'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useMedia } from '@/composables/useMedia'
import { useSession } from '@/stores/session'
import LinkPicker, { type LinkTarget } from './LinkPicker.vue'
import { fromQuery, sortOptions, toApi, toggleTag, toQuery, type KbListState, type KbSort } from './listState'
import { hasMatch, snippetParts } from './snippet'
import TagBrowser from './TagBrowser.vue'

/**
 * The knowledge base (docs/locations-knowledge-plan.md §6.6, §6.7): newest first, or recently updated, most viewed and
 * most edited (30 days or all time), by title, by relevance while searching the content. Filters: tags (several narrow
 * it down), status, what a page links to (a location with its sub-locations), and archived pages. All of it in the URL.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const narrow = useMedia('(max-width: 999px)')
const PageSize = 50

const state = computed<KbListState>(() => fromQuery(route.query))
function update(next: Partial<KbListState>, keepPage = false) {
  const merged = { ...state.value, ...next }
  if (!keepPage) merged.page = 1
  const query: Record<string, string> = toQuery(merged)
  // The name of what the list links to, from a panel's "All pages…", as long as that filter stays.
  const sameLink =
    merged.location === state.value.location && merged.host === state.value.host && merged.sensor === state.value.sensor
  if (sameLink && typeof route.query.linkedName === 'string') query.linkedName = route.query.linkedName
  void router.replace({ query })
}

// The search box is debounced into the URL.
const search = ref(state.value.q)
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => update({ q: value }), 300)
})
watch(
  () => state.value.q,
  (q) => {
    if (q !== search.value.trim()) search.value = q
  },
)

const params = computed(() => toApi(state.value, PageSize))
const list = useQuery({
  queryKey: computed(() => kbKeys.list(params.value)),
  queryFn: () => kbApi.list(params.value),
  placeholderData: keepPreviousData,
  refetchInterval: 60_000,
})
const items = computed(() => list.data.value?.items ?? [])
const total = computed(() => list.data.value?.totalCount ?? 0)
const searching = computed(() => state.value.q.trim().length > 0)

const sorts = computed(() => sortOptions.filter((s) => !s.searching || searching.value))
const sort = computed({
  get: () => state.value.sort ?? (searching.value ? 'relevance' : 'newest'),
  set: (value: KbSort) => update({ sort: value === (searching.value ? 'relevance' : 'newest') ? null : value }),
})
const periods = [
  { label: '30 days', value: '30d' },
  { label: 'All time', value: 'all' },
]
const statuses = [{ label: 'Any status', value: '' }, ...kbStatusOptions]
// Tri-state filters (docs/knowledge-base-extensions-plan.md §4, §5): the URL holds true or false, '' is no filter.
const sensitiveOptions = [
  { label: 'Sensitive or not', value: '' },
  { label: 'Only sensitive', value: 'true' },
  { label: 'Not sensitive', value: 'false' },
]
const aiOptions = [
  { label: 'AI or not', value: '' },
  { label: 'Written with AI', value: 'true' },
  { label: 'Not written with AI', value: 'false' },
]
const asYesNo = (v: string): boolean | null => (v === 'true' ? true : v === 'false' ? false : null)

// What the list is filtered to link to: its name comes with the picked target, or from the first row's links.
const linked = ref<LinkTarget | null>(null)
const linkFilter = computed(() =>
  state.value.location
    ? { kind: 'Location' as KbLinkKind, id: state.value.location }
    : state.value.host
      ? { kind: 'Host' as KbLinkKind, id: state.value.host }
      : state.value.sensor
        ? { kind: 'Sensor' as KbLinkKind, id: state.value.sensor }
        : null,
)
function pickLink(target: LinkTarget) {
  linked.value = target
  update({
    location: target.kind === 'Location' ? target.id : null,
    host: target.kind === 'Host' ? target.id : null,
    sensor: target.kind === 'Sensor' ? target.id : null,
  })
}
const linkedName = computed(() => {
  const f = linkFilter.value
  if (!f) return null
  if (linked.value?.id === f.id) return linked.value.name
  return typeof route.query.linkedName === 'string' ? route.query.linkedName : 'the linked object'
})

const activeFilters = computed(
  () =>
    state.value.tags.length +
    (state.value.status ? 1 : 0) +
    (linkFilter.value ? 1 : 0) +
    (state.value.archived ? 1 : 0) +
    (state.value.sensitive !== null ? 1 : 0) +
    (state.value.ai !== null ? 1 : 0),
)

const count = (n: number, what: string) => `${n.toLocaleString()} ${what}${n === 1 ? '' : 's'}`

function newPage() {
  const f = linkFilter.value
  void router.push({ name: 'kb-new', query: f ? { link: `${f.kind.toLowerCase()}:${f.id}` } : {} })
}
</script>

<template>
  <div class="page">
    <PageHeader title="Knowledge base">
      <template #status>
        <span class="muted">{{ total }}</span>
      </template>
      <template #actions>
        <Button
          v-if="session.can('KnowledgeBase', 'Edit')"
          label="New page"
          icon="pi pi-plus"
          size="small"
          @click="newPage"
        />
      </template>
    </PageHeader>

    <FilterBar v-model:search="search" placeholder="Search the pages' content" :active="activeFilters">
      <Select
        v-model="sort"
        :options="sorts"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Order"
      />
      <SelectButton
        v-if="state.sort === 'views' || state.sort === 'edits'"
        :model-value="state.period"
        :options="periods"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Period"
        @update:model-value="(v: '30d' | 'all') => update({ period: v })"
      />
      <Select
        :model-value="state.status ?? ''"
        :options="statuses"
        option-label="label"
        option-value="value"
        placeholder="Any status"
        size="small"
        aria-label="Status"
        @update:model-value="(v: string) => update({ status: (v || null) as KbListState['status'] })"
      />
      <Select
        :model-value="state.sensitive === null ? '' : String(state.sensitive)"
        :options="sensitiveOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Sensitive"
        @update:model-value="(v: string) => update({ sensitive: asYesNo(v) })"
      />
      <Select
        :model-value="state.ai === null ? '' : String(state.ai)"
        :options="aiOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Written with AI"
        @update:model-value="(v: string) => update({ ai: asYesNo(v) })"
      />
      <div class="link-filter">
        <LinkPicker placeholder="Linked to…" @pick="pickLink" />
      </div>
      <label class="archived">
        <Checkbox
          :model-value="state.archived"
          binary
          input-id="kb-archived"
          @update:model-value="(v: boolean) => update({ archived: v })"
        />
        <span>Show archived</span>
      </label>
    </FilterBar>

    <div v-if="state.tags.length || linkFilter" class="chips" aria-label="Active filters">
      <button v-for="t in state.tags" :key="t" type="button" class="chip" @click="update(toggleTag(state, t))">
        #{{ t }} <i class="pi pi-times" aria-label="Remove" />
      </button>
      <button
        v-if="linkFilter"
        type="button"
        class="chip"
        @click="update({ location: null, host: null, sensor: null })"
      >
        <i :class="kbLinkIcon[linkFilter.kind]" aria-hidden="true" /> {{ linkedName }}
        <span v-if="linkFilter.kind === 'Location'" class="muted">with its sub-locations</span>
        <i class="pi pi-times" aria-label="Remove" />
      </button>
    </div>

    <div class="layout" :class="{ narrow }">
      <div class="results">
        <ErrorState
          v-if="list.isError.value && !list.data.value"
          :error="list.error.value"
          what="the pages"
          @retry="list.refetch()"
        />
        <div v-else-if="!list.data.value" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
        <EmptyState
          v-else-if="!items.length"
          :title="searching || activeFilters ? 'No page matches.' : 'No pages yet.'"
          :text="
            searching
              ? 'Every word matches as a prefix; &quot;…&quot; is a phrase, -word excludes, or gives alternatives.'
              : undefined
          "
        />
        <ul v-else class="rows panel">
          <li v-for="p in items" :key="p.id" :class="{ 'row-muted': p.archivedAt }">
            <div class="line">
              <span class="number mono">{{ kbNumber(p.number) }}</span>
              <RouterLink :to="{ name: 'kb-page', params: { number: p.number } }" class="title">{{
                p.title
              }}</RouterLink>
              <i v-if="p.sensitive" class="pi pi-lock muted" title="Sensitive: never shown to AI or MCP" />
              <i v-if="p.writtenWithAi" :class="kbAiWrittenIcon" class="muted" title="Written mostly with AI" />
              <i v-if="p.hasAttachments" class="pi pi-paperclip muted" title="Has attachments" />
              <Tag
                v-if="p.status !== 'Published'"
                :value="kbStatusLabel[p.status]"
                :severity="kbStatusSeverity(p.status)"
                class="status"
              />
              <Tag v-if="p.archivedAt" value="Archived" severity="secondary" class="status" />
              <span class="meta muted">
                <span v-if="state.sort === 'views'"
                  >{{ count(state.period === 'all' ? p.views : p.recentViews, 'view') }} ·
                </span>
                <span v-else-if="state.sort === 'edits'"
                  >{{ count(state.period === 'all' ? p.edits : p.recentEdits, 'edit') }} ·
                </span>
                <span v-else-if="p.views">{{ count(p.views, 'view') }} · </span>
                <RelativeTime :value="state.sort === 'newest' || !state.sort ? p.createdAt : p.updatedAt" />
                <span v-if="p.updatedByName"> · {{ p.updatedByName }}</span>
              </span>
            </div>
            <p v-if="hasMatch(p.snippet)" class="snippet">
              <template v-for="(part, i) in snippetParts(p.snippet)" :key="i"
                ><mark v-if="part.mark">{{ part.text }}</mark
                ><template v-else>{{ part.text }}</template></template
              >
            </p>
            <p v-else-if="p.summary" class="summary muted">{{ p.summary }}</p>
            <div v-if="p.tags.length" class="tags">
              <button
                v-for="t in p.tags"
                :key="t.name"
                type="button"
                class="tag"
                :class="{ on: state.tags.includes(t.name) }"
                :title="t.source === 'Ai' ? 'Suggested by AI, not yet confirmed' : undefined"
                @click="update(toggleTag(state, t.name))"
              >
                <i v-if="t.source === 'Ai'" class="pi pi-sparkles" aria-label="AI" />#{{ t.name }}
              </button>
            </div>
          </li>
        </ul>
        <Paginator
          v-if="total > PageSize"
          :rows="PageSize"
          :total-records="total"
          :first="(state.page - 1) * PageSize"
          @page="(e) => update({ page: e.page + 1 }, true)"
        />
      </div>
      <TagBrowser :selected="state.tags" class="side" @toggle="(t) => update(toggleTag(state, t))" />
    </div>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 18rem;
  gap: var(--pnmt-space-3);
  align-items: start;
}

.layout.narrow {
  grid-template-columns: minmax(0, 1fr);
}

.link-filter {
  width: 14rem;
}

.archived {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  white-space: nowrap;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  border: 1px solid var(--p-primary-color);
  border-radius: 999px;
  background: transparent;
  color: var(--pnmt-text);
  padding: 0.15rem 0.6rem;
  font: inherit;
  cursor: pointer;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.rows li:last-child {
  border-bottom: none;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
  flex-wrap: wrap;
}

.number {
  color: var(--pnmt-muted);
  white-space: nowrap;
}

.title {
  font-weight: 600;
  min-width: 0;
}

.status {
  font-size: 0.75rem;
}

.meta {
  margin-left: auto;
  font-size: 0.857rem;
  white-space: nowrap;
}

.summary,
.snippet {
  margin: 0;
  font-size: 0.929rem;
  overflow-wrap: anywhere;
}

.snippet mark {
  background: var(--pnmt-warn-soft);
  color: inherit;
  border-radius: 2px;
  padding: 0 1px;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.tag {
  border: none;
  background: none;
  padding: 0;
  font: inherit;
  font-size: 0.857rem;
  color: var(--p-primary-color);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.tag.on {
  font-weight: 600;
  text-decoration: underline;
}

.tag .pi {
  font-size: 0.75rem;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}
</style>
