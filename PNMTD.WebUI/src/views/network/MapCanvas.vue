<script setup lang="ts">
import Button from 'primevue/button'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { S } from '@/api/resources'
import { bendOf, boundsOf, edgePath } from '@/components/graph/geometry'
import { useMoving } from '@/components/graph/useMoving'
import { usePanZoom } from '@/composables/usePanZoom'
import { edgeDash, edgeTone, edgeWidth, nodeGlyph } from './networkFormat'
import { bodyRadius, encloseGroup, layout, nodeRadius, type GroupCircle, type Point } from './networkLayout'

/**
 * The map's drawing (docs/network-map-plan.md §8.2): Vue-rendered SVG, as the charts are. Nodes are circles with the name
 * below; a location's nodes sit in a dashed circle with its name on top; edges grow with the speed, virtual ones dashed,
 * Wi-Fi dotted, unconfirmed ones in the warning colour, suggestions thin and grey. The wheel and pinch zoom, the background
 * pans.
 *
 * Moving (docs/network-map-positions-plan.md §2): a node drags, a location drags by its name, a focused node or location
 * name moves with the arrow keys (Shift: further), Escape puts a drag back. The moves are the v-model `moved`, kept by the
 * page until they are saved or discarded; saved routers come with the graph and stay where they were saved. With
 * `editable`, a router moved but not saved has a dashed ring.
 */
export type Selection = { kind: 'node'; id: string } | { kind: 'edge'; id: string } | null

const props = defineProps<{ graph: S['NetworkGraph']; focus?: string | null; editable?: boolean }>()
const selected = defineModel<Selection>('selected', { default: null })
const moved = defineModel<Map<string, Point>>('moved', { default: () => new Map() })

const svg = ref<SVGSVGElement | null>(null)
const pan = usePanZoom(svg)
const groupNames = computed(() => new Map(props.graph.groups.map((g) => [g.locationId, g.name])))
/** The routers' saved positions, from the graph. */
const saved = computed(
  () => new Map(props.graph.nodes.filter((n) => n.x != null && n.y != null).map((n) => [n.id, { x: n.x!, y: n.y! }])),
)
const computedLayout = computed(() =>
  layout(
    props.graph.nodes.map((n) => ({ id: n.id, group: n.siteId ?? null, kind: n.kind })),
    props.graph.edges.filter((e) => !e.suggestion).map((e) => ({ source: e.source, target: e.target })),
    saved.value,
  ),
)
// Only another set of nodes fits anew (the graph refreshes every minute); the moves stay until saved or discarded.
const nodeSet = computed(() => props.graph.nodes.map((n) => n.id).join(','))
function at(id: string): Point {
  return moved.value.get(id) ?? computedLayout.value.nodes.get(id) ?? { x: 0, y: 0 }
}
const nodes = computed(() =>
  props.graph.nodes.map((n) => ({
    ...n,
    p: at(n.id),
    r: nodeRadius(n.kind),
    unsaved: !!props.editable && n.kind === 'Router' && moved.value.has(n.id),
  })),
)
const byId = computed(() => new Map(nodes.value.map((n) => [n.id, n])))
/** The location circles around where their nodes are now, also while dragging. */
const groups = computed<GroupCircle[]>(() =>
  computedLayout.value.groups.map((g) =>
    encloseGroup(
      g.id,
      nodes.value.filter((n) => n.siteId === g.id).map((n) => ({ x: n.p.x, y: n.p.y, r: bodyRadius(n.kind) })),
    ),
  ),
)
const edges = computed(() =>
  props.graph.edges
    .map((e, i) => {
      const a = byId.value.get(e.source)
      const b = byId.value.get(e.target)
      if (!a || !b) return null
      // Parallel links between the same two nodes bend apart a little.
      const twins = props.graph.edges.filter(
        (x) => (x.source === e.source && x.target === e.target) || (x.source === e.target && x.target === e.source),
      )
      const bend = bendOf(twins.indexOf(e), twins.length)
      return { ...e, a: a.p, b: b.p, bend, key: `${e.id}-${i}` }
    })
    .filter((e): e is NonNullable<typeof e> => e !== null),
)
const neighbours = computed(() => {
  const s = selected.value
  if (s?.kind !== 'node') return null
  const set = new Set([s.id])
  for (const e of props.graph.edges) {
    if (e.source === s.id) set.add(e.target)
    if (e.target === s.id) set.add(e.source)
  }
  return set
})

