import type { ResponsibilityGlobal, ResponsibilityItem } from '@/api/responsibilities'

/**
 * Sorting the list by a column (docs/responsibilities-extensions-plan.md §3): in the browser, since the list isn't paged; kept in the
 * URL as ?sort=responsible or ?sort=-changed. Without a sort, the server's order: by name.
 */

export const sortFields = ['name', 'responsible', 'deputy', 'links', 'redmine', 'changed'] as const
export type SortField = (typeof sortFields)[number]

export interface ListSort {
  field: SortField
  /** 1 ascending, -1 descending. */
  order: 1 | -1
}

/** "responsible" or "-changed" from the URL; null for none or a value the list doesn't know. */
export function parseSort(value: unknown): ListSort | null {
  if (typeof value !== 'string' || !value) return null
  const descending = value.startsWith('-')
  const field = (descending ? value.slice(1) : value) as SortField
  return sortFields.includes(field) ? { field, order: descending ? -1 : 1 } : null
}

export function formatSort(sort: ListSort | null): string | undefined {
  return sort ? `${sort.order === -1 ? '-' : ''}${sort.field}` : undefined
}

/** As people read names: case-insensitive, with numbers as numbers (Backup 2 before Backup 10). */
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })

type Value = string | number | null

/** What a row sorts by in a column; null for nobody, which comes last in both directions. */
function valueOf(
  item: ResponsibilityItem,
  field: SortField,
  global: Pick<ResponsibilityGlobal, 'responsible' | 'deputy'>,
): Value {
  switch (field) {
    case 'name':
      return item.name
    case 'responsible':
      // Where the global responsible stands in, the cell names them (or the global deputy): it sorts by that name.
      if (item.globalStandsIn) return global.responsible?.name ?? global.deputy?.name ?? null
      return item.responsible?.name ?? null
    case 'deputy':
      return item.globalStandsIn ? null : (item.deputy?.name ?? null)
    case 'links':
      return item.links
    case 'redmine':
      return item.combinations
    case 'changed':
      return Date.parse(item.updatedAt)
  }
}

function compare(a: Value, b: Value): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b
  return collator.compare(String(a), String(b))
}

/** The rows in the column's order, nobody last in both directions, ties by name; the rows as they came without a sort. */
export function sortItems(
  items: readonly ResponsibilityItem[],
  sort: ListSort | null,
  global: Pick<ResponsibilityGlobal, 'responsible' | 'deputy'>,
): ResponsibilityItem[] {
  if (!sort) return [...items]
  return [...items].sort((x, y) => {
    const a = valueOf(x, sort.field, global)
    const b = valueOf(y, sort.field, global)
    if (a === null || b === null) {
      if (a !== b) return a === null ? 1 : -1
    } else {
      const c = compare(a, b) * sort.order
      if (c !== 0) return c
    }
    return collator.compare(x.name, y.name)
  })
}
