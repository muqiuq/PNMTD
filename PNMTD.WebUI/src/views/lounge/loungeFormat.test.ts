import { describe, expect, it } from 'vitest'
import {
  axisPercent,
  commentable,
  emailLine,
  flow,
  merge,
  segments,
  stateLine,
  strength,
  type LoungeMessage,
} from './loungeFormat'

/** The lounge's shapes (docs/lounge-plan.md §15.2): chips in the text, comments under their message, a date at each day. */
const line = (id: number, over: Partial<LoungeMessage> = {}): LoungeMessage => ({
  id,
  at: '2026-11-20T09:41:00Z',
  from: 'cookie',
  kind: 'message',
  userId: null,
  author: null,
  authorColor: null,
  authorCode: null,
  text: `line ${id}`,
  onId: null,
  takenById: null,
  replyToId: null,
  refused: false,
  removed: false,
  caseId: null,
  caseTitle: null,
  email: null,
  important: false,
  ...over,
})

describe('segments', () => {
  it('makes chips of cases, hosts, sensors and tickets, and leaves the rest as written', () => {
    const host = '3f2c0000-1111-4222-8333-444455556666'
    expect(segments(`nas-02 has 61% free [host:${host}], see #4821.`)).toEqual([
      { kind: 'text', text: 'nas-02 has 61% free ' },
      { kind: 'host', id: host, text: 'host' },
      { kind: 'text', text: ', see ' },
      { kind: 'ticket', id: 4821, text: '#4821' },
      { kind: 'text', text: '.' },
    ])
    expect(segments('Beep! Nothing to link.')).toEqual([{ kind: 'text', text: 'Beep! Nothing to link.' }])
  })
})

describe('flow', () => {
  it('puts a comment under the message it is on, and one on none in the flow', () => {
    const entries = flow([
      line(1),
      line(2, { from: 'seraph' }),
      line(3, { from: 'person', kind: 'comment', onId: 2 }),
      line(4, { from: 'person', kind: 'comment' }),
    ])
    expect(entries.map((e) => e.message.id)).toEqual([1, 2, 4])
    expect(entries[1]!.comments.map((c) => c.id)).toEqual([3])
    expect(entries[0]!.day).not.toBeNull()
    expect(entries[1]!.day).toBeNull()
  })

  it('starts a new day with its date', () => {
    const entries = flow([line(1, { at: '2026-11-19T12:00:00Z' }), line(2, { at: '2026-11-20T12:00:00Z' })])
    expect(entries.every((e) => e.day)).toBe(true)
  })

  it("merges a poll's lines by id, so a comment taken up changes in place", () => {
    const merged = merge(
      [line(1), line(2, { kind: 'comment', from: 'person' })],
      [line(2, { kind: 'comment', from: 'person', takenById: 3 }), line(3)],
    )
    expect(merged.map((m) => [m.id, m.takenById])).toEqual([
      [1, null],
      [2, 3],
      [3, null],
    ])
  })
})

describe('words', () => {
  it('says what came of an e-mail', () => {
    expect(emailLine({ to: 'Anna', outcome: 'proposed' })).toBe('E-mailed Anna (waiting for a person)')
    expect(emailLine({ to: 'Anna', outcome: 'done' })).toBe('E-mailed Anna (sent)')
  })

  it('places positions on the axis and names their strength', () => {
    expect(axisPercent(-1)).toBe(0)
    expect(axisPercent(0)).toBe(50)
    expect(axisPercent(0.85)).toBe(92.5)
    expect(strength(0.85)).toBe('certain')
    expect(strength(-0.6)).toBe('fairly sure')
    expect(strength(0.1)).toBe('torn')
  })

  it('lets only messages be commented on', () => {
    expect(commentable(line(1))).toBe(true)
    expect(commentable(line(2, { kind: 'comment' }))).toBe(false)
    expect(commentable(line(3, { removed: true }))).toBe(false)
    expect(commentable(line(4, { from: 'system' }))).toBe(false)
  })

  it('names the state, and who is typing', () => {
    const base = {
      running: true,
      reason: null,
      speaking: null,
      nextAt: null,
      canWrite: true,
      waiting: false,
      awakeUntil: null,
    }
    expect(stateLine({ ...base, state: 'Stopped' }, null)).toBe('closed')
    expect(stateLine({ ...base, state: 'Speaking', speaking: 'seraph' }, 'Seraph')).toBe('Seraph is typing…')
  })
})
