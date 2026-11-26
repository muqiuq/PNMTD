<script setup lang="ts">
import Button from 'primevue/button'
import { computed, defineComponent, nextTick, onMounted, ref, watch } from 'vue'
import { personLabel, type ResponsibilityGlobal } from '@/api/responsibilities'
import { isColour, textOn } from '@/components/colours'
import type { Point } from '@/components/graph/forceLayout'
import { bendOf, boundsOf, edgePath } from '@/components/graph/geometry'
import { useMoving } from '@/components/graph/useMoving'
import { noteNodes } from '@/boards/boardMarkdown'
import { usePanZoom } from '@/composables/usePanZoom'
import {
  bodyRadius,
  layoutOverview,
  nodeRadius,
  noteObstacle,
  noteSize,
  type Lines,
  type NoteWidth,
  type OverviewEdge,
  type OverviewNode,
} from './overviewLayout'

/**
 * The overview's drawing (docs/responsibilities-extensions-plan.md §6.1): Vue-rendered SVG on the network map's shared parts. A
 * responsibility is a circle in its colour with its name below, ⚠ when it needs attention and G when the global responsible stands in;
 * a person a smaller circle with their code; a linked object the knowledge map's small shapes. Lines follow the escalation order
 * (docs/responsibilities-mcp-and-map-plan.md §4), styled by role: solid to the responsible, dashed to the deputy, dotted from the
 * global people; thin and grey to what is linked. The wheel and pinch zoom, the background pans;
 * hovering a node fades what isn't next to it; a click selects, a double-click opens.
 *
 * Moving (§6.2): every node drags and moves with the arrow keys; only responsibilities are saved (the page's bar). With `editable`, a
 * responsibility moved but not saved has a dashed ring.
 */
/** A note as the canvas draws it: note:<id>, its Markdown, colour, width and top-left corner. */
export interface CanvasNote {
  id: string
  text: string
  colour: string
  width: NoteWidth
  x: number
  y: number
}

const props = defineProps<{
  nodes: OverviewNode[]
  edges: OverviewEdge[]
  global: ResponsibilityGlobal
  notes?: CanvasNote[]
  focus?: string | null
  editable?: boolean
  /** Which of the escalation order the people's lines show, for the legend. */
  lineView?: Lines
  /** Arranged places not saved yet (docs/responsibilities-mcp-and-map-plan.md §5.4): fixed for the layout, as saved ones. */
  anchors?: ReadonlyMap<string, Point>
}>()
const linesNote: Record<Lines, string> = {
  responsible: 'Lines: the first in each escalation order',
  deputy: 'Lines: the second in each escalation order',
  both: 'Lines: the first two in each escalation order',
}
const selected = defineModel<string | null>('selected', { default: null })
const moved = defineModel<Map<string, Point>>('moved', { default: () => new Map() })
const emit = defineEmits<{ open: [id: string]; 'edit-note': [id: string] }>()

const svg = ref<SVGSVGElement | null>(null)
const pan = usePanZoom(svg)
// Notes are fixed bodies where they were saved (not where they are dragged), so nothing jumps while one moves.
const notePlaces = computed(
  () => new Map((props.notes ?? []).map((n) => [n.id, { ...noteSize(n.text, n.width), x: n.x, y: n.y }])),
)
const obstacles = computed(() => [...notePlaces.value].map(([id, n]) => noteObstacle({ id, ...n })))
const computedLayout = computed(() => layoutOverview(props.nodes, props.edges, obstacles.value, props.anchors))
// Only another set of nodes fits anew (the map refreshes every minute); the moves stay until saved or discarded.
const nodeSet = computed(() => props.nodes.map((n) => n.id).join(','))
/** Where a node is now, or a note's top-left corner: its move, its saved place, or the layout's. */
function at(id: string): Point {
  const note = notePlaces.value.get(id)
  return (
    moved.value.get(id) ??
    (note ? { x: note.x, y: note.y } : null) ??
    computedLayout.value.nodes.get(id) ?? { x: 0, y: 0 }
  )
}

