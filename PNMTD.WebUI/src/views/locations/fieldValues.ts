import type { S } from '@/api/resources'
import { formatDay } from '@/format'
import { mdText } from '@/format/markdown'

/**
 * Location fields' values in the browser (docs/locations-knowledge-plan.md §4.1): their JSON per type, as
 * Contracts/Locations.cs documents it, formatted for the cards, linked where they point somewhere, and written as one
 * Markdown file for *Download as Markdown* (§3.4).
 */

/** The schema's enum is nullable, as a history row's field type is. */
export type FieldType = NonNullable<S['LocationFieldType']>
export type FieldDefinition = S['LocationFieldItem']
export type FieldValue = S['LocationValueItem']

/** A Link value. */
export interface LinkValue {
  url: string
  label?: string | null
}

/** The types in the order of §4.1, with what an Admin reads in the field dialog. */
export const fieldTypes: { value: FieldType; label: string; icon: string; hint: string }[] = [
  { value: 'Markdown', label: 'Markdown', icon: 'pi pi-align-left', hint: 'Text with images, files and diagrams' },
  { value: 'Text', label: 'Text', icon: 'pi pi-pencil', hint: 'One line, at most 512 characters' },
  {
    value: 'Number',
    label: 'Number',
    icon: 'pi pi-hashtag',
    hint: 'With an optional unit, minimum, maximum and decimals',
  },
  { value: 'YesNo', label: 'Yes/No', icon: 'pi pi-check-square', hint: 'Yes or no' },
  { value: 'Date', label: 'Date', icon: 'pi pi-calendar', hint: 'A day' },
  { value: 'Choice', label: 'Choice', icon: 'pi pi-list', hint: 'One of a list' },
  { value: 'Choices', label: 'Choices', icon: 'pi pi-list-check', hint: 'Several of a list' },
  { value: 'Link', label: 'Link', icon: 'pi pi-link', hint: 'An http(s) address with an optional label' },
  { value: 'Email', label: 'E-mail', icon: 'pi pi-envelope', hint: 'An e-mail address' },
  { value: 'Phone', label: 'Phone', icon: 'pi pi-phone', hint: 'A number as written' },
  { value: 'User', label: 'PNMT user', icon: 'pi pi-user', hint: 'Shown by display name' },
  { value: 'Host', label: 'Host', icon: 'pi pi-server', hint: 'A link to the host' },
]

export function typeLabel(type: FieldType | null): string {
  return fieldTypes.find((t) => t.value === type)?.label ?? type ?? ''
}

export function typeIcon(type: FieldType | null): string {
  return fieldTypes.find((t) => t.value === type)?.icon ?? 'pi pi-question'
}

/** Short types are edited in place; Markdown on the editing page (§5.9). */
export function isShort(type: FieldType | null): boolean {
  return type !== 'Markdown'
}

export function isEmpty(value: unknown): boolean {
  return value === null || value === undefined || value === '' || (Array.isArray(value) && value.length === 0)
}

function asLink(value: unknown): LinkValue | null {
  if (typeof value === 'string') {
    return { url: value }
  }
  if (value && typeof value === 'object' && typeof (value as LinkValue).url === 'string') {
    return value as LinkValue
  }
  return null
}

/** A number as written in the browser's language, with the field's unit: <i>12.5 kW</i>. */
export function formatNumber(value: number, options?: S['LocationFieldOptions'] | null): string {
  const text = value.toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: options?.decimals ?? 6,
  })
  return options?.unit ? `${text} ${options.unit}` : text
}

/**
 * A value as one line of text. <code>target</code> is the API's name for a User or Host value; without it, the id.
 * Empty values give the empty string.
 */
export function formatValue(
  field: Pick<FieldDefinition, 'type' | 'options'>,
  value: unknown,
  target?: { name: string } | null,
): string {
  if (isEmpty(value)) {
    return ''
  }
  switch (field.type) {
    case 'YesNo':
      return value ? 'Yes' : 'No'
    case 'Number':
      return typeof value === 'number' ? formatNumber(value, field.options) : String(value)
    case 'Date':
      return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) ? formatDay(value) : String(value)
    case 'Choices':
      return Array.isArray(value) ? value.join(', ') : String(value)
    case 'Link': {
      const link = asLink(value)
      return link ? (link.label ?? link.url) : ''
    }
    case 'User':
    case 'Host':
      return target?.name ?? String(value)
    default:
      return String(value)
  }
}

/**
 * Where a value leads (§3.4): a link opens in a new tab, an e-mail address is mailto:, a phone tel:. A host's page is a
 * route, not an address: see <code>hostId</code>.
 */