function path(e: { a: Point; b: Point; bend: number }): string {
  return edgePath(e.a, e.b, e.bend)
}

/** Everything drawn: the circles, and the nodes outside them. */
function bounds() {
  return boundsOf(
    [...groups.value, ...nodes.value.map((n) => ({ x: n.p.x, y: n.p.y, r: bodyRadius(n.kind) }))],
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
      selected.value = { kind: 'node', id }
    }
  },
  { immediate: true },
)

// --- moving (components/graph/useMoving.ts) ---------------------------------------------------------------------------

const { begin, move, end, release, nudge } = useMoving(moved, { at, toWorld: (e) => pan.toWorld(e) })

function downNode(e: PointerEvent, id: string) {
  begin(e, [id])
}
function upNode(id: string) {
  if (release()) selected.value = { kind: 'node', id }
}
/** A location by its name: all its nodes on the map. */
function members(groupId: string): string[] {
  return nodes.value.filter((n) => n.siteId === groupId).map((n) => n.id)
}

function chooseEdge(e: PointerEvent, id: string) {
  e.stopPropagation()
  selected.value = { kind: 'edge', id }
}
const zoom = computed(() => pan.view.value.k)
const transform = computed(() => `translate(${pan.view.value.x} ${pan.view.value.y}) scale(${pan.view.value.k})`)

/** Where every node on the map is now: its move, its saved position, or the layout's. */
function positions(): Map<string, Point> {
  return new Map(nodes.value.map((n) => [n.id, n.p]))
}

defineExpose({ fit, positions })
</script>

<template>
  <div class="map">
    <svg
      ref="svg"
      class="canvas"
      role="img"
      aria-label="Network map"
      data-testid="network-map"
      @wheel="pan.onWheel"
      @pointerdown="pan.onPointerDown"
      @pointermove="pan.onPointerMove"
      @pointerup="pan.onPointerUp"
      @pointercancel="pan.onPointerUp"
      @click.self="selected = null"
    >
      <g :transform="transform">
        <g v-for="g in groups" :key="g.id" class="group">
          <circle :cx="g.x" :cy="g.y" :r="g.r" />
          <text
            :x="g.x"
            :y="g.y - g.r - 8"
            text-anchor="middle"
            role="button"
            tabindex="0"
            :aria-label="`Location ${groupNames.get(g.id) ?? ''}: drag its name or use the arrow keys to move it`"
            :data-group="g.id"
            @pointerdown="(ev: PointerEvent) => begin(ev, members(g.id))"
            @pointermove="move"
            @pointerup="end"
            @pointercancel="end"
            @keydown="(ev: KeyboardEvent) => nudge(ev, members(g.id))"
          >
            {{ groupNames.get(g.id) ?? '' }}
          </text>
        </g>
        <g
          v-for="e in edges"
          :key="e.key"
          class="edge"
          :class="[
            `tone-${edgeTone(e)}`,
            {
              faded: neighbours && !(neighbours.has(e.source) && neighbours.has(e.target)),
              chosen: selected?.kind === 'edge' && selected.id === e.id,
            },
          ]"
          :data-edge="e.id"
          @pointerdown="(ev: PointerEvent) => chooseEdge(ev, e.id)"
        >
          <path class="hit" :d="path(e)" />
          <path class="line" :d="path(e)" :stroke-width="edgeWidth(e.speedMbps)" :stroke-dasharray="edgeDash(e)" />
          <title>
            {{ e.typeLabel ?? 'No type'
            }}{{ e.sourcePort || e.targetPort ? ` · ${e.sourcePort ?? '?'} ↔ ${e.targetPort ?? '?'}` : ''
            }}{{ e.suggestion ? ' (suggestion)' : '' }}
          </title>
          <text
            v-if="zoom > 1.2 && e.typeLabel"
            class="edge-label"
            :x="(e.a.x + e.b.x) / 2"
            :y="(e.a.y + e.b.y) / 2 - 4"
            text-anchor="middle"
          >
            {{ e.typeLabel }}
          </text>
        </g>
        <g
          v-for="n in nodes"
          :key="n.id"
          class="node"
          :class="[
            `state-${n.state}`,
            n.kind === 'Router' ? 'router' : 'host',
            { faded: neighbours && !neighbours.has(n.id), chosen: selected?.kind === 'node' && selected.id === n.id },
          ]"
          :transform="`translate(${n.p.x} ${n.p.y})`"
          :data-node="n.id"
          :data-unsaved="n.unsaved || undefined"
          role="button"
          tabindex="0"
          :aria-label="`${n.kind === 'Router' ? 'Router' : 'Host'} ${n.name}${n.unsaved ? ' (moved, not saved)' : ''}`"
          @pointerdown="(ev: PointerEvent) => downNode(ev, n.id)"
          @pointermove="move"
          @pointerup="upNode(n.id)"
          @pointercancel="end"
          @keydown.enter="selected = { kind: 'node', id: n.id }"
          @keydown="(ev: KeyboardEvent) => nudge(ev, [n.id])"
        >
          <circle v-if="n.unsaved" class="unsaved" :r="n.r + 6" />
          <circle class="body" :r="n.r" />
          <text v-if="nodeGlyph(n.kind, n.os)" class="glyph" text-anchor="middle" dy="4">
            {{ nodeGlyph(n.kind, n.os) }}
          </text>
          <text v-if="zoom > 0.45 || selected?.kind === 'node'" class="label" :y="n.r + 13" text-anchor="middle">
            {{ n.name }}
          </text>
          <text v-if="n.hiddenConnections && zoom > 0.45" class="hidden" :y="n.r + 25" text-anchor="middle">
            +{{ n.hiddenConnections }} not shown
          </text>
        </g>
      </g>
    </svg>
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
        ><svg width="28" height="8"><line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="3" /></svg>
        physical</span
      >
      <span
        ><svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="2" stroke-dasharray="7 5" />
        </svg>
        virtual</span
      >
      <span
        ><svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="2" stroke-dasharray="2 4" />
        </svg>
        Wi-Fi</span
      >
      <span
        ><svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line warn" stroke-width="2" />
        </svg>
        no longer seen</span
      >
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

