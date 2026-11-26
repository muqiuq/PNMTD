<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Drawer from 'primevue/drawer'
import Menu from 'primevue/menu'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import type { MenuItem } from 'primevue/menuitem'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { responsibilitiesApi, responsibilityKeys } from '@/api/responsibilities'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import type { Point } from '@/components/graph/forceLayout'
import MoreMenu from '@/components/MoreMenu.vue'
import { useActions } from '@/composables/useActions'
import { arrange, place, type Form } from './arrange'
import NoteDialog from './NoteDialog.vue'
import OverviewCanvas, { type CanvasNote } from './OverviewCanvas.vue'
import { buildGraph, nodeId, noteSize, type Layer, type Lines } from './overviewLayout'
import OverviewPanel from './OverviewPanel.vue'
import { focusFromQuery, layersFromQuery, layersToQuery, linesFromQuery, linesToQuery } from './overviewState'

/**
 * The Overview tab (docs/responsibilities-extensions-plan.md §6): the responsibilities on a map with their people and, as layers, the
 * objects they link. The layers, the people's lines (docs/responsibilities-mcp-and-map-plan.md §4) and the focus are in the URL. Arranging works as on the network map (§6.2): the moves are kept here,
 * across layers and refreshes; with Edit, a bar offers Save layout and Discard. Saving writes the moved responsibilities and the
 * shown ones never saved, so nothing moves afterwards. Leaving with unsaved moves asks first. *Arrange* (docs/responsibilities-mcp-and-map-plan.md
 * §5) places every responsibility in a form, as moves of their own: the people settle around them at once (the anchors).
 */
const props = defineProps<{ edit: boolean }>()
const route = useRoute()
const router = useRouter()

const layers = ref<Set<Layer>>(layersFromQuery(route.query))
const layerChoices: { value: Layer; label: string }[] = [
  { value: 'people', label: 'People' },
  { value: 'hosts', label: 'Hosts' },
  { value: 'locations', label: 'Locations' },
  { value: 'pages', label: 'Pages' },
  { value: 'notes', label: 'Notes' },
]
function toggle(layer: Layer, on: boolean) {
  const next = new Set(layers.value)
  if (on) next.add(layer)
  else next.delete(layer)
  layers.value = next
}
watch(layers, (value) => {
  const query = { ...route.query }
  const text = layersToQuery(value)
  if (text === undefined) delete query.layers
  else query.layers = text
  void router.replace({ query })
})
// Whom the people's lines go to: the first in each escalation order by default, the second, or the first two.
const lines = ref<Lines>(linesFromQuery(route.query))
const lineChoices: { value: Lines; label: string }[] = [
  { value: 'responsible', label: 'Responsible' },
  { value: 'deputy', label: 'Deputy' },
  { value: 'both', label: 'Both' },
]
watch(lines, (value) => {
  const query = { ...route.query }
  const text = linesToQuery(value)
  if (text === undefined) delete query.lines
  else query.lines = text
  void router.replace({ query })
})
const params = computed(() => ({
  hosts: layers.value.has('hosts') || undefined,
  locations: layers.value.has('locations') || undefined,
  pages: layers.value.has('pages') || undefined,
}))
const map = useQuery({
  queryKey: computed(() => responsibilityKeys.map(params.value)),
  queryFn: () => responsibilitiesApi.map(params.value),
  placeholderData: keepPreviousData,
  refetchInterval: 60_000,
})
const graph = computed(() =>
  map.data.value ? buildGraph(map.data.value, layers.value, lines.value) : { nodes: [], edges: [] },
)
const notes = computed<CanvasNote[]>(() =>
  layers.value.has('notes')
    ? (map.data.value?.notes ?? []).map((n) => ({
        id: `note:${n.id}`,
        text: n.text,
        colour: n.colour,
        width: n.width,
        x: n.x,
        y: n.y,
      }))
    : [],
)
const empty = computed(() => map.data.value && !map.data.value.responsibilities.length && !map.data.value.notes.length)

