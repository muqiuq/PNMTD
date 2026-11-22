import { layoutGraph, type Layout, type LayoutEdge, type Point } from '@/components/graph/forceLayout'

/**
 * The network map's layout (docs/network-map-plan.md §8.2): routers and hosts in their location circles, on the shared force layout
 * (src/components/graph/forceLayout.ts), with a router larger than a host. Saved routers (docs/network-map-positions-plan.md §4) stay
 * where they were saved.
 */
export {
  encloseGroup,
  separate,
  seeded,
  type GroupCircle,
  type Layout,
  type LayoutEdge,
  type Point,
} from '@/components/graph/forceLayout'

export interface LayoutNode {
  id: string
  /** The top-level location (the circle); null: outside every circle. */
  group: string | null
  kind: 'Router' | 'Host'
}

/** A node's radius on the map: a router larger than a host. */
export function nodeRadius(kind: LayoutNode['kind']): number {
  return kind === 'Router' ? 14 : 10
}

/** Room around a node for its name below it. */
const LABEL_ROOM = 26

/** The room a node takes in its group: its circle and its name. */
export function bodyRadius(kind: LayoutNode['kind']): number {
  return nodeRadius(kind) + LABEL_ROOM
}

export function layout(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  saved: ReadonlyMap<string, Point> = new Map(),
): Layout {
  return layoutGraph(
    nodes.map((n) => ({ id: n.id, group: n.group, r: bodyRadius(n.kind) })),
    edges,
    saved,
  )
}
