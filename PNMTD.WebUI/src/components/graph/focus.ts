/**
 * A node in the URL, `?focus=router:<id>` or `?focus=page:42` (docs/network-map-plan.md §8.2, docs/knowledge-base-extensions-plan.md
 * §8.2): one of the map's kinds and an id (a UUID, or a number for pages), lower case. Anything else: no focus.
 */
export function parseFocus<K extends string>(
  value: unknown,
  kinds: readonly K[],
  numbered: readonly K[] = [],
): { kind: K; id: string } | null {
  if (typeof value !== 'string') return null
  const match = /^([a-z]+):([0-9a-f-]{36}|\d+)$/i.exec(value.trim())
  if (!match) return null
  const kind = match[1]!.toLowerCase() as K
  const id = match[2]!.toLowerCase()
  if (!kinds.includes(kind)) return null
  const isNumber = /^\d+$/.test(id)
  return isNumber === numbered.includes(kind) ? { kind, id } : null
}