const selected = ref<string | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
// The panel keeps the last selection while the drawer slides out.
const shown = ref<string | null>(null)
watch(selected, (s) => s && (shown.value = s), { immediate: true })
const focus = computed(() => focusFromQuery(route.query))
const find = ref<string | null>(null)
const findFocus = ref<string | null>(null)
watch(find, (id) => {
  if (id) {
    findFocus.value = id
    selected.value = id
  }
})
const kindWord: Record<string, string> = {
  responsibility: '',
  person: 'person · ',
  host: 'host · ',
  location: 'location · ',
  page: 'page · ',
}
const findOptions = computed(() =>
  graph.value.nodes.map((n) => ({ value: n.id, label: `${kindWord[n.kind] ?? ''}${n.name}` })),
)
function openPage(id: string) {
  void router.push({ name: 'responsibility', params: { id: id.slice('responsibility:'.length) } })
}

// --- moving and saving the layout ----------------------------------------------------------------------------------------
const { run, done, ask, busy } = useActions()
const canvas = ref<InstanceType<typeof OverviewCanvas> | null>(null)
const moved = ref(new Map<string, Point>())
const savable = (id: string) => id.startsWith('responsibility:') || id.startsWith('note:')
const movedSavable = computed(() => [...moved.value.keys()].filter(savable))
const movedNotes = computed(() => movedSavable.value.filter((id) => id.startsWith('note:')))
/** "2 responsibilities and 1 note", for the bar and the toast. */
function counted(responsibilities: number, notes: number): string {
  const parts = []
  if (responsibilities) parts.push(plural(responsibilities, 'responsibility', 'responsibilities'))
  if (notes) parts.push(plural(notes, 'note', 'notes'))
  return parts.join(' and ')
}
const dirty = computed(() => props.edit && movedSavable.value.length > 0)
const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** What Save writes: the moved responsibilities, and the shown ones without a saved place, where they are now. */
function toSave(): Map<string, Point> {
  const out = new Map<string, Point>()
  for (const id of movedSavable.value) if (id.startsWith('responsibility:')) out.set(id, moved.value.get(id)!)
  const now = canvas.value?.positions() ?? new Map<string, Point>()
  for (const n of graph.value.nodes) {
    const p = now.get(n.id)
    if (n.kind === 'responsibility' && !n.saved && !out.has(n.id) && p) out.set(n.id, p)
  }
  return out
}
const toSaveCount = computed(
  () =>
    movedSavable.value.length +
    graph.value.nodes.filter((n) => n.kind === 'responsibility' && !n.saved && !moved.value.has(n.id)).length,
)
const round = (v: number) => Math.round(v * 100) / 100
async function save() {
  const points = toSave()
  const noteMoves = movedNotes.value.map((id) => [id, moved.value.get(id)!] as const)
  if (!points.size && !noteMoves.length) return
  const ok = await run(
    () =>
      responsibilitiesApi.savePositions(
        [...points].map(([id, p]) => ({ id: id.slice('responsibility:'.length), x: round(p.x), y: round(p.y) })),
        noteMoves.map(([id, p]) => ({ id: id.slice('note:'.length), x: round(p.x), y: round(p.y) })),
      ),
    {
      success: `Saved the place${points.size + noteMoves.length === 1 ? '' : 's'} of ${counted(points.size, noteMoves.length)}`,
      failure: 'Layout not saved',
      invalidate: [responsibilityKeys.all],
    },
  )
  // The map has the saved places now: the moves and the arranged anchors can go without anything jumping.
  if (ok) {
    const next = new Map(moved.value)
    for (const id of points.keys()) next.delete(id)
    for (const [id] of noteMoves) next.delete(id)
    moved.value = next
    anchors.value = new Map()
  }
}
function discard() {
  moved.value = new Map()
  anchors.value = new Map()
}
async function reset() {
  const count = map.data.value?.savedPositions ?? 0
  const sure = await ask(
    'Back to automatic for all responsibilities?',
    `The saved places of ${plural(count, 'responsibility', 'responsibilities')} are cleared, for everyone. The notes stay where they are.`,
    'Back to automatic',
  )
  if (!sure) return
  if (
    await done(() => responsibilitiesApi.resetPositions(), {
      success: 'Every responsibility is placed automatically again',
      invalidate: [responsibilityKeys.all],
    })
  ) {
    const next = new Map(moved.value)
    for (const id of [...next.keys()]) if (id.startsWith('responsibility:')) next.delete(id)
    moved.value = next
    anchors.value = new Map()
  }
}
async function automatic(id: string) {
  const name = map.data.value?.responsibilities.find((r) => r.id === id)?.name ?? 'The responsibility'
  if (
    await done(() => responsibilitiesApi.clearPosition(id), {
      success: `${name} is placed automatically again`,
      invalidate: [responsibilityKeys.all],
    })
  ) {
    const next = new Map(moved.value)
    next.delete(`responsibility:${id}`)
    moved.value = next
    const kept = new Map(anchors.value)
    kept.delete(`responsibility:${id}`)
    anchors.value = kept
  }
}

