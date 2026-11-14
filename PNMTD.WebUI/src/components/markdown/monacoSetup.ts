import * as monaco from 'monaco-editor/editor/editor.api'
// VS Code's editor, with what writing Markdown uses (docs/locations-knowledge-plan.md §5.3): no
// suggestions, formatters or language servers.
import 'monaco-editor/features/codicon/register'
import 'monaco-editor/features/codeEditor/register'
import 'monaco-editor/features/diffEditor/register'
import 'monaco-editor/features/bracketMatching/register'
import 'monaco-editor/features/caretOperations/register'
import 'monaco-editor/features/clipboard/register'
import 'monaco-editor/features/comment/register'
import 'monaco-editor/features/contextmenu/register'
import 'monaco-editor/features/cursorUndo/register'
import 'monaco-editor/features/dnd/register'
import 'monaco-editor/features/find/register'
import 'monaco-editor/features/folding/register'
import 'monaco-editor/features/gotoLine/register'
import 'monaco-editor/features/indentation/register'
import 'monaco-editor/features/lineSelection/register'
import 'monaco-editor/features/linesOperations/register'
import 'monaco-editor/features/links/register'
import 'monaco-editor/features/longLinesHelper/register'
import 'monaco-editor/features/multicursor/register'
import 'monaco-editor/features/placeholderText/register'
import 'monaco-editor/features/quickCommand/register'
import 'monaco-editor/features/quickHelp/register'
import 'monaco-editor/features/readOnlyMessage/register'
import 'monaco-editor/features/smartSelect/register'
import 'monaco-editor/features/stickyScroll/register'
import 'monaco-editor/features/toggleTabFocusMode/register'
import 'monaco-editor/features/unicodeHighlighter/register'
import 'monaco-editor/features/unusualLineTerminators/register'
import 'monaco-editor/features/wordHighlighter/register'
import 'monaco-editor/features/wordOperations/register'
import 'monaco-editor/features/wordPartOperations/register'
// Markdown, and the languages its fences are highlighted in: each loads when first seen.
import 'monaco-editor/languages/definitions/markdown/register'
import 'monaco-editor/languages/definitions/shell/register'
import 'monaco-editor/languages/definitions/powershell/register'
import 'monaco-editor/languages/definitions/yaml/register'
import 'monaco-editor/languages/definitions/xml/register'
import 'monaco-editor/languages/definitions/ini/register'
import 'monaco-editor/languages/definitions/sql/register'
import 'monaco-editor/languages/definitions/python/register'
import 'monaco-editor/languages/definitions/javascript/register'
import 'monaco-editor/languages/definitions/csharp/register'
import 'monaco-editor/languages/definitions/dockerfile/register'
// The worker from PNMT's own origin, bundled by Vite (§5.3): the CSP stays as it is.
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'

/**
 * Monaco, as PNMT sets it up (docs/locations-knowledge-plan.md §5.3). This module is the code
 * editor's chunk: CodeEditor and MonacoDiff import it lazily.
 */

self.MonacoEnvironment = {
  // Markdown needs only the editor's own worker (diffs, links, word highlights).
  getWorker: () => new EditorWorker(),
}

// The names fences use for the languages above (Monaco looks up the fence's word by alias).
const fenceAliases: Record<string, string[]> = {
  shell: ['bash', 'zsh', 'console'],
  powershell: ['pwsh'],
  xml: ['html', 'svg'],
  ini: ['toml', 'cfg', 'conf', 'properties'],
  sql: ['sql'],
  csharp: ['cs', 'c#'],
  dockerfile: ['docker'],
}
for (const [id, aliases] of Object.entries(fenceAliases)) {
  monaco.languages.register({ id, aliases })
}
// JSON in a fence, with JavaScript's tokens: Monaco's JSON support is a language service with a worker.
monaco.languages.register({ id: 'json', aliases: ['JSON', 'json', 'jsonc'] })
monaco.languages.registerTokensProviderFactory('json', {
  create: async () => (await import('monaco-editor/languages/definitions/javascript/javascript')).language,
})

