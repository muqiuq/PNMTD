<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Drawer from 'primevue/drawer'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  kbAiWrittenIcon,
  kbApi,
  kbKeys,
  kbLinkIcon,
  kbLinkRoute,
  kbNumber,
  kbStatusLabel,
  kbStatusOptions,
  kbStatusSeverity,
  type KbGraphNode,
  type KbLinkKind,
} from '@/api/kb'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import LinkPicker, { type LinkTarget } from '../LinkPicker.vue'
import KnowledgeMapCanvas, { type Selection } from './KnowledgeMapCanvas.vue'
import { edgeKey, edgeLabel, kindLabel, mapFromQuery, mapToApi, mapToQuery, type KbMapState } from './knowledgeMap'

/**
 * The knowledge map (docs/knowledge-base-extensions-plan.md §8): pages, locations, hosts and sensors, and how they are connected.
 * Around a focus (`?focus=page:42`, 2 steps), or the whole knowledge base with filters; all of it in the URL. A click opens a side
 * panel, which doesn't block the map.
 */
const route = useRoute()
const router = useRouter()

const state = computed<KbMapState>(() => mapFromQuery(route.query))
function update(next: Partial<KbMapState>) {
  void router.replace({ query: mapToQuery({ ...state.value, ...next }) })
}
const params = computed(() => mapToApi(state.value))
const graph = useQuery({
  queryKey: computed(() => kbKeys.map(params.value)),
  queryFn: () => kbApi.map(params.value),
  placeholderData: keepPreviousData,
  refetchInterval: 60_000,
})
const data = computed(() => graph.data.value)

const selected = ref<Selection>(null)
watch(
  () => state.value.focus,
  () => (selected.value = null),
)
const node = computed(() =>
  selected.value?.kind === 'node' ? (data.value?.nodes.find((n) => n.id === selected.value!.id) ?? null) : null,
)
const edge = computed(() =>
  selected.value?.kind === 'edge' ? (data.value?.edges.find((e) => edgeKey(e) === selected.value!.id) ?? null) : null,
)
const byId = computed(() => new Map((data.value?.nodes ?? []).map((n) => [n.id, n])))
const panelOpen = computed({
  get: () => !!node.value || !!edge.value,
  set: (open: boolean) => {
    if (!open) selected.value = null
  },
})
/** The node's neighbours, by how they are connected. */
const neighbours = computed(() => {
  const n = node.value
  if (!n || !data.value) return []
  return data.value.edges
    .filter((e) => e.source === n.id || e.target === n.id)
    .map((e) => ({ edge: e, other: byId.value.get(e.source === n.id ? e.target : e.source)! }))
    .filter((x) => x.other)
    .sort((a, b) => a.other.kind.localeCompare(b.other.kind) || a.other.name.localeCompare(b.other.name))
})

const focusNode = computed(() => (state.value.focus ? byId.value.get(data.value?.focus ?? '') : undefined))
const kinds: Record<string, KbLinkKind> = { page: 'Page', location: 'Location', host: 'Host', sensor: 'Sensor' }
function focusOn(target: LinkTarget) {
  const kind = target.kind.toLowerCase()
  update({ focus: kind === 'page' ? `page:${target.number}` : `${kind}:${target.id}` })
}
function focusHere(n: KbGraphNode) {
  update({ focus: n.kind === 'Page' ? `page:${n.number}` : `${n.kind.toLowerCase()}:${n.refId}` })
}
function routeOf(n: KbGraphNode) {
  return kbLinkRoute({ kind: kinds[n.kind.toLowerCase()]!, id: n.refId, number: n.number })
}
function iconOf(n: KbGraphNode) {
  return kbLinkIcon[kinds[n.kind.toLowerCase()]!]
}

