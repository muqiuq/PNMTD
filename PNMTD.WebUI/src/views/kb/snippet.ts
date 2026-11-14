/**
 * A search result's snippet (docs/locations-knowledge-plan.md §6.7): the server encloses each match in U+E000 and
 * U+E001, so no HTML comes from it. These are the parts to show, the matches as `<mark>` through the template's text
 * interpolation, never as HTML.
 */
export const MARK_START = '\uE000'
export const MARK_END = '\uE001'

export interface SnippetPart {
  text: string
  mark: boolean
}

/** The snippet in parts, alternating plain and marked; a stray marker is dropped, an unclosed match ends the text. */
export function snippetParts(snippet: string | null | undefined): SnippetPart[] {
  const parts: SnippetPart[] = []
  let mark = false
  let text = ''
  const flush = () => {
    if (text) {
      const last = parts[parts.length - 1]
      if (last && last.mark === mark) {
        last.text += text
      } else {
        parts.push({ text, mark })
      }
    }
    text = ''
  }
  for (const c of snippet ?? '') {
    if (c === MARK_START || c === MARK_END) {
      flush()
      mark = c === MARK_START
      continue
    }
    text += c
  }
  flush()
  return parts
}

/** Whether a snippet holds a match: without one (a title or tag matched) the list shows the summary. */
export function hasMatch(snippet: string | null | undefined): boolean {
  return !!snippet && snippet.includes(MARK_START)
}
