import { describe, expect, it } from 'vitest'
import { missingPlaceholders, newId, normalizeLevels, setOverride, withUnknown } from './cookieAdmin'

describe('Admin → Cookie', () => {
  it('keeps only the prompts that differ from their default (docs/cookie-prompts.md)', () => {
    const texts: Record<string, string> = {}
    setOverride(texts, 'status', 'Changed {state} {cases}', 'Default {state} {cases}')
    expect(texts).toEqual({ status: 'Changed {state} {cases}' })
    setOverride(texts, 'status', 'Default {state} {cases}', 'Default {state} {cases}')
    expect(texts).toEqual({})
  })

  it('names the required placeholders a text has lost', () => {
    expect(missingPlaceholders('{state} only', ['{state}', '{cases}'])).toEqual(['{cases}'])
    expect(missingPlaceholders('{cases} moved before {state}', ['{state}', '{cases}'])).toEqual([])
  })

  it('shows chosen values a list lacks, once', () => {
    const options = withUnknown([{ label: 'Ops', value: 'a' }], ['a', 'b', 'b', null], (v) => `Group ${v}`)
    expect(options).toEqual([
      { label: 'Ops', value: 'a' },
      { label: 'Group b', value: 'b' },
    ])
  })

  it('makes version-4 ids', () => {
    expect(newId()).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(newId()).not.toBe(newId())
  })

  it('gives every action a level, the defaults of §21.1 for those the settings lack', () => {
    const levels = normalizeLevels({ pushover: { level: 'Auto', minConfidence: 0.9 } })
    expect(levels.pushover).toEqual({ level: 'Auto', minConfidence: 0.9 })
    expect(levels.email).toEqual({ level: 'Propose', minConfidence: 0.7 })
    expect(levels.objectNote).toEqual({ level: 'Auto', minConfidence: 0.7 })
    expect(levels.mailEvent).toEqual({ level: 'Auto', minConfidence: 0.7 })
    expect(Object.keys(normalizeLevels(undefined))).toHaveLength(7)
  })
})