// The filters of the whole knowledge base.
const tagText = ref(state.value.tags.map((t) => `#${t}`).join(' '))
let timer: ReturnType<typeof setTimeout> | undefined
watch(tagText, (value) => {
  clearTimeout(timer)
  timer = setTimeout(
    () =>
      update({
        tags: value
          .split(/[\s,]+/)
          .map((t) => t.replace(/^#+/, '').toLowerCase())
          .filter(Boolean),
      }),
    400,
  )
})
const depths = [1, 2, 3, 4].map((d) => ({ label: `${d} step${d === 1 ? '' : 's'}`, value: d }))
const statuses = [{ label: 'Any status', value: '' }, ...kbStatusOptions]
const aiOptions = [
  { label: 'AI or not', value: '' },
  { label: 'Written with AI', value: 'true' },
  { label: 'Not written with AI', value: 'false' },
]
const locationName = ref<string | null>(null)
function pickLocation(target: LinkTarget) {
  locationName.value = target.name
  update({ location: target.id })
}
const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`
</script>

<template>
  <div class="page">
    <PageHeader title="Knowledge map" :crumbs="[{ label: 'Knowledge base', to: { name: 'kb' } }, { label: 'Map' }]" />

    <div class="bar">
      <template v-if="state.focus">
        <span class="chip" aria-label="Focus">
          <i v-if="focusNode" :class="iconOf(focusNode)" aria-hidden="true" />
          Around
          {{
            focusNode
              ? (focusNode.detail && focusNode.kind === 'Page' ? `${focusNode.detail} ` : '') + focusNode.name
              : state.focus
          }}
          <button type="button" class="chip-x" aria-label="The whole knowledge base" @click="update({ focus: null })">
            <i class="pi pi-times" />
          </button>
        </span>
        <Select
          :model-value="state.depth"
          :options="depths"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Steps"
          @update:model-value="(v: number) => update({ depth: v })"
        />
      </template>
      <template v-else>
        <InputText v-model="tagText" size="small" placeholder="#tags" aria-label="Tags" class="tags" />
        <div class="picker">
          <LinkPicker :kinds="['Location']" placeholder="In a location…" @pick="pickLocation" />
        </div>
        <span v-if="state.location" class="chip">
          <i class="pi pi-map-marker" aria-hidden="true" /> {{ locationName ?? 'a location' }}
          <button type="button" class="chip-x" aria-label="Any location" @click="update({ location: null })">
            <i class="pi pi-times" />
          </button>
        </span>
        <Select
          :model-value="state.status ?? ''"
          :options="statuses"
          option-label="label"
          option-value="value"
          placeholder="Any status"
          size="small"
          aria-label="Status"
          @update:model-value="(v: string) => update({ status: (v || null) as KbMapState['status'] })"
        />
        <Select
          :model-value="state.ai === null ? '' : String(state.ai)"
          :options="aiOptions"
          option-label="label"
          option-value="value"
          placeholder="AI or not"
          size="small"
          aria-label="Written with AI"
          @update:model-value="(v: string) => update({ ai: v === 'true' ? true : v === 'false' ? false : null })"
        />
        <label class="check">
          <Checkbox
            :model-value="state.unlinked"
            binary
            input-id="kbmap-unlinked"
            @update:model-value="(v: boolean) => update({ unlinked: v })"
          />
          <span>Unlinked pages</span>
        </label>
      </template>
      <div class="picker">
        <LinkPicker placeholder="Focus on…" @pick="focusOn" />
      </div>
      <label class="check">
        <Checkbox
          :model-value="state.archived"
          binary
          input-id="kbmap-archived"
          @update:model-value="(v: boolean) => update({ archived: v })"
        />
        <span>Archived</span>
      </label>
      <label class="check">
        <Checkbox
          :model-value="state.circles"
          binary
          input-id="kbmap-circles"
          @update:model-value="(v: boolean) => update({ circles: v })"
        />
        <span>Circles for sites</span>
      </label>
    </div>

    <Message v-if="data?.truncated" severity="info" :closable="false">
      Showing {{ data.nodes.length.toLocaleString() }} of {{ data.total.toLocaleString() }}; narrow it with the filters,
      or focus on a page.
    </Message>

    <ErrorState
      v-if="graph.isError.value && !data"
      :error="graph.error.value"
      what="the map"
      @retry="graph.refetch()"
    />
    <div v-else-if="!data" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <EmptyState
      v-else-if="!data.nodes.length"
      title="No links yet."
      text="Link pages to each other, or to locations, hosts and sensors, in a page's details."
    />
    <KnowledgeMapCanvas v-else v-model:selected="selected" :graph="data" :circles="state.circles" :focus="data.focus" />

    <Drawer
      v-model:visible="panelOpen"
      position="right"
      :modal="false"
      :dismissable="false"
      :header="node ? kindLabel[node.kind] : 'Connection'"
      class="map-panel"
    >
      <template v-if="node">
        <h2 class="name">
          <i :class="iconOf(node)" class="muted" aria-hidden="true" />
          {{ node.kind === 'Page' ? `${kbNumber(node.number ?? 0)} ${node.name}` : node.name }}
        </h2>
        <p v-if="node.detail && node.kind !== 'Page'" class="muted">{{ node.detail }}</p>
        <div v-if="node.kind === 'Page'" class="facts">
          <Tag
            v-if="node.status && node.status !== 'Published'"
            :value="kbStatusLabel[node.status]"
            :severity="kbStatusSeverity(node.status)"
          />
          <Tag v-if="node.sensitive" icon="pi pi-lock" value="Sensitive" severity="secondary" />
          <Tag v-if="node.writtenWithAi" :icon="kbAiWrittenIcon" value="Written with AI" severity="secondary" />
          <Tag v-if="node.archived" value="Archived" severity="secondary" />
          <span class="muted"
            ><i class="pi pi-heart" aria-hidden="true" /> {{ node.likes }} ·
            <i class="pi pi-check-circle" aria-hidden="true" /> {{ plural(node.uses, 'use', 'uses') }}</span
          >
        </div>
        <p v-else-if="node.state" class="muted">State: {{ node.state }}</p>
        <div class="actions">
          <Button label="Open" icon="pi pi-external-link" size="small" @click="router.push(routeOf(node))" />
          <Button
            label="Focus here"
            icon="pi pi-bullseye"
            size="small"
            severity="secondary"
            outlined
            @click="focusHere(node)"
          />
        </div>
        <h3 v-if="neighbours.length" class="sub">Connected</h3>
        <ul class="list">
          <li v-for="x in neighbours" :key="edgeKey(x.edge)">
            <i :class="iconOf(x.other)" class="muted" aria-hidden="true" />
            <span class="link-text">
              <a href="#" @click.prevent="selected = { kind: 'node', id: x.other.id }">{{
                x.other.kind === 'Page' ? `${x.other.detail} ${x.other.name}` : x.other.name
              }}</a>
              <span class="muted detail"
                >{{ edgeLabel[x.edge.kind] }}{{ x.edge.alsoInText ? ', also in the text' : '' }}</span
              >
            </span>
          </li>
        </ul>
      </template>
      <template v-else-if="edge">
        <p>
          <strong>{{ edgeLabel[edge.kind] }}</strong><span v-if="edge.alsoInText" class="muted"
            >, also in the text</span
          >
        </p>
        <ul class="list">
          <li v-for="id in [edge.source, edge.target]" :key="id">
            <template v-if="byId.get(id)">
              <i :class="iconOf(byId.get(id)!)" class="muted" aria-hidden="true" />
              <a href="#" @click.prevent="selected = { kind: 'node', id }">{{ byId.get(id)!.name }}</a>
            </template>
          </li>
        </ul>
      </template>
    </Drawer>
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.tags {
  width: 12rem;
}

.picker {
  width: 14rem;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  border: 1px solid var(--p-primary-color);
  border-radius: 999px;
  padding: 0.15rem 0.3rem 0.15rem 0.6rem;
}

.chip-x {
  border: none;
  background: none;
  cursor: pointer;
  color: var(--pnmt-muted);
}

.chip-x .pi {
  font-size: 0.7rem;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  white-space: nowrap;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.name {
  font-size: 1.1rem;
  margin: 0 0 var(--pnmt-space-2);
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}

.facts {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-3);
}

.actions {
  display: flex;
  gap: var(--pnmt-space-2);
  margin: var(--pnmt-space-3) 0;
}

.sub {
  font-size: 0.857rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pnmt-muted);
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.link-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.detail {
  font-size: 0.857rem;
}
</style>
