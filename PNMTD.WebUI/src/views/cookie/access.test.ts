import { describe, expect, it } from 'vitest'
import {
  accessGroupLabel,
  accessVerdictLabel,
  accessVerdictSeverity,
  answerForm,
  answerLabel,
  answerRequest,
  caseLink,
  personAnswerLabel,
  reviewStateLabel,
  reviewStateSeverity,
  serverAccountsFromText,
} from './access'

describe('The access watch in words (docs/cookie-access-plan.md §5.4, §7)', () => {
  it('colours the verdicts by how suspicious a login looks', () => {
    expect(accessVerdictLabel('alarming')).toBe('Alarming')
    expect(accessVerdictLabel(null)).toBe('–')
    expect(accessVerdictSeverity('normal')).toBe('success')
    expect(accessVerdictSeverity('unusual')).toBe('info')
    expect(accessVerdictSeverity('suspicious')).toBe('warn')
    expect(accessVerdictSeverity('alarming')).toBe('danger')
    expect(accessVerdictSeverity('something new')).toBe('secondary')
  })

  it("names a reviewed login's state, grey while it waits or wasn't judged", () => {
    expect(reviewStateLabel('NotJudged')).toBe('not judged')
    expect(reviewStateLabel('Suspicious')).toBe('suspicious')
    expect(reviewStateSeverity('Waiting')).toBe('secondary')
    expect(reviewStateSeverity('NotJudged')).toBe('secondary')
    expect(reviewStateSeverity('Alarming')).toBe('danger')
    expect(reviewStateSeverity('Normal')).toBe('success')
  })

  it('names the answers and the kind groups', () => {
    expect(answerLabel('expected')).toBe('Expected')
    expect(answerLabel('not-expected')).toBe('Not expected')
    expect(answerLabel(null)).toBe('–')
    expect(personAnswerLabel('no')).toBe("No, it wasn't me")
    expect(personAnswerLabel('yes')).toBe('Yes, it was me')
    expect(accessGroupLabel('file-transfer')).toBe('File transfer')
    expect(accessGroupLabel('future')).toBe('future')
  })

  it('links to the case in Cookie’s window', () => {
    expect(caseLink('c1')).toEqual({ name: 'cookie', params: { tab: 'cases' }, query: { case: 'c1' } })
  })
})

describe('The answer on an access case (docs/cookie-access-plan.md §7)', () => {
  it('sends Expected without Remember this unless asked, the comment trimmed', () => {
    expect(answerRequest({ ...answerForm(true), comment: ' The vendor ' })).toEqual({
      expected: true,
      comment: 'The vendor',
      remember: null,
    })
  })

  it('remembers this target, its network and its way for 90 days by default', () => {
    expect(answerRequest({ ...answerForm(true), remember: true })).toEqual({
      expected: true,
      comment: null,
      remember: { scope: 'target', anySource: false, anyWay: false, days: 90 },
    })
    const wide = { ...answerForm(true), remember: true, scope: 'all' as const, anySource: true, anyWay: true }
    expect(answerRequest({ ...wide, days: null }).remember).toEqual({
      scope: 'all',
      anySource: true,
      anyWay: true,
      days: null,
    })
  })

  it('never sends Remember this with Not expected: only what was expected is remembered', () => {
    expect(answerRequest({ ...answerForm(false), remember: true, comment: '  ' })).toEqual({
      expected: false,
      comment: null,
      remember: null,
    })
  })
})

describe("A person's server accounts (docs/cookie-access-plan.md §5.3)", () => {
  it('takes one per line, trimmed, each once whatever its case', () => {
    expect(serverAccountsFromText(' CONTOSO\\jdoe \n\njdoe\r\nJDOE\n  \njdoe@contoso.local')).toEqual([
      'CONTOSO\\jdoe',
      'jdoe',
      'jdoe@contoso.local',
    ])
    expect(serverAccountsFromText('')).toEqual([])
  })
})
