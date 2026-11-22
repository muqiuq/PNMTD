<script setup lang="ts">
import Button from 'primevue/button'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import type { KbGraph } from '@/api/kb'
import { encloseGroup, layoutGraph, type Point } from '@/components/graph/forceLayout'
import { bendOf, boundsOf, edgePath } from '@/components/graph/geometry'
import { usePanZoom } from '@/composables/usePanZoom'
import { edgeKey, nodeLabel, nodeRadius, LABEL_ROOM } from './knowledgeMap'

/**
 * The knowledge map's drawing (docs/knowledge-base-extensions-plan.md §8.1): Vue-rendered SVG on the network map's layout, geometry
 * and pan/zoom (§8.5). Pages are rounded squares, locations, hosts and sensors circles, hosts and sensors in their state's colour.
 * Page links are solid, links in the text dashed with an arrow, links to objects solid and thin, the structure thin and grey. With
 * `circles`, each top-level location is a dashed circle around its objects. Hovering a node fades what isn't next to it; a click
 * selects a node or an edge. No dragging: the same data gives the same picture.
 */
export type Selection = { kind: 'node'; id: string } | { kind: 'edge'; id: string } | null

const props = defineProps<{ graph: KbGraph; circles?: boolean; focus?: string | null }>()
const selected = defineModel<Selection>('selected', { default: null })

const svg = ref<SVGSVGElement | null>(null)
const pan = usePanZoom(svg)
const groupNames = computed(() => new Map(props.graph.groups.map((g) => [g.id, g.name])))
const computedLayout = computed(() =>
  layoutGraph(
    props.graph.nodes.map((n) => ({
      id: n.id,
      group: props.circles ? (n.group ?? null) : null,
      r: nodeRadius(n.kind) + LABEL_ROOM,
    })),
    props.graph.edges.map((e) => ({ source: e.source, target: e.target })),
  ),
)
const nodes = computed(() =>
  props.graph.nodes.map((n) => ({
    ...n,
    p: computedLayout.value.nodes.get(n.id) ?? { x: 0, y: 0 },
    r: nodeRadius(n.kind),
    label: nodeLabel(n),
  })),
)
const byId = computed(() => new Map(nodes.value.map((n) => [n.id, n])))
const groups = computed(() =>
  props.circles
    ? computedLayout.value.groups.map((g) =>
        encloseGroup(
          g.id,
          nodes.value.filter((n) => n.group === g.id).map((n) => ({ x: n.p.x, y: n.p.y, r: n.r + LABEL_ROOM })),
        ),
      )
    : [],
)
const edges = computed(() =>
  props.graph.edges
    .map((e) => {
      const a = byId.value.get(e.source)
      const b = byId.value.get(e.target)
      if (!a || !b) return null
      const twins = props.graph.edges.filter(
        (x) => (x.source === e.source && x.target === e.target) || (x.source === e.target && x.target === e.source),
      )
      // An arrow stops at the target's edge, not its centre.
      const d = Math.max(1, Math.hypot(b.p.x - a.p.x, b.p.y - a.p.y))
      const end =
        e.kind === 'TextLink'
          ? { x: b.p.x - ((b.p.x - a.p.x) / d) * (b.r + 3), y: b.p.y - ((b.p.y - a.p.y) / d) * (b.r + 3) }
          : b.p
      return { ...e, key: edgeKey(e), a: a.p, b: end, bend: bendOf(twins.indexOf(e), twins.length) }
    })
    .filter((e): e is NonNullable<typeof e> => e !== null),
)
const hovered = ref<string | null>(null)
const near = computed(() => {
  const id = hovered.value ?? (selected.value?.kind === 'node' ? selected.value.id : null)
  if (!id) return null
  const set = new Set([id])
  for (const e of props.graph.edges) {
    if (e.source === id) set.add(e.target)
    if (e.target === id) set.add(e.source)
  }
  return set
})

