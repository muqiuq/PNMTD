import type { BoardColumn, BoardElement, BoardElementSettings, BoardElementType, BoardLayout } from './types'

/**
 * A layout's pure helpers (docs/boards-plan.md §6.2, §10.3), for the editor: new elements, columns added and taken away, elements moved.
 * Each returns a new layout; the server validates and keeps only the settings a type reads.
 */

export const maxColumns = 4
export const maxElementsPerColumn = 12
export const maxElements = 30

/** Every setting at the server's default: what a type doesn't read is dropped on save. */
export function blankSettings(): BoardElementSettings {
  return {
    rows: null,
    importance: null,
    locationId: null,
    messages: false,
    ignored: false,
    onlyProblems: false,
    tiles: false,
    targets: null,
    perRow: null,
    projectId: null,
    subjects: true,
    redmineTiles: null,
    redmineLists: null,
    newMinutes: null,
    yesterday: false,
    yellow: null,
    red: null,
    severity: null,
    groups: null,
    suppressed: false,
    windowHours: null,
    comments: true,
    seconds: false,
    date: true,
    zones: null,
    text: null,
    center: false,
    large: false,
    days: null,
    timeFormat: null,
    dateFormat: null,
    weekNumber: null,
    deputies: null,
    dutyLabel: null,
    deputyLabel: null,
    nobodyText: null,
    closedText: null,
    role: true,
    where: false,
    dayComment: false,
    tomorrow: false,
    textSize: null,
    oneLine: false,
    monospace: false,
    stateColour: null,
    age: true,
    fileId: null,
    imageFit: null,
    background: null,
    caption: null,
    alt: null,
  }
}

/** 8 letters and digits, as the server checks them. */
export function newId(): string {
  const bytes = new Uint8Array(4)
  crypto.getRandomValues(bytes)
  return [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('')
}

/** A new element; a new "On duty today" shows one deputy (docs/board-messages-plan.md §9.5), one from 1.23 none. */
export function newElement(type: BoardElementType): BoardElement {
  const settings = blankSettings()
  if (type === 'OnDuty') settings.deputies = 1
  return { id: newId(), type, title: null, showTitle: true, height: null, settings }
}

export function elements(layout: BoardLayout): BoardElement[] {
  return layout.columns.flatMap((c) => c.elements)
}

export function find(layout: BoardLayout, id: string): { column: number; index: number; element: BoardElement } | null {
  for (const [column, c] of layout.columns.entries()) {
    const index = c.elements.findIndex((e) => e.id === id)
    if (index >= 0) return { column, index, element: c.elements[index]! }
  }
  return null
}

/**
 * 1 to 4 columns (§10.3): new ones come empty; the elements of columns taken away move to the end of the last one that stays, so
 * nothing is lost.
 */
export function setColumns(layout: BoardLayout, count: number): BoardLayout {
  const n = Math.max(1, Math.min(maxColumns, Math.round(count)))
  const columns: BoardColumn[] = layout.columns.slice(0, n).map((c) => ({ ...c, elements: [...c.elements] }))
  while (columns.length < n) columns.push({ width: 1, elements: [] })
  const moved = layout.columns.slice(n).flatMap((c) => c.elements)
  if (moved.length) columns[n - 1] = { ...columns[n - 1]!, elements: [...columns[n - 1]!.elements, ...moved] }
  return { ...layout, columns }
}

/** The element moved to a column, at an index there (the end when beyond it). */
export function move(layout: BoardLayout, id: string, toColumn: number, toIndex: number): BoardLayout {
  const at = find(layout, id)
  if (!at || toColumn < 0 || toColumn >= layout.columns.length) return layout
  const columns = layout.columns.map((c) => ({ ...c, elements: c.elements.filter((e) => e.id !== id) }))
  const target = columns[toColumn]!
  const index = Math.max(0, Math.min(toIndex, target.elements.length))
  target.elements.splice(index, 0, at.element)
  return { ...layout, columns }
}

/** One step up or down within its column; across into the neighbour's end or start at the edge is the drag's job. */
export function step(layout: BoardLayout, id: string, delta: -1 | 1): BoardLayout {
  const at = find(layout, id)
  if (!at) return layout
  return move(layout, id, at.column, at.index + delta)
}

export function add(layout: BoardLayout, column: number, element: BoardElement): BoardLayout {
  return {
    ...layout,
    columns: layout.columns.map((c, i) => (i === column ? { ...c, elements: [...c.elements, element] } : c)),
  }
}

export function remove(layout: BoardLayout, id: string): BoardLayout {
  return { ...layout, columns: layout.columns.map((c) => ({ ...c, elements: c.elements.filter((e) => e.id !== id) })) }
}

export function replace(layout: BoardLayout, element: BoardElement): BoardLayout {
  return {
    ...layout,
    columns: layout.columns.map((c) => ({
      ...c,
      elements: c.elements.map((e) => (e.id === element.id ? element : e)),
    })),
  }
}

/** Room for one more element in this column, and on the board. */
export function canAdd(layout: BoardLayout, column: number): boolean {
  return (
    elements(layout).length < maxElements &&
    (layout.columns[column]?.elements.length ?? maxElementsPerColumn) < maxElementsPerColumn
  )
}

/**
 * The root font size of a board (§6.3): fit to window scales with the screen's height, so that a board looks the same on an HD and a 4K
 * screen (1080 px high is 16 px at 100 %); a page that scrolls is 16 px. Then the board's text size.
 */
export function fontSize(fit: boolean, height: number, textScale: number): number {
  const base = fit ? (Math.max(240, height) / 1080) * 16 : 16
  return Math.round(base * (Math.max(50, Math.min(300, textScale)) / 100) * 100) / 100
}
