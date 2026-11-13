import type { S } from '@/api/resources'

/**
 * The line diff of PNMTD.Lib (LineDiff.cs, docs/mikrotik-plan.md §6.4), ported line for line, for exports only the
 * browser can read: encrypted backups, decrypted with the key loaded in this tab (docs/mikrotik-backup-plan.md §3.6).
 * Myers' O((N+M)D) algorithm after the common start and end are cut off, with unified hunks, so the browser shows
 * the same diff as the server.
 */
type Line = S['MikrotikDiffLine']
type Hunk = S['MikrotikDiffHunk']

export const MAX_EDITS = 4000

export interface LineDiffResult {
  added: number
  removed: number
  hunks: Hunk[]
}

/** The lines of a text; a final line end does not make an empty last line. */
export function lines(text: string): string[] {
  if (text.length === 0) return []
  let normalised = text.replaceAll('\r\n', '\n')
  if (normalised.endsWith('\n')) normalised = normalised.slice(0, -1)
  return normalised.split('\n')
}

export function compare(oldText: string, newText: string, context = 3): LineDiffResult {
  const edits = editsOf(lines(oldText), lines(newText))
  return {
    added: edits.filter((e) => e.kind === 'Added').length,
    removed: edits.filter((e) => e.kind === 'Removed').length,
    hunks: hunksOf(edits, context),
  }
}

/** As `diff -u` writes it, with the given file names; empty when the texts are the same. */
export function unified(diff: LineDiffResult, oldName: string, newName: string): string {
  if (diff.added === 0 && diff.removed === 0) return ''
  let text = `--- ${oldName}\n+++ ${newName}\n`
  for (const hunk of diff.hunks) {
    text += `@@ -${hunk.oldStart},${hunk.oldCount} +${hunk.newStart},${hunk.newCount} @@\n`
    for (const line of hunk.lines) {
      text += (line.kind === 'Added' ? '+' : line.kind === 'Removed' ? '-' : ' ') + line.text + '\n'
    }
  }
  return text
}

/** Every line of both texts in order: kept, removed or added. */
function editsOf(a: string[], b: string[]): Line[] {
  let prefix = 0
  while (prefix < a.length && prefix < b.length && a[prefix] === b[prefix]) prefix++
  let suffix = 0
  while (
    suffix < a.length - prefix &&
    suffix < b.length - prefix &&
    a[a.length - 1 - suffix] === b[b.length - 1 - suffix]
  ) {
    suffix++
  }
  const result: Line[] = []
  for (let i = 0; i < prefix; i++) result.push({ kind: 'Context', text: a[i]!, oldNumber: i + 1, newNumber: i + 1 })
  const middle = myers(a.slice(prefix, a.length - suffix), b.slice(prefix, b.length - suffix))
  for (const [kind, ai, bi] of middle) {
    if (kind === 'Context') {
      result.push({ kind, text: a[prefix + ai]!, oldNumber: prefix + ai + 1, newNumber: prefix + bi + 1 })
    } else if (kind === 'Removed') {
      result.push({ kind, text: a[prefix + ai]!, oldNumber: prefix + ai + 1, newNumber: null })
    } else {
      result.push({ kind, text: b[prefix + bi]!, oldNumber: null, newNumber: prefix + bi + 1 })
    }
  }
  for (let i = 0; i < suffix; i++) {
    const ai = a.length - suffix + i
    const bi = b.length - suffix + i
    result.push({ kind: 'Context', text: a[ai]!, oldNumber: ai + 1, newNumber: bi + 1 })
  }
  return result
}

type Step = [S['DiffLineKind'], number, number]

/** Myers' greedy algorithm with the trace of each step, then the path walked back. */
function myers(a: string[], b: string[]): Step[] {
  const n = a.length
  const m = b.length
  const max = n + m
  const path: Step[] = []
  if (max === 0) return path
  const limit = Math.min(max, MAX_EDITS)
  const offset = limit + 1
  const v = new Array<number>(2 * limit + 3).fill(0)
  const trace: number[][] = []
  let found = false
  for (let d = 0; d <= limit && !found; d++) {
    trace.push([...v])
    for (let k = -d; k <= d; k += 2) {
      const down = k === -d || (k !== d && v[offset + k - 1]! < v[offset + k + 1]!)
      let x = down ? v[offset + k + 1]! : v[offset + k - 1]! + 1
      let y = x - k
      while (x < n && y < m && a[x] === b[y]) {
        x++
        y++
      }
      v[offset + k] = x
      if (x >= n && y >= m) {
        found = true
        break
      }
    }
  }
  if (!found) {
    // Too different: everything removed, then everything added.
    for (let i = 0; i < n; i++) path.push(['Removed', i, 0])
    for (let j = 0; j < m; j++) path.push(['Added', n, j])
    return path
  }
  // Walk back from (n, m) through the saved states.
  let cx = n
  let cy = m
  for (let d = trace.length - 1; d >= 0; d--) {
    const vd = trace[d]!
    const k = cx - cy
    if (d === 0) {
      while (cx > 0 && cy > 0) path.push(['Context', --cx, --cy])
      break
    }
    const down = k === -d || (k !== d && vd[offset + k - 1]! < vd[offset + k + 1]!)
    const prevK = down ? k + 1 : k - 1
    const prevX = vd[offset + prevK]!
    const prevY = prevX - prevK
    while (cx > prevX && cy > prevY) path.push(['Context', --cx, --cy])
    if (down) {
      path.push(['Added', cx, --cy])
    } else {
      path.push(['Removed', --cx, cy])
    }
  }
  return path.reverse()
}

function hunksOf(edits: Line[], context: number): Hunk[] {
  const hunks: Hunk[] = []
  let i = 0
  while (i < edits.length) {
    if (edits[i]!.kind === 'Context') {
      i++
      continue
    }
    // A change: take up to `context` lines before it, then extend while the next change is near.
    const start = Math.max(0, i - context)
    let end = i
    for (;;) {
      while (end < edits.length && edits[end]!.kind !== 'Context') end++
      let next = end
      while (next < edits.length && edits[next]!.kind === 'Context' && next - end < 2 * context) next++
      if (next < edits.length && edits[next]!.kind !== 'Context') {
        end = next
        continue
      }
      end = Math.min(edits.length, end + context)
      break
    }
    const lines = edits.slice(start, end)
    const oldLines = lines.filter((l) => l.oldNumber !== null)
    const newLines = lines.filter((l) => l.newNumber !== null)
    const oldStart = oldLines[0]?.oldNumber ?? previous(edits, start, 'oldNumber') ?? 0
    const newStart = newLines[0]?.newNumber ?? previous(edits, start, 'newNumber') ?? 0
    hunks.push({ oldStart, oldCount: oldLines.length, newStart, newCount: newLines.length, lines })
    i = end
  }
  return hunks
}

/** For a hunk without lines on one side, unified diff names the line before it. */
function previous(edits: Line[], index: number, side: 'oldNumber' | 'newNumber'): number | null {
  for (let j = index - 1; j >= 0; j--) {
    const number = edits[j]![side]
    if (number !== null) return number
  }
  return null
}