function fit() {
  pan.fit(
    boundsOf(
      [...groups.value, ...nodes.value.map((n) => ({ x: n.p.x, y: n.p.y, r: n.r + LABEL_ROOM }))],
      computedLayout.value.bounds,
    ),
  )
}
const nodeSet = computed(() => props.graph.nodes.map((n) => n.id).join(','))
onMounted(() => nextTick(fit))
watch([nodeSet, () => props.circles], () => nextTick(fit))
watch(
  () => [props.focus, nodeSet.value] as const,
  ([id]) => {
    const node = id ? byId.value.get(id) : undefined
    if (node) {
      void nextTick(() => pan.centre(node.p))
      selected.value = { kind: 'node', id: node.id }
    }
  },
  { immediate: true },
)
function chooseNode(e: PointerEvent, id: string) {
  e.stopPropagation()
  selected.value = { kind: 'node', id }
}
function chooseEdge(e: PointerEvent, key: string) {
  e.stopPropagation()
  selected.value = { kind: 'edge', id: key }
}
const transform = computed(() => `translate(${pan.view.value.x} ${pan.view.value.y}) scale(${pan.view.value.k})`)
const pathOf = (e: { a: Point; b: Point; bend: number }) => edgePath(e.a, e.b, e.bend)
const glyph: Record<string, string> = { Page: 'KB', Location: 'L', Host: 'H', Sensor: 'S' }
defineExpose({ fit })
</script>

<template>
  <div class="map">
    <svg
      ref="svg"
      class="canvas"
      role="img"
      aria-label="Knowledge map"
      data-testid="knowledge-map"
      @wheel="pan.onWheel"
      @pointerdown="pan.onPointerDown"
      @pointermove="pan.onPointerMove"
      @pointerup="pan.onPointerUp"
      @pointercancel="pan.onPointerUp"
      @click.self="selected = null"
    >
      <defs>
        <marker
          id="kbmap-arrow"
          viewBox="0 0 10 10"
          refX="9"
          refY="5"
          markerWidth="7"
          markerHeight="7"
          orient="auto-start-reverse"
        >
          <path d="M 0 0 L 10 5 L 0 10 z" class="arrow" />
        </marker>
      </defs>
      <g :transform="transform">
        <g v-for="g in groups" :key="g.id" class="group">
          <circle :cx="g.x" :cy="g.y" :r="g.r" />
          <text :x="g.x" :y="g.y - g.r - 8" text-anchor="middle">{{ groupNames.get(g.id) ?? '' }}</text>
        </g>
        <g
          v-for="e in edges"
          :key="e.key"
          class="edge"
          :class="[
            `kind-${e.kind}`,
            {
              faded: near && !(near.has(e.source) && near.has(e.target)),
              chosen: selected?.kind === 'edge' && selected.id === e.key,
            },
          ]"
          :data-edge="e.key"
          @pointerdown="(ev: PointerEvent) => chooseEdge(ev, e.key)"
        >
          <path class="hit" :d="pathOf(e)" />
          <path class="line" :d="pathOf(e)" :marker-end="e.kind === 'TextLink' ? 'url(#kbmap-arrow)' : undefined" />
        </g>
        <g
          v-for="n in nodes"
          :key="n.id"
          class="node"
          :class="[
            `kind-${n.kind}`,
            n.state ? `state-${n.state}` : '',
            n.status ? `status-${n.status}` : '',
            {
              faded: near && !near.has(n.id),
              chosen: selected?.kind === 'node' && selected.id === n.id,
              archived: n.archived,
            },
          ]"
          role="button"
          tabindex="0"
          :aria-label="n.label"
          :data-node="n.id"
          @pointerdown="(ev: PointerEvent) => chooseNode(ev, n.id)"
          @pointerenter="hovered = n.id"
          @pointerleave="hovered = null"
          @keydown.enter="selected = { kind: 'node', id: n.id }"
        >
          <rect
            v-if="n.kind === 'Page'"
            class="body"
            :x="n.p.x - n.r"
            :y="n.p.y - n.r"
            :width="n.r * 2"
            :height="n.r * 2"
            rx="4"
          />
          <circle v-else class="body" :cx="n.p.x" :cy="n.p.y" :r="n.r" />
          <text class="glyph" :x="n.p.x" :y="n.p.y + 4" text-anchor="middle">{{ glyph[n.kind] }}</text>
          <text class="label" :x="n.p.x" :y="n.p.y + n.r + 13" text-anchor="middle">{{ n.label }}</text>
          <title>{{ n.label }}{{ n.detail && n.kind !== 'Page' ? ` · ${n.detail}` : '' }}</title>
        </g>
      </g>
    </svg>
    <div class="controls">
      <Button icon="pi pi-plus" size="small" severity="secondary" aria-label="Zoom in" @click="pan.zoomBy(1.25)" />
      <Button icon="pi pi-minus" size="small" severity="secondary" aria-label="Zoom out" @click="pan.zoomBy(0.8)" />
      <Button label="Fit" size="small" severity="secondary" @click="fit" />
    </div>
    <div class="legend muted">
      <span
        ><svg width="28" height="8"><line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="2.5" /></svg>
        linked pages</span
      >
      <span
        ><svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="1.5" stroke-dasharray="5 4" />
        </svg>
        in the text</span
      >
      <span
        ><svg width="28" height="8"><line x1="0" y1="4" x2="28" y2="4" class="legend-line" stroke-width="1.2" /></svg>
        linked object</span
      >
      <span
        ><svg width="28" height="8">
          <line x1="0" y1="4" x2="28" y2="4" class="legend-line muted" stroke-width="1" />
        </svg>
        belongs to</span
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
  height: calc(100vh - 15rem);
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
}

