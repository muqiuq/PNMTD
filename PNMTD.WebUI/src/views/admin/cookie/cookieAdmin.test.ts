import { describe, expect, it } from 'vitest'
import { holds, meets, type Area, type Level } from '@/api/rights'
import {
  awayStateLabel,
  awayStateSeverity,
  awayVerdict,
  cookieAdminTabs,
  isLinkProblem,
  linkStateLabel,
  linkStateMeaning,
  linkStateSeverity,
  missingPlaceholders,
  newId,
  normalizeLevels,
  pickedFor,
  setOverride,
  shownTab,
  withUnknown,
  type RedmineLinkState,
} from './cookieAdmin'

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
    expect(levels.accessNotice).toEqual({ level: 'Auto', minConfidence: 0.6 })
    expect(levels.awayNotice).toEqual({ level: 'Auto', minConfidence: 0.7 })
    expect(Object.keys(normalizeLevels(undefined))).toHaveLength(9)
  })
})

describe('the tabs of Admin → Cookie by right (docs/cookie-redmine-tab-plan.md §3.2)', () => {
  const allowedTo = (rights: Partial<Record<Area, Level>>) =>
    cookieAdminTabs.filter((t) => meets(t.right, false, (area, level) => holds(false, rights, area, level)))

  it('shows Redmine: Manage only the Redmine tab, and opens it whatever was asked', () => {
    const redmine = allowedTo({ Redmine: 'Manage' })
    expect(redmine.map((t) => t.key)).toEqual(['redmine'])
    expect(shownTab(null, redmine)).toBe('redmine')
    expect(shownTab('actions', redmine)).toBe('redmine')
  })

  it('shows Cookie: Manage every tab but Redmine, and both rights every tab', () => {
    const cookie = allowedTo({ Cookie: 'Manage' })
    expect(cookie.map((t) => t.key)).not.toContain('redmine')
    expect(cookie).toHaveLength(cookieAdminTabs.length - 1)
    expect(shownTab(null, cookie)).toBe('overview')
    expect(shownTab('redmine', cookie)).toBe('overview')
    expect(shownTab('lessons', cookie)).toBe('lessons')
    expect(allowedTo({ Cookie: 'Manage', Redmine: 'Manage' })).toHaveLength(cookieAdminTabs.length)
    expect(allowedTo({ Redmine: 'Link', Cookie: 'Instruct' })).toEqual([])
  })
})

describe('the Redmine links panel (docs/cookie-redmine-tab-plan.md §4.3)', () => {
  const row = (state: RedmineLinkState, shared = false) => ({ state, shared })

  it('keeps every row but a known link for "Problems only", and two people with one Redmine user', () => {
    expect(isLinkProblem(row('Linked'))).toBe(false)
    expect(isLinkProblem(row('Linked', true))).toBe(true)
    for (const state of ['LinkedUnknown', 'Matches', 'Several', 'NoMatch', 'NotSet'] as const) {
      expect(isLinkProblem(row(state))).toBe(true)
    }
  })

  it('starts the picker at the linked user, or the one match, and lets a person pick among several', () => {
    expect(pickedFor({ redmineUserId: 7, candidates: [] })).toBe(7)
    expect(pickedFor({ redmineUserId: null, candidates: [{ id: 3, name: 'Ann' }] })).toBe(3)
    expect(
      pickedFor({
        redmineUserId: null,
        candidates: [
          { id: 3, name: 'Sam' },
          { id: 4, name: 'Sam' },
        ],
      }),
    ).toBeNull()
  })

  it('names every state and what it means for the reminders', () => {
    for (const state of Object.keys(linkStateLabel) as RedmineLinkState[]) {
      expect(linkStateMeaning[state].length).toBeGreaterThan(20)
    }
    expect(linkStateSeverity('Linked')).toBe('success')
    expect(linkStateSeverity('NoMatch')).toBe('danger')
  })
})

describe('the comments while away (docs/cookie-redmine-tab-plan.md §9)', () => {
  it("names Cookie's verdict, and code's when nobody judged", () => {
    expect(awayVerdict({ importance: 'high', confidence: 0.91, judgedBy: 'cookie' })).toBe('high, 91 % sure')
    expect(awayVerdict({ importance: null, confidence: null, judgedBy: 'code' })).toBe('not judged')
    expect(awayVerdict({ importance: null, confidence: null, judgedBy: null })).toBe('–')
  })

  it('names every state, told ones as done and the warnings as such', () => {
    expect(Object.keys(awayStateLabel)).toHaveLength(11)
    expect(awayStateSeverity('Notified')).toBe('success')
    expect(awayStateSeverity('Joined')).toBe('success')
    expect(awayStateSeverity('Withheld')).toBe('warn')
    expect(awayStateSeverity('NotImportant')).toBe('secondary')
  })
})