// --- arranging (§5) ------------------------------------------------------------------------------------------------------
/** Arranged places not saved yet: fixed for the automatic layout, so people settle around them at once (§5.4). */
const anchors = ref(new Map<string, Point>())
/**
 * The last Arrange, said in the bar on top while its places stand. Not a toast: toasts sit in the bottom-right corner, on the
 * map's controls, just when another form is tried.
 */
const arranged = ref<{ form: Form; clear: boolean } | null>(null)
const arrangedNote = computed(() =>
  arranged.value && anchors.value.size
    ? `arranged as ${formLabel[arranged.value.form]}${arranged.value.clear ? '' : ', some under notes'}`
    : '',
)
const arrangeMenu = ref<InstanceType<typeof Menu> | null>(null)
const formLabel: Record<Form, string> = {
  circle: 'a circle',
  clusters: 'clusters around their responsible',
  grid: 'a grid',
}
async function arrangeAs(form: Form) {
  const data = map.data.value
  if (!data?.responsibilities.length) return
  // Moved by hand since (not where an earlier Arrange put them): asks first.
  const byHand = [...moved.value].filter(([id, p]) => {
    const a = anchors.value.get(id)
    return id.startsWith('responsibility:') && (!a || a.x !== p.x || a.y !== p.y)
  })
  if (
    props.edit &&
    byHand.length &&
    !(await ask(
      'Replace your unsaved moves?',
      `${plural(byHand.length, 'responsibility moved by hand is', 'responsibilities moved by hand are')} placed anew. Moved notes stay where they are.`,
      'Arrange',
    ))
  )
    return
  const items = data.responsibilities.map((r) => ({
    id: nodeId('responsibility', r.id),
    name: r.name,
    escalation: r.escalation,
  }))
  // Centred where the responsibilities are now, clear of every note, shown or not.
  const now = canvas.value?.positions() ?? new Map<string, Point>()
  const here = items.map((i) => now.get(i.id)).filter((p): p is Point => !!p)
  const centre = here.length
    ? { x: here.reduce((s, p) => s + p.x, 0) / here.length, y: here.reduce((s, p) => s + p.y, 0) / here.length }
    : { x: 0, y: 0 }
  const boxes = data.notes.map((n) => {
    const at = moved.value.get(`note:${n.id}`) ?? { x: n.x, y: n.y }
    return { ...at, ...noteSize(n.text, n.width) }
  })
  const { points, clear } = place(
    arrange(form, items),
    centre,
    boxes,
    items.map((i) => i.name),
  )
  const next = new Map(moved.value)
  for (const [id, p] of points) next.set(id, p)
  moved.value = next
  anchors.value = points
  arranged.value = { form, clear }
  await nextTick()
  canvas.value?.fit()
}
const arrangeItems = computed<MenuItem[]>(() => [
  { label: 'Circle', icon: 'pi pi-circle', command: () => void arrangeAs('circle') },
  { label: 'Around their responsible', icon: 'pi pi-sitemap', command: () => void arrangeAs('clusters') },
  { label: 'Grid', icon: 'pi pi-th-large', command: () => void arrangeAs('grid') },
])
// --- notes (§7) ----------------------------------------------------------------------------------------------------------
const noteOpen = ref(false)
const editingNote = ref<NonNullable<typeof map.data.value>['notes'][number] | null>(null)
const noteAt = ref<Point | null>(null)
function newNote() {
  editingNote.value = null
  noteAt.value = canvas.value?.middle() ?? { x: 0, y: 0 }
  noteOpen.value = true
}
function editNote(id: string) {
  const plain = id.startsWith('note:') ? id.slice('note:'.length) : id
  editingNote.value = map.data.value?.notes.find((n) => n.id === plain) ?? null
  noteOpen.value = !!editingNote.value
}
function noteSaved(id: string) {
  // Shown, if the layer was off.
  if (!layers.value.has('notes')) toggle('notes', true)
  selected.value = `note:${id}`
}
async function deleteNote(id: string) {
  const plain = id.startsWith('note:') ? id.slice('note:'.length) : id
  if (
    !(await ask('Delete the note?', 'It goes from the overview, for everyone. The audit log keeps its text.', 'Delete'))
  )
    return
  if (
    await done(() => responsibilitiesApi.deleteNote(plain), {
      success: 'Note deleted',
      invalidate: [responsibilityKeys.all],
    })
  ) {
    const next = new Map(moved.value)
    next.delete(`note:${plain}`)
    moved.value = next
    selected.value = null
  }
}