const MAX_LABEL = 28
const label = (n: OverviewNode) => {
  const text = n.kind === 'page' && n.number != null ? `KB-${n.number} ${n.name}` : n.name
  return text.length > MAX_LABEL ? `${text.slice(0, MAX_LABEL - 1)}…` : text
}
const kindLabel: Record<OverviewNode['kind'], string> = {
  responsibility: 'Responsibility',
  person: 'Person',
  host: 'Host',
  location: 'Location',
  page: 'Page',
}

const drawn = computed(() =>
  props.nodes.map((n) => ({
    ...n,
    p: at(n.id),
    r: nodeRadius(n.kind),
    unsaved: !!props.editable && n.kind === 'responsibility' && moved.value.has(n.id),
  })),
)
const byId = computed(() => new Map(drawn.value.map((n) => [n.id, n])))
const drawnNotes = computed(() =>
  (props.notes ?? []).map((n) => ({
    ...n,
    ...noteSize(n.text, n.width),
    p: at(n.id),
    unsaved: !!props.editable && moved.value.has(n.id),
    firstLine: n.text
      .split('\n')[0]!
      .replace(/[*_#>`]/g, '')
      .trim()
      .slice(0, 60),
  })),
)
/** A note's Markdown, as a board's note draws it; on the map without links, so a click moves or selects it. */
const NoteText = defineComponent({
  props: { markdown: { type: String, required: true } },
  setup: (p) => () => noteNodes(p.markdown, false),
})
const lines = computed(() =>
  props.edges
    .map((e, i) => {
      const a = byId.value.get(e.source)
      const b = byId.value.get(e.target)
      if (!a || !b) return null
      // A person who is both, say, the global responsible standing in and an inactive responsible: the lines bend apart.
      const twins = props.edges.filter((x) => x.source === e.source && x.target === e.target)
      return {
        ...e,
        a: a.p,
        b: b.p,
        bend: bendOf(twins.indexOf(e), twins.length),
        key: `${e.kind}|${e.source}|${e.target}|${i}`,
      }
    })
    .filter((e): e is NonNullable<typeof e> => e !== null),
)

const hovered = ref<string | null>(null)
/** The selected or hovered node and what is next to it; the rest fades. */
const near = computed(() => {
  const id = hovered.value ?? selected.value
  if (!id || !byId.value.has(id)) return null
  const set = new Set([id])
  for (const e of props.edges) {
    if (e.source === id) set.add(e.target)
    if (e.target === id) set.add(e.source)
  }
  return set
})

function bounds() {
  return boundsOf(
    [
      ...drawn.value.map((n) => ({ x: n.p.x, y: n.p.y, r: bodyRadius(n.kind) })),
      ...drawnNotes.value.map((n) => ({ x: n.p.x + n.w / 2, y: n.p.y + n.h / 2, r: Math.hypot(n.w, n.h) / 2 })),
    ],
    computedLayout.value.bounds,
  )
}
function fit() {
  pan.fit(bounds())
}
onMounted(() => nextTick(fit))
watch(nodeSet, () => nextTick(fit))
watch(
  () => props.focus,
  (id) => {
    if (id && byId.value.has(id)) {
      pan.centre(at(id))
      selected.value = id
    }
  },
  { immediate: true },
)

// --- moving (components/graph/useMoving.ts) -----------------------------------------------------------------------------
const { begin, move, end, release, nudge } = useMoving(moved, { at, toWorld: (e) => pan.toWorld(e) })
function up(id: string) {
  if (release()) selected.value = id
}
/** Enter selects a note; on a selected one it opens it for editing (with Edit). */
function enterNote(id: string) {
  if (selected.value === id && props.editable) emit('edit-note', id)
  else selected.value = id
}
/** Enter selects a node; on a selected responsibility it opens its page. */
function enter(n: OverviewNode) {
  if (selected.value === n.id && n.kind === 'responsibility') emit('open', n.id)
  else selected.value = n.id
}

const zoom = computed(() => pan.view.value.k)
const transform = computed(() => `translate(${pan.view.value.x} ${pan.view.value.y}) scale(${pan.view.value.k})`)
const fill = (n: OverviewNode) => (isColour(n.color) ? { fill: n.color } : {})
const ink = (n: OverviewNode) => (isColour(n.color) ? textOn(n.color) : undefined)

/** Where every node on the map is now: its move, its saved place, or the layout's. */
function positions(): Map<string, Point> {
  return new Map(drawn.value.map((n) => [n.id, n.p]))
}

/** The middle of what is shown, in the map's units: where a new note goes. */
function middle(): Point {
  const rect = svg.value?.getBoundingClientRect()
  return pan.toWorld({
    clientX: (rect?.left ?? 0) + (rect?.width ?? 800) / 2,
    clientY: (rect?.top ?? 0) + (rect?.height ?? 600) / 2,
  } as PointerEvent)
}

defineExpose({ fit, positions, middle, at })
</script>

<template>
  <div class="map">
    <svg
      ref="svg"
      class="canvas"
      role="img"
      aria-label="Responsibilities overview"
      data-testid="responsibilities-map"
      @wheel="pan.onWheel"
      @pointerdown="pan.onPointerDown"
      @pointermove="pan.onPointerMove"
      @pointerup="pan.onPointerUp"
      @pointercancel="pan.onPointerUp"
      @click.self="selected = null"
    >
      <g :transform="transform">
        <g
          v-for="n in drawnNotes"
          :key="n.id"
          class="note"
          :class="[`note-${n.colour.toLowerCase()}`, { chosen: selected === n.id }]"
          :transform="`translate(${n.p.x} ${n.p.y})`"
          :data-note="n.id"
          :data-unsaved="n.unsaved || undefined"
          role="button"
          tabindex="0"
          :aria-label="`Note ${n.firstLine}${n.unsaved ? ' (moved, not saved)' : ''}`"
          @pointerdown="(ev: PointerEvent) => begin(ev, [n.id])"
          @pointermove="move"
          @pointerup="up(n.id)"
          @pointercancel="end"
          @dblclick="editable && emit('edit-note', n.id)"
          @keydown.enter="enterNote(n.id)"
          @keydown="(ev: KeyboardEvent) => nudge(ev, [n.id])"
        >
          <rect v-if="n.unsaved" class="unsaved" x="-6" y="-6" :width="n.w + 12" :height="n.h + 12" rx="8" />
          <rect class="paper" :width="n.w" :height="n.h" rx="4" />
          <foreignObject :width="n.w" :height="n.h">
            <div xmlns="http://www.w3.org/1999/xhtml" class="note-text"><NoteText :markdown="n.text" /></div>
          </foreignObject>
        </g>
        <g
          v-for="e in lines"
          :key="e.key"
          class="edge"
          :class="[`kind-${e.kind}`, { faded: near && !(near.has(e.source) && near.has(e.target)) }]"
        >
          <path :d="edgePath(e.a, e.b, e.bend)" />
        </g>
        <g
          v-for="n in drawn"
          :key="n.id"
          class="node"
          :class="[
            n.kind,
            {
              faded: near && !near.has(n.id),
              chosen: selected === n.id,
              inactive: n.inactive,
              plain: !isColour(n.color),
            },
          ]"
          :transform="`translate(${n.p.x} ${n.p.y})`"
          :data-node="n.id"
          :data-unsaved="n.unsaved || undefined"
          role="button"
          tabindex="0"
          :aria-label="`${kindLabel[n.kind]} ${n.name}${n.inactive ? ' (inactive)' : ''}${n.attention ? ' (needs attention)' : ''}${n.unsaved ? ' (moved, not saved)' : ''}`"
          @pointerdown="(ev: PointerEvent) => begin(ev, [n.id])"
          @pointermove="move"
          @pointerup="up(n.id)"
          @pointercancel="end"
          @pointerenter="hovered = n.id"
          @pointerleave="hovered = null"
          @dblclick="n.kind === 'responsibility' && emit('open', n.id)"
          @keydown.enter="enter(n)"
          @keydown="(ev: KeyboardEvent) => nudge(ev, [n.id])"
        >
          <title>{{ n.name }}{{ n.summary ? ` · ${n.summary}` : '' }}</title>
          <circle v-if="n.unsaved" class="unsaved" :r="n.r + 6" />
          <rect v-if="n.kind === 'page'" class="body" :x="-n.r" :y="-n.r" :width="2 * n.r" :height="2 * n.r" rx="3" />
          <circle v-else class="body" :r="n.r" :style="fill(n)" />
          <text v-if="n.kind === 'person'" class="code" text-anchor="middle" dy="4" :style="{ fill: ink(n) }">
            {{ n.code }}
          </text>
          <text v-if="n.attention" class="mark" text-anchor="middle" dy="6" :style="{ fill: ink(n) }">⚠</text>
          <g v-if="n.standsIn" class="badge" :transform="`translate(${n.r - 4} ${-n.r + 4})`">
            <circle r="8" />
            <text text-anchor="middle" dy="3.5">G</text>
          </g>
          <text
            v-if="n.kind === 'responsibility' || zoom > 0.6 || selected === n.id || hovered === n.id"
            class="label"
            :class="{ strong: n.kind === 'responsibility' }"
            :y="n.r + 14"
            text-anchor="middle"
          >
            {{ label(n) }}
          </text>
        </g>
      </g>
    </svg>
    <div class="global" data-test="overview-global">
      <template v-if="global.responsible || global.deputy">
        <div><span class="muted">Global responsible</span> {{ personLabel(global.responsible) }}</div>
        <div><span class="muted">Global deputy</span> {{ personLabel(global.deputy) }}</div>
      </template>
      <span v-else class="muted">No global responsible is set.</span>
    </div>
    <div class="bar">
      <slot name="bar" />
    </div>
    <div class="controls">
      <slot name="controls" />
      <Button icon="pi pi-plus" size="small" severity="secondary" aria-label="Zoom in" @click="pan.zoomBy(1.25)" />
      <Button icon="pi pi-minus" size="small" severity="secondary" aria-label="Zoom out" @click="pan.zoomBy(0.8)" />
      <Button label="Fit" size="small" severity="secondary" @click="fit" />
    </div>
    <div class="legend muted">
      <span
        ><svg width="28" height="8"><line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="2" /></svg>
        responsible</span
      >
      <span>
        <svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="2" stroke-dasharray="6 4" />
        </svg>
        deputy
      </span>
      <span>
        <svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line warn" stroke-width="2" stroke-dasharray="2 4" />
        </svg>
        global, standing in
      </span>
      <span>
        <svg width="28" height="8"><line x1="0" y1="4" x2="28" y2="4" class="legend-line thin" stroke-width="1" /></svg>
        linked
      </span>
      <span v-if="lineView" data-test="overview-lines-note">{{ linesNote[lineView] }}</span>
    </div>
  </div>
</template>

<style scoped>
.map {
  position: relative;
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  background: var(--pnmt-panel);
  overflow: hidden;
  height: calc(100vh - 17rem);
  min-height: 420px;
}

.canvas {
  width: 100%;
  height: 100%;
  touch-action: none;
  cursor: grab;
  user-select: none;
}

.edge path {
  fill: none;
  stroke: var(--pnmt-text);
  stroke-width: 2;
  opacity: 0.55;
}

.edge.kind-deputy path {
  stroke-dasharray: 6 4;
}

.edge.kind-global path {
  stroke: var(--pnmt-warn);
  stroke-dasharray: 2 4;
  opacity: 0.9;
}

.edge.kind-link path {
  stroke: var(--pnmt-muted);
  stroke-width: 1;
}

.node {
  cursor: pointer;
}

.node:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.node .body {
  fill: var(--pnmt-none-soft);
  stroke: var(--pnmt-border);
  stroke-width: 1.5;
}

/* Without a colour: neutral, the outline dashed. */
.node.responsibility.plain .body {
  stroke: var(--pnmt-muted);
  stroke-dasharray: 4 3;
}

.node.responsibility .body {
  stroke-width: 2;
}

.node.person .body {
  fill: var(--pnmt-panel);
  stroke: var(--pnmt-text);
}

.node.person:not(.plain) .body {
  stroke: var(--pnmt-border);
}

.node.inactive {
  opacity: 0.55;
}

.node.host .body,
.node.location .body,
.node.page .body {
  fill: var(--pnmt-panel);
  stroke: var(--pnmt-muted);
}

.node.chosen .body {
  stroke: var(--p-primary-color);
  stroke-width: 4;
}

/* Notes: paper in their colour, the text cut at the edge; the side panel shows all of it. */
.note {
  cursor: move;
}

.note:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.note .paper {
  fill: var(--pnmt-note-yellow);
  stroke: var(--pnmt-border);
  stroke-width: 1;
}

.note.note-green .paper {
  fill: var(--pnmt-note-green);
}

.note.note-blue .paper {
  fill: var(--pnmt-note-blue);
}

.note.note-pink .paper {
  fill: var(--pnmt-note-pink);
}

.note.note-purple .paper {
  fill: var(--pnmt-note-purple);
}

.note.note-grey .paper {
  fill: var(--pnmt-note-grey);
}

.note.chosen .paper {
  stroke: var(--p-primary-color);
  stroke-width: 3;
}

.note foreignObject {
  pointer-events: none;
}

.note-text {
  height: 100%;
  overflow: hidden;
  padding: 8px 10px;
  color: var(--pnmt-text);
  font-size: 12px;
  line-height: 17px;
  overflow-wrap: anywhere;
}

.note-text :deep(p),
.note-text :deep(ul),
.note-text :deep(ol) {
  margin: 0 0 4px;
}

.note-text :deep(ul),
.note-text :deep(ol) {
  padding-left: 16px;
}

.note .unsaved {
  fill: none;
  stroke: var(--p-primary-color);
  stroke-width: 2;
  stroke-dasharray: 4 3;
}

/* Moved, not saved yet: what Save layout will write. */
.node .unsaved {
  fill: none;
  stroke: var(--p-primary-color);
  stroke-width: 2;
  stroke-dasharray: 4 3;
}

.node .code {
  fill: var(--pnmt-text);
  font-size: 10px;
  font-weight: 700;
  pointer-events: none;
}

.node .mark {
  fill: var(--pnmt-text);
  font-size: 16px;
  pointer-events: none;
}

.node .badge circle {
  fill: var(--pnmt-warn);
  stroke: var(--pnmt-panel);
  stroke-width: 1.5;
}

.node .badge text {
  fill: #000;
  font-size: 9px;
  font-weight: 700;
  pointer-events: none;
}

.node .label {
  fill: var(--pnmt-text);
  font-size: 11px;
  paint-order: stroke;
  stroke: var(--pnmt-panel);
  stroke-width: 3px;
}

.node .label.strong {
  font-size: 12px;
  font-weight: 600;
}

.faded {
  opacity: 0.2;
}

.global {
  position: absolute;
  top: var(--pnmt-space-2);
  left: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  font-size: 0.857rem;
  display: grid;
  gap: 2px;
  max-width: calc(100% - 2 * var(--pnmt-space-2));
}

.global .muted {
  margin-right: var(--pnmt-space-2);
}

.controls {
  position: absolute;
  right: var(--pnmt-space-2);
  bottom: var(--pnmt-space-2);
  display: flex;
  gap: var(--pnmt-space-1);
}

/* The bar of unsaved moves: on top, in the middle. */
.bar {
  position: absolute;
  top: var(--pnmt-space-2);
  left: 50%;
  transform: translateX(-50%);
  max-width: calc(100% - 2 * var(--pnmt-space-2));
}

.legend {
  position: absolute;
  left: var(--pnmt-space-2);
  bottom: var(--pnmt-space-2);
  display: flex;
  gap: var(--pnmt-space-3);
  font-size: 0.786rem;
  flex-wrap: wrap;
}

.legend span {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.legend-line {
  stroke: var(--pnmt-text);
}

.legend-line.warn {
  stroke: var(--pnmt-warn);
}

.legend-line.thin {
  stroke: var(--pnmt-muted);
}
</style>
