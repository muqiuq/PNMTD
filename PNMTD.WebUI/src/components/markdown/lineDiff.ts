import type { DiffHunk, DiffLine } from '@/components/DiffView.vue'

/**
 * A line diff in the browser (docs/locations-knowledge-plan.md §5.4, §5.7): how many lines the visual
 * editor would rewrite, and the conflict's diffs where Monaco doesn't run. The same shape as the
 * server's LineDiff, so DiffView shows either.
 *
 * The common start and end are cut off, the rest is an LCS by dynamic programming; a middle of more
 * than maxCells (lines × lines) counts as replaced as a whole.
 */
export interface LineDiffResult {
  hunks: DiffHunk[]
  added: number
  removed: number
}

const maxCells = 4_000_000

export function splitLines(text: string): string[] {
  const lines = text.replace(/\r\n?/g, '\n').split('\n')
  if (lines.length > 1 && lines[lines.length - 1] === '') {
    lines.pop()
  }
  return lines
}

/** The edit script: every line of both, as context, removed or added, in order. */
function script(a: string[], b: string[]): DiffLine[] {
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) {
    start++
  }
  let endA = a.length
  let endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--
    endB--
  }

  const out: DiffLine[] = []
  for (let i = 0; i < start; i++) {
    out.push({ kind: 'Context', text: a[i]!, oldNumber: i + 1, newNumber: i + 1 })
  }

  const n = endA - start
  const m = endB - start
  if (n * m > maxCells) {
    for (let i = start; i < endA; i++) out.push({ kind: 'Removed', text: a[i]!, oldNumber: i + 1, newNumber: null })
    for (let j = start; j < endB; j++) out.push({ kind: 'Added', text: b[j]!, oldNumber: null, newNumber: j + 1 })
  } else {
    // lcs[i][j]: the longest common subsequence of a[start+i..endA) and b[start+j..endB).
    const width = m + 1
    const lcs = new Uint32Array((n + 1) * width)
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        lcs[i * width + j] =
          a[start + i] === b[start + j]
            ? lcs[(i + 1) * width + j + 1]! + 1
            : Math.max(lcs[(i + 1) * width + j]!, lcs[i * width + j + 1]!)
      }
    }
    let i = 0
    let j = 0
    while (i < n || j < m) {
      if (i < n && j < m && a[start + i] === b[start + j]) {
        out.push({ kind: 'Context', text: a[start + i]!, oldNumber: start + i + 1, newNumber: start + j + 1 })
        i++
        j++
      } else if (j < m && (i === n || lcs[i * width + j + 1]! >= lcs[(i + 1) * width + j]!)) {
        out.push({ kind: 'Added', text: b[start + j]!, oldNumber: null, newNumber: start + j + 1 })
        j++
      } else {
        out.push({ kind: 'Removed', text: a[start + i]!, oldNumber: start + i + 1, newNumber: null })
        i++
      }
    }
  }

  for (let k = 0; k < a.length - endA; k++) {
    out.push({ kind: 'Context', text: a[endA + k]!, oldNumber: endA + k + 1, newNumber: endB + k + 1 })
  }
  // Within a change, removed lines first, as the server's diff and git show them.
  for (let k = 1; k < out.length; k++) {
    let x = k
    while (x > 0 && out[x]!.kind === 'Removed' && out[x - 1]!.kind === 'Added') {
      ;[out[x - 1], out[x]] = [out[x]!, out[x - 1]!]
      x--
    }
  }
  return out
}

/** The diff of two texts, with `context` lines around each change. */
export function lineDiff(before: string, after: string, context = 3): LineDiffResult {
  const lines = script(splitLines(before), splitLines(after))
  const added = lines.filter((l) => l.kind === 'Added').length
  const removed = lines.filter((l) => l.kind === 'Removed').length

  const hunks: DiffHunk[] = []
  let current: DiffLine[] = []
  let trailing = 0
  lines.forEach((line, index) => {
    if (line.kind !== 'Context') {
      if (!current.length) {
        current = lines.slice(Math.max(0, index - context), index)
      } else if (trailing > 2 * context) {
        // A gap wider than two contexts: the hunk ends after its context, a new one starts.
        hunks.push(finish(current.slice(0, current.length - trailing + context)))
        current = lines.slice(index - context, index)
      }
      current.push(line)
      trailing = 0
    } else if (current.length) {
      current.push(line)
      trailing++
    }
  })
  if (current.length) {
    hunks.push(finish(current.slice(0, current.length - Math.max(0, trailing - context))))
  }
  return { hunks, added, removed }
}

function finish(lines: DiffLine[]): DiffHunk {
  const first = (key: 'oldNumber' | 'newNumber') => lines.find((l) => l[key] !== null)?.[key] ?? 0
  const oldCount = lines.filter((l) => l.kind !== 'Added').length
  const newCount = lines.filter((l) => l.kind !== 'Removed').length
  return { oldStart: first('oldNumber'), oldCount, newStart: first('newNumber'), newCount, lines }
}

/** How many lines a rewrite changes, and its first change as an example (§5.4). */
export function rewriteSummary(
  before: string,
  after: string,
): { lines: number; example: { from: string; to: string } | null } {
  const diff = lineDiff(before, after, 0)
  const all = diff.hunks.flatMap((h) => h.lines)
  const from = all.find((l) => l.kind === 'Removed')
  const to = all.find((l) => l.kind === 'Added')
  return {
    lines: Math.max(diff.added, diff.removed),
    example: from || to ? { from: from?.text ?? '', to: to?.text ?? '' } : null,
  }
}