.group circle {
  fill: color-mix(in srgb, var(--p-primary-color) 4%, transparent);
  stroke: var(--pnmt-muted);
  stroke-dasharray: 6 5;
  stroke-width: 1.2;
}

.group text {
  fill: var(--pnmt-muted);
  font-size: 13px;
  font-weight: 600;
  cursor: move;
}

.group text:focus-visible,
.node:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.edge {
  cursor: pointer;
}

.edge .hit {
  stroke: transparent;
  stroke-width: 12;
  fill: none;
}

.edge .line {
  stroke: var(--pnmt-text);
  opacity: 0.55;
  fill: none;
}

.edge.tone-warn .line {
  stroke: var(--pnmt-warn);
  opacity: 0.9;
}

.edge.tone-muted .line {
  stroke: var(--pnmt-muted);
  opacity: 0.8;
}

.edge.chosen .line {
  opacity: 1;
  stroke: var(--p-primary-color);
}

.edge-label {
  fill: var(--pnmt-muted);
  font-size: 10px;
}

.node {
  cursor: pointer;
}

.node .body {
  fill: var(--pnmt-none-soft);
  stroke: var(--pnmt-none);
  stroke-width: 2;
}

.node.state-Ok .body {
  fill: var(--pnmt-ok-soft);
  stroke: var(--pnmt-ok);
}

.node.state-Failing .body {
  fill: var(--pnmt-fail-soft);
  stroke: var(--pnmt-fail);
}

.node.router .body {
  stroke-width: 3;
}

.node.chosen .body {
  stroke: var(--p-primary-color);
  stroke-width: 4;
}

/* Moved, not saved yet: what Save positions will write. */
.node .unsaved {
  fill: none;
  stroke: var(--p-primary-color);
  stroke-width: 2;
  stroke-dasharray: 4 3;
}

.node .glyph {
  fill: var(--pnmt-text);
  font-size: 12px;
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

.node .hidden {
  fill: var(--pnmt-muted);
  font-size: 9px;
}

.faded {
  opacity: 0.25;
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
</style>
