import { describe, expect, it } from 'vitest'
import { lineDiff, rewriteSummary } from './lineDiff'

describe('lineDiff', () => {
  it('finds nothing in equal texts', () => {
    expect(lineDiff('a\nb\n', 'a\nb\n')).toEqual({ hunks: [], added: 0, removed: 0 })
  })

  it('numbers the lines of both versions, removed lines first', () => {
    const diff = lineDiff('a\nb\nc\nd', 'a\nB\nc\nd\ne')
    expect(diff.added).toBe(2)
    expect(diff.removed).toBe(1)
    expect(diff.hunks).toHaveLength(1)
    expect(
      diff.hunks[0]!.lines.map((l) => `${l.kind[0]}${l.oldNumber ?? '-'}/${l.newNumber ?? '-'} ${l.text}`),
    ).toEqual(['C1/1 a', 'R2/- b', 'A-/2 B', 'C3/3 c', 'C4/4 d', 'A-/5 e'])
    expect(diff.hunks[0]).toMatchObject({ oldStart: 1, oldCount: 4, newStart: 1, newCount: 5 })
  })

  it('splits changes far apart into hunks with their context', () => {
    const before = Array.from({ length: 30 }, (_, i) => `line ${i + 1}`).join('\n')
    const after = before.replace('line 2\n', 'line two\n').replace('line 28\n', 'line twenty-eight\n')
    const diff = lineDiff(before, after)
    expect(diff.hunks).toHaveLength(2)
    expect(diff.hunks[0]).toMatchObject({ oldStart: 1, newStart: 1 })
    expect(diff.hunks[1]!.lines[0]!.text).toBe('line 25')
  })

  it('says how many lines a rewrite changes, with an example', () => {
    expect(rewriteSummary('* a\n* b\n\ntext', '- a\n- b\n\ntext\n')).toEqual({
      lines: 2,
      example: { from: '* a', to: '- a' },
    })
    expect(rewriteSummary('same\n', 'same')).toEqual({ lines: 0, example: null })
  })
})
