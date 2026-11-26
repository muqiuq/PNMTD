/**
 * The server's answer to a failed Save of the plan's settings, shown only while it is still about what is on screen
 * (docs/availability-extensions-plan.md §3.2). Before 1.28 a message such as "Periods may not overlap." stayed until the
 * next Save, also after the periods had been fixed. Now each field's messages go with the part of the draft they are
 * about, as it was sent; a change to that part hides them, and the browser's own checks (hours.ts) carry on.
 */

/** The parts of the settings' draft that the server's fields name. */
export interface SettingsParts {
  types: readonly unknown[]
  countColumns: readonly { id: string }[]
  week: readonly { day: string }[]
  holidays: readonly { date: string }[]
  globalFallback: unknown
  responsibleLabel: string
}

const json = (value: unknown) => JSON.stringify(value ?? null)

/**
 * What a field is about, as text to compare: `week.Monday` is Monday's periods, `holidays.2026-12-24` the holidays on that
 * date, `types.3` the fourth type, `countColumns.{id}` that column; a section's name the whole section. A field the
 * settings don't know is about all of them.
 */
export function partOf(parts: SettingsParts, field: string): string {
  const dot = field.indexOf('.')
  const head = dot < 0 ? field : field.slice(0, dot)
  const key = dot < 0 ? null : field.slice(dot + 1)
  switch (head) {
    case 'types':
      return json(key === null ? parts.types : parts.types[Number(key)])
    case 'countColumns':
      return json(key === null ? parts.countColumns : parts.countColumns.find((c) => c.id === key))
    case 'week':
      return json(key === null ? parts.week : parts.week.find((d) => d.day === key))
    case 'holidays':
      return json(key === null ? parts.holidays : parts.holidays.filter((h) => h.date === key))
    case 'globalFallback':
      return json(parts.globalFallback)
    case 'responsibleLabel':
      return json(parts.responsibleLabel)
    default:
      return json(parts)
  }
}

/** Each field with a message, and what it was about when it was sent. */
export function sentParts(errors: Readonly<Record<string, string[]>>, parts: SettingsParts): Record<string, string> {
  return Object.fromEntries(Object.keys(errors).map((field) => [field, partOf(parts, field)]))
}

/** The messages whose part of the draft is still as it was sent. */
export function currentErrors(
  errors: Readonly<Record<string, string[]>>,
  sent: Readonly<Record<string, string>>,
  parts: SettingsParts,
): Record<string, string[]> {
  return Object.fromEntries(Object.entries(errors).filter(([field]) => sent[field] === partOf(parts, field)))
}