.arrow {
  fill: var(--pnmt-muted);
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

.edge.kind-PageLink .line {
  stroke-width: 2.5;
}

.edge.kind-TextLink .line {
  stroke-width: 1.5;
  stroke-dasharray: 5 4;
}

.edge.kind-ObjectLink .line {
  stroke-width: 1.2;
}

.edge.kind-Structure .line {
  stroke: var(--pnmt-muted);
  stroke-width: 1;
  opacity: 0.6;
}

.edge.chosen .line {
  opacity: 1;
  stroke: var(--p-primary-color);
}

.node {
  cursor: pointer;
}

.node .body {
  fill: var(--pnmt-none-soft);
  stroke: var(--pnmt-none);
  stroke-width: 2;
}

.node.kind-Page .body {
  fill: color-mix(in srgb, var(--p-primary-color) 12%, var(--pnmt-panel));
  stroke: var(--p-primary-color);
}

.node.kind-Page.status-Draft .body {
  fill: var(--pnmt-none-soft);
  stroke: var(--pnmt-muted);
}

.node.kind-Page.status-NeedsReview .body {
  fill: var(--pnmt-warn-soft);
  stroke: var(--pnmt-warn);
}

.node.kind-Location .body {
  fill: var(--pnmt-panel);
  stroke: var(--pnmt-muted);
  stroke-dasharray: 3 2;
}

.node.state-Ok .body {
  fill: var(--pnmt-ok-soft);
  stroke: var(--pnmt-ok);
}

.node.state-Failing .body {
  fill: var(--pnmt-fail-soft);
  stroke: var(--pnmt-fail);
}

.node.archived {
  opacity: 0.5;
}

.node.chosen .body {
  stroke: var(--p-primary-color);
  stroke-width: 4;
}

.node:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.node .glyph {
  fill: var(--pnmt-text);
  font-size: 10px;
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

.faded {
  opacity: 0.2;
}

.controls {
  position: absolute;
  right: var(--pnmt-space-2);
  top: var(--pnmt-space-2);
  display: flex;
  gap: var(--pnmt-space-1);
}

.legend {
  position: absolute;
  left: var(--pnmt-space-2);
  bottom: var(--pnmt-space-2);
  display: flex;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
  font-size: 0.857rem;
  background: color-mix(in srgb, var(--pnmt-panel) 85%, transparent);
  padding: 2px var(--pnmt-space-2);
  border-radius: 4px;
}

.legend-line {
  stroke: var(--pnmt-text);
  opacity: 0.6;
}

.legend-line.muted {
  stroke: var(--pnmt-muted);
}
</style>