export function valueHref(field: Pick<FieldDefinition, 'type'>, value: unknown): string | null {
  if (isEmpty(value)) {
    return null
  }
  switch (field.type) {
    case 'Link': {
      const url = asLink(value)?.url
      return url && /^https?:\/\//i.test(url) ? url : null
    }
    case 'Email':
      return `mailto:${String(value)}`
    case 'Phone':
      return `tel:${String(value).replace(/[^\d+]/g, '')}`
    default:
      return null
  }
}

/** A Host value's host. */
export function hostId(field: Pick<FieldDefinition, 'type'>, value: unknown): string | null {
  return field.type === 'Host' && typeof value === 'string' && value ? value : null
}

/** A value as Markdown, for the export: Markdown as it is, links as links, the rest as text. */
export function valueMarkdown(
  field: Pick<FieldDefinition, 'type' | 'options'>,
  value: unknown,
  target?: { name: string } | null,
): string {
  if (isEmpty(value)) {
    return ''
  }
  if (field.type === 'Markdown') {
    return String(value).replace(/\s+$/, '')
  }
  if (field.type === 'Link') {
    const link = asLink(value)
    return link ? `[${mdText(link.label ?? link.url)}](<${link.url}>)` : ''
  }
  if (field.type === 'Email') {
    return `<${String(value)}>`
  }
  return mdText(formatValue(field, value, target))
}

/**
 * *Download as Markdown* (§3.4): the location as one file, its path as the title, then every field in order, each a
 * section; inherited values say where they come from, empty fields are left out.
 */
export function locationMarkdown(
  location: Pick<S['LocationDetail'], 'path' | 'description' | 'values'>,
  fields: readonly FieldDefinition[],
): string {
  const values = new Map(location.values.map((v) => [v.fieldId, v]))
  const parts = [`# ${mdText(location.path)}`]
  if (location.description) {
    parts.push(mdText(location.description))
  }
  for (const field of fields) {
    const value = values.get(field.id)
    if (field.archivedAt || !value || isEmpty(value.value)) {
      continue
    }
    const body = valueMarkdown(field, value.value, value.target)
    const from = value.inheritedFrom ? `\n\n*From ${mdText(value.inheritedFrom.name)}.*` : ''
    parts.push(`## ${mdText(field.label)}\n\n${body}${from}`)
  }
  return parts.join('\n\n') + '\n'
}

/** The fields by their groups, in the fields' order; fields without a group first (§3.4's *Information*). */
export function grouped<T extends Pick<FieldDefinition, 'group'>>(
  fields: readonly T[],
): { group: string | null; fields: T[] }[] {
  const groups: { group: string | null; fields: T[] }[] = []
  for (const field of fields) {
    const name = field.group ?? null
    let entry = groups.find((g) => g.group === name)
    if (!entry) {
      entry = { group: name, fields: [] }
      if (name === null) {
        groups.unshift(entry)
      } else {
        groups.push(entry)
      }
    }
    entry.fields.push(field)
  }
  return groups
}

/** A sample value per type, for the field dialog's and the admin page's previews (§10.5). */
export function sampleValue(field: Pick<FieldDefinition, 'type' | 'options' | 'label'>): unknown {
  switch (field.type) {
    case 'Markdown':
      return 'Rack 4–6, cage B. Badges at the **front desk**; ask for the IT cage.'
    case 'Text':
      return 'Init7 · 12-3456'
    case 'Number':
      return Math.min(Math.max(12.5, field.options?.min ?? -Infinity), field.options?.max ?? Infinity)
    case 'YesNo':
      return true
    case 'Date':
      return '2026-11-14'
    case 'Choice':
      return field.options?.choices?.[0] ?? 'Option'
    case 'Choices':
      return field.options?.choices?.slice(0, 2) ?? ['Option']
    case 'Link':
      return { url: 'https://vault.example.com/zrh', label: 'Door codes' }
    case 'Email':
      return 'noc@example.com'
    case 'Phone':
      return '+41 44 123 45 67'
    case 'User':
      return '00000000-0000-0000-0000-000000000001'
    case 'Host':
      return '00000000-0000-0000-0000-000000000002'
  }
}

/** The name a sample User or Host value shows. */
export function sampleTarget(type: FieldType | null): { id: string; name: string } | null {
  return type === 'User'
    ? { id: '00000000-0000-0000-0000-000000000001', name: 'Anna Muster' }
    : type === 'Host'
      ? { id: '00000000-0000-0000-0000-000000000002', name: 'fw-zrh-01' }
      : null
}
