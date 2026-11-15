import { describe, expect, it } from 'vitest'
import {
  decisionKindLabel,
  decisionOutcomeLabel,
  decisionOutcomeSeverity,
  decisionOutcomes,
  formatConfidence,
  instructionProblems,
  instructionRequest,
  instructionState,
  instructionTarget,
  lessonProblems,
  sortInstructions,
  untilAfter,
  verdictSeverity,
  type InstructionForm,
  type InstructionItem,
} from './acting'

const now = Date.parse('2026-11-15T10:00:00Z')
const day = 86_400_000
const hostId = '7b0e8c1e-3f7a-4c39-9d3e-0a9f1b2c3d4e'

const form = (patch: Partial<InstructionForm> = {}): InstructionForm => ({
  kind: 'suppress',
  scope: 'channel',
  value: 'pushover',
  label: '',
  text: '',
  until: new Date(now + day),
  ...patch,
})

const item = (patch: Partial<InstructionItem> = {}): InstructionItem => ({
  id: 'a',
  kind: 'mute',
  scope: 'host',
  value: hostId,
  label: 'srv-01',
  text: '',
  until: new Date(now + day).toISOString(),
  confirmed: true,
  createdAt: new Date(now - day).toISOString(),
  createdByName: 'Anna',
  source: 'admin',
  endedAt: null,
  endedByName: null,
  active: true,
  ...patch,
})

describe("Cookie's decision log (docs/cookie-plan.md §11, §21.1)", () => {
  it('colours each outcome: done green, waiting blue, held amber, refused red, dropped grey', () => {
    expect(decisionOutcomeSeverity('done')).toBe('success')
    expect(decisionOutcomeSeverity('approved')).toBe('success')
    expect(decisionOutcomeSeverity('proposed')).toBe('info')
    expect(decisionOutcomeSeverity('held-back')).toBe('warn')
    expect(decisionOutcomeSeverity('declined')).toBe('warn')
    expect(decisionOutcomeSeverity('rejected')).toBe('danger')
    expect(decisionOutcomeSeverity('refused')).toBe('danger')
    expect(decisionOutcomeSeverity('failed')).toBe('danger')
    expect(decisionOutcomeSeverity('off')).toBe('secondary')
    expect(decisionOutcomeSeverity('duplicate')).toBe('secondary')
    expect(decisionOutcomeSeverity('muted')).toBe('secondary')
    expect(decisionOutcomeSeverity('something new')).toBe('secondary')
  })

  it('names outcomes and kinds in words, and keeps unknown ones as they are', () => {
    expect(decisionOutcomeLabel('held-back')).toBe('held back')
    expect(decisionOutcomeLabel('below-threshold')).toBe('below threshold')
    expect(decisionOutcomes).toContain('noted')
    expect(decisionKindLabel('object-note')).toBe('Note on a host or sensor')
    expect(decisionKindLabel('hand-to-person')).toBe('For a person')
    expect(decisionKindLabel('future-kind')).toBe('future-kind')
  })

  it("shows Seraph's verdicts and the confidence", () => {
    expect(verdictSeverity('approve')).toBe('success')
    expect(verdictSeverity('revise')).toBe('warn')
    expect(verdictSeverity('reject')).toBe('danger')
    expect(verdictSeverity('ask')).toBe('info')
    expect(formatConfidence(0.8249)).toBe('0.82')
    expect(formatConfidence(null)).toBe('–')
  })
})

