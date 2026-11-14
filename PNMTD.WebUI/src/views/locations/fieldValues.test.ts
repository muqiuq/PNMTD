import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  formatValue,
  grouped,
  hostId,
  isEmpty,
  locationMarkdown,
  valueHref,
  valueMarkdown,
  type FieldDefinition,
  type FieldType,
} from './fieldValues'

const field = (type: FieldType, patch: Partial<FieldDefinition> = {}): FieldDefinition => ({
  id: `${type}-id`,
  key: type.toLowerCase(),
  label: type,
  type,
  help: null,
  group: null,
  position: 0,
  overview: false,
  inherited: false,
  sensitive: false,
  options: null,
  archivedAt: null,
  createdAt: '2026-11-14T00:00:00Z',
  filled: 0,
  ...patch,
})

describe('location field values', () => {
  it('formats each type', () => {
    expect(formatValue(field('YesNo'), false)).toBe('No')
    expect(formatValue(field('Number', { options: { unit: 'kW', decimals: 1 } }), 12.5)).toMatch(/^12[.,]5 kW$/)
    expect(formatValue(field('Choices'), ['Colo', 'Office'])).toBe('Colo, Office')
    expect(formatValue(field('Link'), { url: 'https://vault.example.com', label: 'Door codes' })).toBe('Door codes')
    expect(formatValue(field('Link'), { url: 'https://vault.example.com' })).toBe('https://vault.example.com')
    expect(formatValue(field('User'), 'a-guid', { name: 'Anna Muster' })).toBe('Anna Muster')
    expect(formatValue(field('Date'), '2026-11-14')).toMatch(/2026/)
    expect(formatValue(field('Text'), null)).toBe('')
  })

  it('links where a value points', () => {
    expect(valueHref(field('Link'), { url: 'https://x.example' })).toBe('https://x.example')
    expect(valueHref(field('Link'), { url: 'javascript:alert(1)' })).toBeNull()
    expect(valueHref(field('Email'), 'noc@example.ch')).toBe('mailto:noc@example.ch')
    expect(valueHref(field('Phone'), '+41 (44) 123-45/67')).toBe('tel:+41441234567')
    expect(valueHref(field('Text'), 'x')).toBeNull()
    expect(hostId(field('Host'), 'h1')).toBe('h1')
  })

  it('knows empty values', () => {
    expect(isEmpty([])).toBe(true)
    expect(isEmpty('')).toBe(true)
    expect(isEmpty(false)).toBe(false)
    expect(isEmpty(0)).toBe(false)
  })

  it('writes a location as one Markdown file', () => {
    const overview = field('Markdown', { id: 'o', label: 'Overview' })
    const link = field('Link', { id: 'l', label: 'Vault' })
    const hours = field('Text', { id: 'h', label: 'Opening hours', inherited: true })
    const empty = field('Text', { id: 'e', label: 'Empty' })
    const value = (fieldId: string, v: unknown, from: string | null = null): S['LocationValueItem'] => ({
      fieldId,
      value: v,
      inheritedFrom: from ? { id: 'p', name: from } : null,
      revision: 1,
      updatedAt: null,
      updatedByName: null,
      target: null,
    })
    const text = locationMarkdown(
      {
        path: 'Zurich DC › Cage B',
        description: 'Rack 4–6',
        values: [
          value('o', '## Access\n\nBadges at the desk.\n'),
          value('l', { url: 'https://v.example/z' }),
          value('h', '8–17', 'Zurich DC'),
          value('e', null),
        ],
      },
      [overview, link, hours, empty],
    )
    expect(text).toBe(
      '# Zurich DC › Cage B\n\nRack 4–6\n\n## Overview\n\n## Access\n\nBadges at the desk.\n\n## Vault\n\n[https://v.example/z](<https://v.example/z>)\n\n## Opening hours\n\n8–17\n\n*From Zurich DC.*\n',
    )
    expect(valueMarkdown(field('Text'), 'a *b*')).toBe('a \\*b\\*')
  })

  it('groups fields, those without a group first', () => {
    const groups = grouped([
      field('Text', { group: 'Network' }),
      field('Text', { group: null }),
      field('Text', { group: 'Access' }),
      field('Text', { group: 'Network' }),
    ])
    expect(groups.map((g) => [g.group, g.fields.length])).toEqual([
      [null, 1],
      ['Network', 2],
      ['Access', 1],
    ])
  })
})
