import { describe, expect, it } from 'vitest'
import { describeRhythm, loungeProblems, meanSceneLength, type LoungeValues } from './loungeAdmin'

/** Admin → Cookie → Lounge's checks (docs/lounge-plan.md §12) and its "what it means" line (§15.3), as the server's. */
const week = (from = '08:00', to = '17:00') =>
  (['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'] as const)
    .map((day) => ({ day, periods: [{ from, to }] }))
    .concat([
      { day: 'Saturday' as never, periods: [] },
      { day: 'Sunday' as never, periods: [] },
    ])

const values = (over: Partial<LoungeValues> = {}): LoungeValues => ({
  awake: week(),
  asleepOnHolidays: true,
  messagesPerHour: 6,
  longestScene: 6,
  disagreeShare: 0.3,
  repliesPerHour: 20,
  importantPerDay: 3,
  groupId: null,
  effort: 'low',
  toolCallsPerMessage: 3,
  casesPerDay: 2,
  emailLevel: 'Propose',
  emailsPerDay: 2,
  emailsPerPersonWeek: 1,
  halfLifeDays: 7,
  memoriesInPrompt: 15,
  maxDriftPerDay: 0.05,
  convictions: [
    {
      key: 'reboot',
      question: 'Is a reboot a fix?',
      minus: 'No.',
      plus: 'Yes.',
      owner: 'cookie',
      cookie: 0.85,
      seraph: -0.7,
    },
    {
      key: 'friday',
      question: 'Changes on a Friday?',
      minus: 'Yes.',
      plus: 'Never.',
      owner: 'seraph',
      cookie: -0.5,
      seraph: 0.9,
    },
  ],
  budgetUsd: 10,
  ...over,
})

describe('loungeProblems', () => {
  it('passes the defaults', () => {
    expect(loungeProblems(values())).toEqual({})
  })

  it('names the fields as the server does', () => {
    const p = loungeProblems(
      values({
        messagesPerHour: 241,
        importantPerDay: 11,
        longestScene: 1,
        maxDriftPerDay: 0.3,
        awake: week().map((d) => ({ ...d, periods: [] })),
      }),
    )
    expect(p.messagesPerHour).toBe('0 to 240.')
    expect(p.importantPerDay).toBe('0 to 10.')
    expect(p.longestScene).toBe('2 to 10.')
    expect(p.maxDriftPerDay).toBe('0 to 0.2.')
    expect(p.awake).toContain('At least one awake period')
  })

  it('wants two convictions, one per owner, the owner at least 0.5 on its side', () => {
    const [a, b] = values().convictions
    expect(loungeProblems(values({ convictions: [a!, { ...b!, owner: 'cookie' }] })).convictions).toContain(
      'one for Cookie and one for Seraph',
    )
    expect(loungeProblems(values({ convictions: [{ ...a!, cookie: 0.4 }, b!] }))['convictions.0']).toContain(
      'at least 0.5',
    )
    expect(loungeProblems(values({ convictions: [{ ...a!, key: 'Not A Key' }, b!] }))['convictions.0']).toContain(
      'lower-case',
    )
  })
})

describe('describeRhythm', () => {
  it('says how often a scene comes at a low pace', () => {
    // Aimed a little below the ceiling, as the server's LoungePacing.Describe says it.
    expect(describeRhythm(values())).toMatch(/^About 50 messages a day, a scene about every \d+ minutes\.$/)
  })

  it('calls a high pace one continuous conversation', () => {
    expect(describeRhythm(values({ messagesPerHour: 60 }))).toBe(
      'A message about every minute: one continuous conversation, about 510 messages a day.',
    )
    expect(describeRhythm(values({ messagesPerHour: 120 }))).toBe(
      'A message about every 30 seconds: one continuous conversation, about 1,030 messages a day.',
    )
    expect(describeRhythm(values({ messagesPerHour: 240 }))).toBe(
      'A message about every 15 seconds: one continuous conversation, about 2,050 messages a day.',
    )
  })

  it('says they only answer people at 0', () => {
    expect(describeRhythm(values({ messagesPerHour: 0 }))).toBe(
      'They only answer people: no conversation of their own.',
    )
  })

  it('weights short scenes most', () => {
    expect(meanSceneLength(2, 0)).toBe(2)
    expect(meanSceneLength(6, 0)).toBeGreaterThan(2.5)
    expect(meanSceneLength(6, 0)).toBeLessThan(3.5)
    expect(meanSceneLength(6, 1) - meanSceneLength(6, 0)).toBeCloseTo(2)
  })
})