describe('Mute, suppress and instructions (docs/cookie-plan.md §10.3)', () => {
  it('takes a suppress of a channel for a day', () => {
    expect(instructionProblems(form(), now)).toEqual({})
    expect(instructionRequest(form({ text: ' Maintenance ' }))).toEqual({
      kind: 'suppress',
      scope: 'channel',
      value: 'pushover',
      label: null,
      text: 'Maintenance',
      until: new Date(now + day).toISOString(),
    })
  })

  it('wants a known channel and kind of case', () => {
    expect(instructionProblems(form({ value: 'sms' }), now).value).toBeTruthy()
    expect(instructionProblems(form({ value: null }), now).value).toBeTruthy()
    expect(instructionProblems(form({ scope: 'case-kind', value: 'Problem' }), now)).toEqual({})
    expect(instructionProblems(form({ scope: 'case-kind', value: 'request' }), now).value).toBeTruthy()
    expect(instructionProblems(form({ scope: 'sensor-type', value: 'PING' }), now)).toEqual({})
    expect(instructionProblems(form({ scope: 'location' }), now).scope).toBeTruthy()
  })

  it('mutes by an id, never by a name', () => {
    expect(instructionProblems(form({ kind: 'mute', scope: 'host', value: hostId }), now)).toEqual({})
    expect(instructionProblems(form({ kind: 'mute', scope: 'host', value: 'srv-01' }), now).value).toBe(
      'Choose the host.',
    )
    expect(instructionProblems(form({ kind: 'mute', scope: 'channel', value: hostId }), now).scope).toBeTruthy()
  })

  it('wants a note to say something, and sends it without a scope', () => {
    expect(instructionProblems(form({ kind: 'note', scope: '', value: null, text: ' ' }), now).text).toBeTruthy()
    const note = form({ kind: 'note', scope: 'channel', value: 'pushover', label: 'x', text: 'Leave the printers' })
    expect(instructionProblems(note, now)).toEqual({})
    expect(instructionRequest(note)).toMatchObject({ kind: 'note', scope: '', value: null, label: null })
    expect(instructionProblems(form({ text: 'x'.repeat(1001) }), now).text).toBeTruthy()
  })

  it('ends in the future, at most 30 days away', () => {
    expect(instructionProblems(form({ until: null }), now).until).toBe('Choose when it ends.')
    expect(instructionProblems(form({ until: new Date(now - 1) }), now).until).toBe('In the future.')
    expect(instructionProblems(form({ until: new Date(now + 30 * day) }), now)).toEqual({})
    expect(instructionProblems(form({ until: new Date(now + 31 * day) }), now).until).toBe('At most 30 days away.')
    expect(untilAfter('P1D', now)?.getTime()).toBe(now + day)
    expect(untilAfter('P30D', now)?.getTime()).toBe(now + 30 * day)
    expect(untilAfter('custom', now)).toBeNull()
  })

  it('tells active ones from drafts, ended and expired ones, and lists them in that order', () => {
    const active = item({ id: 'active' })
    const draft = item({ id: 'draft', confirmed: false, active: false, createdAt: new Date(now).toISOString() })
    const ended = item({ id: 'ended', endedAt: new Date(now - 1000).toISOString(), active: false })
    const expired = item({ id: 'expired', until: new Date(now - 1000).toISOString(), active: false })
    expect(instructionState(active, now)).toBe('active')
    expect(instructionState(draft, now)).toBe('draft')
    expect(instructionState(ended, now)).toBe('ended')
    expect(instructionState(expired, now)).toBe('expired')
    expect(sortInstructions([expired, draft, ended, active], now).map((i) => i.id)).toEqual([
      'active',
      'draft',
      'expired',
      'ended',
    ])
  })

  it('names what one is about', () => {
    expect(instructionTarget(item())).toBe('Host srv-01')
    expect(instructionTarget(item({ label: null }))).toBe(`Host ${hostId}`)
    expect(instructionTarget(item({ kind: 'suppress', scope: 'channel', value: 'redmine-note' }))).toBe(
      'Channel Notes on tickets',
    )
    expect(instructionTarget(item({ kind: 'suppress', scope: 'case-kind', value: 'problem' }))).toBe(
      'Kind of case Problems',
    )
    expect(instructionTarget(item({ kind: 'note', scope: '' }))).toBe('')
  })
})

describe('Lessons (docs/cookie-plan.md §11)', () => {
  const lesson = (id: string, active = true) => ({ id, text: `Lesson ${id}`, active })

  it('allows at most 30 active, however many are off', () => {
    const lessons = Array.from({ length: 30 }, (_, i) => lesson(String(i)))
    expect(lessonProblems(lessons)).toEqual({})
    expect(lessonProblems([...lessons, lesson('off', false)])).toEqual({})
    expect(lessonProblems([...lessons, lesson('one more')]).lessons).toBeTruthy()
  })

  it('wants a text, and a value for a suppress rule', () => {
    expect(lessonProblems([{ id: 'a', text: ' ', active: true }]).a).toBe('Write the lesson.')
    expect(lessonProblems([{ id: 'a', text: 'x'.repeat(301), active: true }]).a).toBeTruthy()
    expect(
      lessonProblems([{ id: 'a', text: 'No Pushover', active: true, suppressScope: 'channel', suppressValue: null }]).a,
    ).toBe('Choose what it suppresses.')
    expect(
      lessonProblems([
        { id: 'a', text: 'No Pushover', active: true, suppressScope: 'channel', suppressValue: 'pushover' },
      ]),
    ).toEqual({})
  })
})
