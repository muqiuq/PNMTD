import { describe, expect, it } from 'vitest'
import { compare, lines, unified } from './lineDiff'

// The cases of PNMTD.Tests/Mikrotik/ExportAndDiffTests.cs: the port gives the server's diff.
const header = (h: { oldStart: number; oldCount: number; newStart: number; newCount: number }) =>
  `@@ -${h.oldStart},${h.oldCount} +${h.newStart},${h.newCount} @@`

describe('line diff', () => {
  it('writes a unified diff as diff -u does', () => {
    const before = Array.from({ length: 12 }, (_, i) => `line ${i + 1}`).join('\n') + '\n'
    const after = before.replace('line 3\n', 'line 3 changed\n').replace('line 11\n', '') + 'line 13\n'
    const diff = compare(before, after)
    expect([diff.added, diff.removed]).toEqual([2, 2])
    expect(diff.hunks.map(header)).toEqual(['@@ -1,6 +1,6 @@', '@@ -8,5 +8,5 @@'])
    const text = unified(diff, 'a.rsc', 'b.rsc')
    expect(
      text.startsWith('--- a.rsc\n+++ b.rsc\n@@ -1,6 +1,6 @@\n line 1\n line 2\n-line 3\n+line 3 changed\n line 4\n'),
    ).toBe(true)
    expect(text).toContain('-line 11\n line 12\n+line 13\n')
    expect(unified(compare(before, before), 'a', 'b')).toBe('')
  })

  it('puts changes close together into one hunk', () => {
    const before = Array.from({ length: 20 }, (_, i) => `${i + 1}`).join('\n') + '\n'
    const after = before.replace('\n5\n', '\nfive\n').replace('\n10\n', '\nten\n')
    expect(compare(before, after).hunks.map(header)).toEqual(['@@ -2,12 +2,12 @@'])
  })

  it('handles empty texts', () => {
    expect(compare('', 'a\nb\n').hunks.map(header)).toEqual(['@@ -0,0 +1,2 @@'])
    expect(compare('a\nb\n', '').hunks.map(header)).toEqual(['@@ -1,2 +0,0 @@'])
    expect(compare('', '').hunks).toEqual([])
  })

  it('diffs random texts minimally enough to rebuild both sides', () => {
    let seed = 4711
    const next = (n: number) => (seed = (seed * 1103515245 + 12345) % 2 ** 31) % n
    for (let round = 0; round < 200; round++) {
      const a = Array.from({ length: next(30) }, () => 'abcd'[next(4)]!)
      const b = Array.from({ length: next(30) }, () => 'abcd'[next(4)]!)
      const all = compare(a.join('\n'), b.join('\n'), 1000).hunks.flatMap((h) => h.lines)
      const rebuiltA = all.filter((l) => l.kind !== 'Added').map((l) => l.text)
      const rebuiltB = all.filter((l) => l.kind !== 'Removed').map((l) => l.text)
      // Lines outside every hunk are unchanged: with a huge context the one hunk covers everything.
      if (all.length) {
        expect(rebuiltA).toEqual(lines(a.join('\n')))
        expect(rebuiltB).toEqual(lines(b.join('\n')))
      }
    }
  })
})