/** Folding by headings and fenced blocks, as VS Code's Markdown does. */
monaco.languages.registerFoldingRangeProvider('markdown', {
  provideFoldingRanges(model) {
    const ranges: monaco.languages.FoldingRange[] = []
    const open: { level: number; line: number }[] = []
    let fence: { marker: string; line: number } | null = null
    const lines = model.getLineCount()
    const close = (level: number, before: number) => {
      while (open.length && open[open.length - 1]!.level >= level) {
        const heading = open.pop()!
        if (before > heading.line) {
          ranges.push({ start: heading.line, end: before })
        }
      }
    }
    for (let n = 1; n <= lines; n++) {
      const text = model.getLineContent(n)
      const marker = text.match(/^ {0,3}(`{3,}|~{3,})/)?.[1]
      if (fence) {
        if (marker && marker[0] === fence.marker[0] && marker.length >= fence.marker.length) {
          ranges.push({ start: fence.line, end: n })
          fence = null
        }
        continue
      }
      if (marker) {
        fence = { marker, line: n }
        continue
      }
      const heading = text.match(/^ {0,3}(#{1,6})\s/)
      if (heading) {
        let end = n - 1
        while (end > 0 && model.getLineContent(end).trim() === '') end--
        close(heading[1]!.length, end)
        open.push({ level: heading[1]!.length, line: n })
      }
    }
    let end = lines
    while (end > 0 && model.getLineContent(end).trim() === '') end--
    close(1, end)
    return ranges
  },
})

// --- the theme from PNMT's tokens ---------------------------------------------------------------

function hex(value: string, fallback: string): string {
  const rgb = value.match(/^rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)(?:[,\s/]+([\d.]+%?))?\s*\)$/i)
  if (rgb) {
    const alpha = rgb[4] ? (rgb[4].endsWith('%') ? Number.parseFloat(rgb[4]) / 100 : Number.parseFloat(rgb[4])) : 1
    const parts = [rgb[1], rgb[2], rgb[3]].map((p) => Number(p).toString(16).padStart(2, '0'))
    return `#${parts.join('')}${
      alpha < 1
        ? Math.round(alpha * 255)
            .toString(16)
            .padStart(2, '0')
        : ''
    }`
  }
  return /^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(value) ? value : fallback
}

function color(probe: HTMLElement, token: string, fallback: string): string {
  probe.style.color = ''
  probe.style.color = `var(${token})`
  return hex(getComputedStyle(probe).color, fallback)
}

/** Defines pnmt-light or pnmt-dark from base.css's tokens, and returns its name. */
export function defineTheme(dark: boolean): string {
  const name = dark ? 'pnmt-dark' : 'pnmt-light'
  const probe = document.createElement('span')
  probe.style.display = 'none'
  document.body.appendChild(probe)
  try {
    const panel = color(probe, '--pnmt-panel', dark ? '#18181b' : '#ffffff')
    const text = color(probe, '--pnmt-text', dark ? '#ffffff' : '#0f172a')
    const muted = color(probe, '--pnmt-muted', dark ? '#a1a1aa' : '#334155')
    const border = color(probe, '--pnmt-border', dark ? '#3f3f46' : '#cbd5e1')
    const background = color(probe, '--pnmt-bg', dark ? '#09090b' : '#f1f5f9')
    const primary = color(probe, '--p-primary-color', dark ? '#8ea3d6' : '#3b59a5')
    const highlight = color(probe, '--p-highlight-background', dark ? '#3b59a540' : '#dfe6f5')
    monaco.editor.defineTheme(name, {
      base: dark ? 'vs-dark' : 'vs',
      inherit: true,
      rules: [],
      colors: {
        'editor.background': panel,
        'editor.foreground': text,
        'editorLineNumber.foreground': muted,
        'editorLineNumber.activeForeground': text,
        'editor.lineHighlightBackground': background,
        'editor.lineHighlightBorder': background,
        'editorCursor.foreground': text,
        'editor.selectionBackground': highlight,
        'editor.inactiveSelectionBackground': highlight,
        'editorIndentGuide.background1': border,
        'editorWidget.background': panel,
        'editorWidget.border': border,
        'editorStickyScroll.background': panel,
        'editorStickyScroll.border': border,
        focusBorder: primary,
        'editorGutter.background': panel,
      },
    })
  } finally {
    probe.remove()
  }
  return name
}

/** The font is JetBrains Mono (bundled, §5.3); Monaco measures it again once it has loaded. */
export const fontFamily = "'JetBrains Mono Variable', ui-monospace, 'SFMono-Regular', Menlo, monospace"
if (typeof document !== 'undefined' && document.fonts) {
  void document.fonts.load(`13px 'JetBrains Mono Variable'`).then(() => monaco.editor.remeasureFonts())
}

export { monaco }