const layoutItems = computed<MenuItem[]>(() => [
  { label: 'Save this layout', icon: 'pi pi-save', disabled: !toSaveCount.value, command: () => void save() },
  {
    label: 'Back to automatic for all responsibilities…',
    icon: 'pi pi-replay',
    disabled: !map.data.value?.savedPositions,
    command: () => void reset(),
  },
])

// Leaving with unsaved moves: another page, another tab of this page, or the browser's reload.
async function mayLeave(): Promise<boolean> {
  if (!dirty.value) return true
  const leave = await ask(
    'Leave without saving?',
    `What you moved is not saved: ${counted(movedSavable.value.length - movedNotes.value.length, movedNotes.value.length)}.`,
    'Leave',
  )
  if (leave) discard()
  return leave
}
onBeforeRouteLeave(mayLeave)
onBeforeRouteUpdate((to) => (to.params.tab === 'overview' ? true : mayLeave()))
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (dirty.value) e.preventDefault()
}
onMounted(() => window.addEventListener('beforeunload', onBeforeUnload))
onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))
</script>

<template>
  <div class="tab">
    <div class="filters">
      <span class="muted">Show</span>
      <label v-for="l in layerChoices" :key="l.value" class="check">
        <Checkbox
          :model-value="layers.has(l.value)"
          binary
          :input-id="`layer-${l.value}`"
          @update:model-value="(v: boolean) => toggle(l.value, v)"
        />
        <span>{{ l.label }}</span>
      </label>
      <span class="lines" :title="layers.has('people') ? undefined : 'Turn on People'">
        <span class="muted">Lines to</span>
        <SelectButton
          v-model="lines"
          :options="lineChoices"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :disabled="!layers.has('people')"
          size="small"
          aria-label="Lines to"
          data-test="overview-lines"
        />
      </span>
      <span class="spacer" />
      <Select
        v-model="find"
        :options="findOptions"
        option-label="label"
        option-value="value"
        filter
        show-clear
        placeholder="Find…"
        size="small"
        class="find"
        aria-label="Find on the overview"
      />
      <Button v-if="edit" label="Note" icon="pi pi-plus" size="small" data-test="overview-new-note" @click="newNote" />
    </div>
    <p v-if="map.data.value?.truncated" class="muted notice" role="status">
      {{ map.data.value.objects.length }} of {{ map.data.value.objectsTotal }} linked objects shown, those most shared
      first. Choose fewer layers, or use Find.
    </p>
    <ErrorState
      v-if="map.isError.value && !map.data.value"
      :error="map.error.value"
      what="the overview"
      @retry="map.refetch()"
    />
    <EmptyState
      v-else-if="empty"
      icon="pi pi-briefcase"
      title="No responsibilities yet."
      text="Create them on the Responsibilities tab; they appear here, with their people."
    />
    <OverviewCanvas
      v-else-if="map.data.value"
      ref="canvas"
      v-model:selected="selected"
      v-model:moved="moved"
      :nodes="graph.nodes"
      :edges="graph.edges"
      :global="map.data.value.global"
      :notes="notes"
      :focus="findFocus ?? focus"
      :editable="edit"
      :line-view="layers.has('people') ? lines : undefined"
      :anchors="anchors"
      @open="openPage"
      @edit-note="editNote"
    >
      <template #bar>
        <div v-if="dirty" class="save-bar" role="status" data-test="overview-save-bar">
          <span>{{ counted(movedSavable.length - movedNotes.length, movedNotes.length) }} moved</span>
          <span v-if="arrangedNote" class="muted" data-test="overview-arranged">{{ arrangedNote }}</span>
          <Button label="Discard" size="small" severity="secondary" text :disabled="busy" @click="discard" />
          <Button label="Save layout" icon="pi pi-save" size="small" :loading="busy" @click="save" />
        </div>
        <!-- Without Edit nothing is saved: the bar says so, and Discard is the way back to the automatic layout. -->
        <div v-else-if="arrangedNote" class="save-bar" role="status" data-test="overview-save-bar">
          <span>{{ plural(anchors.size, 'responsibility', 'responsibilities') }} {{ arrangedNote }}</span>
          <span class="muted">only for you, until you leave the page</span>
          <Button label="Discard" size="small" severity="secondary" text @click="discard" />
        </div>
      </template>
      <template #controls>
        <Button
          label="Arrange"
          icon="pi pi-objects-column"
          size="small"
          severity="secondary"
          aria-haspopup="true"
          aria-controls="overview-arrange"
          data-test="overview-arrange"
          @click="(e: Event) => arrangeMenu?.toggle(e)"
        />
        <Menu id="overview-arrange" ref="arrangeMenu" :model="arrangeItems" popup />
        <MoreMenu v-if="edit" :items="layoutItems" label="Layout" />
      </template>
    </OverviewCanvas>

    <Drawer
      v-model:visible="open"
      position="right"
      :modal="false"
      :dismissable="false"
      class="overview-drawer"
      aria-label="Details"
    >
      <template #container="{ closeCallback }">
        <OverviewPanel
          v-if="shown && map.data.value"
          :selection="shown"
          :map="map.data.value"
          :edit="edit"
          @select="(id: string) => ((selected = id), (findFocus = id))"
          @automatic="automatic"
          @edit-note="editNote"
          @delete-note="deleteNote"
          @close="closeCallback"
        />
      </template>
    </Drawer>
    <NoteDialog v-model:visible="noteOpen" :note="editingNote" :at="noteAt" @saved="noteSaved" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.filters {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.929rem;
}

.lines {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-size: 0.929rem;
}

.spacer {
  flex: 1;
}

.find {
  width: 16rem;
}

.notice {
  margin: 0;
  font-size: 0.857rem;
}

.save-bar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-1) var(--pnmt-space-1) var(--pnmt-space-1) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  box-shadow: 0 2px 10px rgb(0 0 0 / 12%);
  white-space: nowrap;
}

.save-bar span {
  font-weight: 500;
}
</style>

<style>
/* As the network map's drawer: PrimeVue's own width rule weighs as much as a two-class one. */
.p-drawer-mask .p-drawer.overview-drawer {
  width: min(28rem, 100vw);
}

@media (max-width: 599px) {
  .p-drawer-mask .p-drawer.overview-drawer {
    width: 100vw;
  }
}
</style>
