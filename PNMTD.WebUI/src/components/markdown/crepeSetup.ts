import {
  LanguageDescription,
  LanguageSupport,
  StreamLanguage,
  HighlightStyle,
  syntaxHighlighting,
} from '@codemirror/language'
import { languages as codeMirrorLanguages } from '@codemirror/language-data'
import { EditorView as CodeMirrorView } from '@codemirror/view'
import { tags } from '@lezer/highlight'
import { CrepeBuilder } from '@milkdown/crepe/builder'
import { blockEdit } from '@milkdown/crepe/feature/block-edit'
import { codeMirror } from '@milkdown/crepe/feature/code-mirror'
import { cursor } from '@milkdown/crepe/feature/cursor'
import { imageBlock } from '@milkdown/crepe/feature/image-block'
import { linkTooltip } from '@milkdown/crepe/feature/link-tooltip'
import { listItem } from '@milkdown/crepe/feature/list-item'
import { placeholder } from '@milkdown/crepe/feature/placeholder'
import { table } from '@milkdown/crepe/feature/table'
import { toolbar } from '@milkdown/crepe/feature/toolbar'
import { topBar } from '@milkdown/crepe/feature/top-bar'
import { imageBlockSchema } from '@milkdown/kit/component/image-block'
import {
  editorViewCtx,
  editorViewOptionsCtx,
  remarkStringifyOptionsCtx,
  schemaCtx,
  type Editor,
} from '@milkdown/kit/core'
import type { Ctx } from '@milkdown/kit/ctx'
import { Fragment, type Node as ProseNode, type Schema } from '@milkdown/kit/prose/model'
import { Plugin, PluginKey, type EditorState } from '@milkdown/kit/prose/state'
import { Decoration, DecorationSet, type EditorView } from '@milkdown/kit/prose/view'
import { $prose, insert, replaceAll } from '@milkdown/kit/utils'
import { fileUrl } from './renderMarkdown'
import type { PageLink } from './types'
import type { UploadHandler, UploadIntent } from './useUpload'

/**
 * Crepe, as PNMT sets it up (docs/locations-knowledge-plan.md §5.2). Built with CrepeBuilder from the
 * features one by one, so that what isn't used isn't in the bundle:
 * - on: the top bar (with PNMT's *Attach file* and *Link to a page…*), the toolbar on a selection,
 *   `/` commands and the block handle, tables, task lists, the image block with PNMT's upload, code
 *   blocks with the languages of §5.1 and a `mermaid` block's diagram under it, placeholders;
 * - off: LaTeX (no KaTeX in the bundle) and **the AI feature, never**: it would send text past
 *   PNMT's pipeline and the sensitive rule. crepeSetup.test.ts fails if it is ever configured.
 *
 * Pasting and dropping files uploads them (§5.5): images are shown, anything else becomes a link.
 * Pasted HTML keeps its text; its data: images are uploaded, other sites' images become links.
 */

/** The features in use, by name, for the AI guard's test. */
export const visualFeatures = [
  'cursor',
  'list-item',
  'link-tooltip',
  'image-block',
  'block-edit',
  'toolbar',
  'placeholder',
  'table',
  'code-mirror',
  'top-bar',
] as const

// --- code blocks: the languages of §5.1, named as fences name them ------------------------------

const plainText = new LanguageSupport(
  StreamLanguage.define({
    name: 'text',
    token(stream) {
      stream.skipToEnd()
      return null
    },
  }),
)

function fence(name: string, alias: string[], from: string): LanguageDescription {
  const source = codeMirrorLanguages.find((l) => l.name === from)
  return LanguageDescription.of({
    name,
    alias: [name, ...alias],
    load: () => (source ? source.load() : Promise.resolve(plainText)),
  })
}

function plain(name: string, alias: string[] = []): LanguageDescription {
  return LanguageDescription.of({ name, alias: [name, ...alias], support: plainText })
}

export const codeLanguages: LanguageDescription[] = [
  fence('bash', ['sh', 'shell', 'zsh', 'console'], 'Shell'),
  fence('powershell', ['ps1', 'pwsh'], 'PowerShell'),
  fence('yaml', ['yml'], 'YAML'),
  fence('json', [], 'JSON'),
  fence('xml', ['html', 'svg'], 'XML'),
  fence('ini', ['cfg', 'conf', 'properties'], 'Properties files'),
  fence('toml', [], 'TOML'),
  fence('sql', [], 'SQL'),
  fence('python', ['py'], 'Python'),
  fence('javascript', ['js'], 'JavaScript'),
  fence('csharp', ['cs', 'c#'], 'C#'),
  fence('dockerfile', ['docker'], 'Dockerfile'),
  fence('diff', ['patch'], 'diff'),
  fence('nginx', [], 'Nginx'),
  plain('routeros', ['mikrotik', 'rsc']),
  plain('mermaid'),
  plain('text', ['plaintext', 'txt']),
]

// CodeMirror in PNMT's colours, light and dark alike: the values are base.css's variables.
const codeTheme = [
  CodeMirrorView.theme({
    '&': { backgroundColor: 'var(--pnmt-bg)', color: 'var(--pnmt-text)', fontSize: '13px' },
    '.cm-content': { fontFamily: 'var(--pnmt-mono)', caretColor: 'var(--pnmt-text)' },
    '.cm-gutters': { backgroundColor: 'var(--pnmt-bg)', color: 'var(--pnmt-muted)', border: 'none' },
    '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: 'var(--p-content-hover-background)' },
    '&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection': {
      backgroundColor: 'var(--p-highlight-background) !important',
    },
    '.cm-cursor': { borderLeftColor: 'var(--pnmt-text)' },
  }),
  syntaxHighlighting(
    HighlightStyle.define([
      { tag: [tags.comment, tags.meta], color: 'var(--md-code-comment)', fontStyle: 'italic' },
      { tag: [tags.keyword, tags.modifier, tags.operatorKeyword, tags.tagName], color: 'var(--md-code-keyword)' },
      { tag: [tags.string, tags.inserted, tags.special(tags.string)], color: 'var(--md-code-string)' },
      { tag: [tags.number, tags.bool, tags.null, tags.atom], color: 'var(--md-code-number)' },
      { tag: [tags.propertyName, tags.attributeName, tags.variableName], color: 'var(--md-code-attr)' },
      { tag: [tags.function(tags.variableName), tags.typeName, tags.className], color: 'var(--md-code-title)' },
      { tag: tags.deleted, color: 'var(--md-code-deleted)' },
      { tag: tags.heading, fontWeight: 'bold' },
    ]),
  ),
]

// --- the image block: alt text and title kept --------------------------------------------------

/**
 * Crepe's image block writes `![1.00](src "caption")`: its size ratio as the alt text, the caption as
 * the title. A round trip would turn `![Cage B](…)` into `![1.00](…)`. PNMT's block reads the alt text
 * as the caption and keeps the title, and writes both back; the ratio (resizing in the editor) isn't
 * stored, since Markdown has no size.
 */
export const imageBlockKeepingAlt = imageBlockSchema.extendSchema((previous) => (ctx) => {
  const base = previous(ctx)
  return {
    ...base,
    attrs: { ...base.attrs, title: { default: '', validate: 'string' } },
    parseMarkdown: {
      match: ({ type }) => type === 'image-block',
      runner: (state, node, type) => {
        state.addNode(type, {
          src: (node.url as string) ?? '',
          caption: (node.alt as string | null) ?? '',
          title: (node.title as string | null) ?? '',
          ratio: 1,
        })
      },
    },
    toMarkdown: {
      match: (node) => node.type.name === 'image-block',
      runner: (state, node) => {
        state.openNode('paragraph')
        state.addNode('image', undefined, undefined, {
          url: node.attrs.src,
          alt: node.attrs.caption,
          title: node.attrs.title || null,
        })
        state.closeNode()
      },
    },
  }
})

// --- uploads ----------------------------------------------------------------------------------

const placeholders = new PluginKey<DecorationSet>('PNMT_UPLOADS')

type PlaceholderAction = { add: { id: symbol; pos: number; label: string } } | { remove: { id: symbol } }

/** The placeholders of uploads in flight, as widgets at their positions (§5.2). */
const uploadPlaceholders = $prose(
  () =>
    new Plugin<DecorationSet>({
      key: placeholders,
      state: {
        init: () => DecorationSet.empty,
        apply(tr, set) {
          const next = set.map(tr.mapping, tr.doc)
          const action = tr.getMeta(placeholders) as PlaceholderAction | undefined
          if (action && 'add' in action) {
            const dom = document.createElement('span')
            dom.className = 'pnmt-upload-placeholder'
            dom.dataset.uploadId = String(action.add.id.description)
            dom.textContent = action.add.label
            return next.add(tr.doc, [Decoration.widget(action.add.pos, dom, { id: action.add.id })])
          }
          if (action && 'remove' in action) {
            return next.remove(next.find(undefined, undefined, (spec) => spec.id === action.remove.id))
          }
          return next
        },
      },
      props: {
        decorations(state) {
          return placeholders.getState(state)
        },
      },
    }),
)

function placeholderPos(state: EditorState, id: symbol): number {
  const found = placeholders.getState(state)?.find(undefined, undefined, (spec) => spec.id === id)
  return found?.[0]?.from ?? -1
}

/** The node an uploaded file becomes: an image block for an image, a link otherwise. */
function fileNode(schema: Schema, file: { kind: string; url: string; name: string }): ProseNode | Fragment {
  if (file.kind.toLowerCase() === 'image' && schema.nodes['image-block']) {
    return schema.nodes['image-block'].create({ src: file.url, caption: file.name })
  }
  const link = schema.marks.link!.create({ href: file.url })
  return Fragment.from([schema.text(file.name, [link]), schema.text(' ')])
}

let counter = 0

/** Uploads files and inserts them at `pos`, with a placeholder while each uploads. */
function insertFiles(view: EditorView, files: File[], pos: number, intent: UploadIntent, upload: UploadHandler) {
  for (const file of files) {
    const id = Symbol(`upload-${++counter}`)
    view.dispatch(view.state.tr.setMeta(placeholders, { add: { id, pos, label: `Uploading ${file.name}…` } }))
    const widget = () => view.dom.querySelector<HTMLElement>(`[data-upload-id="${id.description}"]`)
    void upload(file, intent, (fraction) => {
      const dom = widget()
      if (dom) {
        dom.textContent = `Uploading ${file.name}… ${Math.round(fraction * 100)} %`
      }
    }).then((uploaded) => {
      const at = placeholderPos(view.state, id)
      let tr = view.state.tr.setMeta(placeholders, { remove: { id } })
      if (uploaded && at >= 0) {
        const node = fileNode(view.state.schema, uploaded)
        tr = node instanceof Fragment ? tr.insert(at, node) : tr.replaceWith(at, at, node)
      }
      view.dispatch(tr)
    })
  }
}

function filesOf(data: DataTransfer | null | undefined): File[] {
  return data ? Array.from(data.files) : []
}

/** Whether pasted HTML carries text, rather than being the wrapper of a copied image. */
function htmlHasText(html: string): boolean {
  const doc = new DOMParser().parseFromString(html, 'text/html')
  return (doc.body.textContent ?? '').trim().length > 0
}

function dataUrlToFile(dataUrl: string, name: string): File | null {
  const match = dataUrl.match(/^data:(image\/[a-z0-9.+-]+);base64,(.*)$/is)
  if (!match) {
    return null
  }
  const type = match[1]!.toLowerCase()
  const binary = atob(match[2]!.replace(/\s+/g, ''))
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  const extension = type.split('/')[1]!.replace('jpeg', 'jpg').replace('svg+xml', 'svg')
  return new File([bytes], `${name || 'pasted-image'}.${extension}`, { type })
}

/**
 * Pasted HTML (a web page, Word, Outlook), before ProseMirror parses it (§5.2): data: images are
 * uploaded in its place afterwards; another site's image becomes a link *external image: …*; an
 * image ProseMirror can't take over (Word's file:, Outlook's cid:) is left out, with a notice.
 */
function pastedHtml(
  html: string,
  view: EditorView,
  upload: UploadHandler | null,
  notice: (message: string) => void,
): string {
  if (!/<img/i.test(html)) {
    return html
  }
  const doc = new DOMParser().parseFromString(html, 'text/html')
  const pending = new Map<string, File>()
  let skipped = 0
  for (const img of Array.from(doc.querySelectorAll('img'))) {
    const src = (img.getAttribute('src') ?? '').trim()
    const alt = (img.getAttribute('alt') ?? '').trim()
    const local = src.startsWith(location.origin) ? src.slice(location.origin.length) : src
    if (fileUrl.test(local)) {
      img.setAttribute('src', local)
    } else if (/^data:image\//i.test(src) && upload) {
      const file = dataUrlToFile(src, alt.replace(/[^\p{L}\p{N}._-]+/gu, '-').slice(0, 60))
      if (file) {
        const token = `pnmt-pending:${++counter}`
        img.setAttribute('src', token)
        pending.set(token, file)
      } else {
        img.remove()
        skipped++
      }
    } else if (/^https?:\/\//i.test(src)) {
      const link = doc.createElement('a')
      link.setAttribute('href', src)
      link.textContent = alt ? `external image: ${alt}` : `external image: ${src}`
      img.replaceWith(link)
    } else {
      img.remove()
      skipped++
    }
  }
  if (skipped) {
    notice(
      `${skipped === 1 ? 'An image' : `${skipped} images`} of the pasted text couldn't be taken over; copy and paste ${skipped === 1 ? 'it' : 'them'} on ${skipped === 1 ? 'its' : 'their'} own.`,
    )
  }
  if (pending.size && upload) {
    // After ProseMirror inserted the paste: each upload replaces its token, or removes the image.
    setTimeout(() => {
      for (const [token, file] of pending) {
        void upload(file, 'paste').then((uploaded) => replaceToken(view, token, uploaded?.url ?? null))
      }
    })
  }
  return doc.body.innerHTML
}

function replaceToken(view: EditorView, token: string, url: string | null) {
  const found: { pos: number; node: ProseNode }[] = []
  view.state.doc.descendants((node, pos) => {
    if (node.attrs?.src === token) {
      found.push({ pos, node })
    }
  })
  if (!found.length) {
    return
  }
  let tr = view.state.tr
  for (const { pos, node } of found.reverse()) {
    tr = url ? tr.setNodeMarkup(pos, undefined, { ...node.attrs, src: url }) : tr.delete(pos, pos + node.nodeSize)
  }
  view.dispatch(tr)
}

// --- the top bar's labels -----------------------------------------------------------------------

/** Crepe's top bar buttons have only icons: names for them, in their order (TopBar's getGroups). */
const topBarLabels = [
  'Bold (Ctrl/⌘ B)',
  'Italic (Ctrl/⌘ I)',
  'Strikethrough',
  'Code',
  'Bulleted list',
  'Numbered list',
  'Task list',
  'Link (Ctrl/⌘ K)',
  'Image',
  'Table',
  'Attach file',
  'Link to a page…',
  'Code block',
  'Quote',
  'Horizontal rule',
]

function labelTopBar(root: HTMLElement, hasPageLink: boolean) {
  const labels = hasPageLink ? topBarLabels : topBarLabels.filter((l) => l !== 'Link to a page…')
  root.querySelectorAll<HTMLButtonElement>('.milkdown-top-bar button.top-bar-item').forEach((button, i) => {
    const label = labels[i]
    if (label && button.getAttribute('aria-label') !== label) {
      button.setAttribute('aria-label', label)
      button.title = label
    }
  })
}

const attachIcon =
  '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><path fill="currentColor" d="M16.5 6v11.5c0 2.21-1.79 4-4 4s-4-1.79-4-4V5c0-1.38 1.12-2.5 2.5-2.5s2.5 1.12 2.5 2.5v10.5c0 .55-.45 1-1 1s-1-.45-1-1V6H10v9.5c0 1.38 1.12 2.5 2.5 2.5s2.5-1.12 2.5-2.5V5c0-2.21-1.79-4-4-4S7 2.79 7 5v12.5c0 3.04 2.46 5.5 5.5 5.5s5.5-2.46 5.5-5.5V6h-1.5z"/></svg>'
const pageIcon =
  '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><path fill="currentColor" d="M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-5 14H7v-2h7v2zm3-4H7v-2h10v2zm0-4H7V7h10v2z"/></svg>'

// --- the editor ---------------------------------------------------------------------------------

export interface VisualEditorOptions {
  root: HTMLElement
  markdown: string
  placeholder: string
  /** Uploads a file; null: files can't be pasted here. */
  upload: UploadHandler | null
  /** *Link to a page…*; null hides it. */
  pickPage: (() => Promise<PageLink | null>) | null
  /** A diagram's preview for a `mermaid` code block, as an element; undefined while it draws. */
  renderDiagram: (source: string, apply: (preview: HTMLElement | null) => void) => void
  onChange: (markdown: string) => void
  notice: (message: string) => void
}

export interface VisualEditorHandle {
  /** Milkdown's editor, for looking inside (the AI guard's test reads its features). */
  editor: Editor
  /** The Markdown Crepe writes for the document now. */
  markdown(): string
  setMarkdown(markdown: string): void
  /** Inserts Markdown at the cursor: a file's link, a page's. */
  insertMarkdown(markdown: string): void
  focus(): void
  destroy(): Promise<void>
}

/** Creates Crepe in `root`. Throws if it doesn't start; the caller falls back to the textarea (§5.4). */
export async function createVisualEditor(options: VisualEditorOptions): Promise<VisualEditorHandle> {
  const { root, upload } = options
  const pickFiles = (accept: string, intent: UploadIntent, ctx: Ctx) => {
    if (!upload) {
      options.notice('Files can’t be added here.')
      return
    }
    const input = document.createElement('input')
    input.type = 'file'
    input.multiple = true
    input.accept = accept
    input.onchange = () => {
      const view = ctx.get(editorViewCtx)
      insertFiles(view, Array.from(input.files ?? []), view.state.selection.from, intent, upload)
    }
    input.click()
  }

  const crepe = new CrepeBuilder({ root, defaultValue: options.markdown })
  crepe
    .addFeature(cursor)
    .addFeature(listItem)
    .addFeature(linkTooltip, { inputPlaceholder: 'Paste a link, or /pnmt-admin/kb/12' })
    .addFeature(imageBlock, {
      onUpload: async (file: File) => {
        const uploaded = upload ? await upload(file, 'image') : null
        if (!uploaded) {
          throw new Error('Not uploaded')
        }
        return uploaded.url
      },
      blockUploadButton: 'Upload an image',
      blockUploadPlaceholderText: 'or paste its link',
      blockCaptionPlaceholderText: 'Describe the image (its alt text)',
      inlineUploadPlaceholderText: 'or paste its link',
    })
    .addFeature(blockEdit, {
      advancedGroup: { math: null },
    })
    .addFeature(toolbar)
    .addFeature(placeholder, { text: options.placeholder, mode: 'doc' })
    .addFeature(table)
    .addFeature(codeMirror, {
      languages: codeLanguages,
      theme: codeTheme,
      searchPlaceholder: 'Search a language',
      noResultText: 'No such language',
      copyText: 'Copy',
      previewLabel: 'Diagram',
      previewOnlyByDefault: false,
      previewLoading: 'Drawing the diagram…',
      renderPreview: (language, content, apply) => {
        if (language.toLowerCase() !== 'mermaid' || !content.trim()) {
          return null
        }
        options.renderDiagram(content, apply)
        return undefined
      },
    })
    .addFeature(topBar, {
      buildTopBar: (builder) => {
        const insertGroup = builder.getGroup('insert')
        insertGroup.addItem('attach-file', {
          icon: attachIcon,
          active: () => false,
          onRun: (ctx) => pickFiles('', 'attach', ctx),
        })
        if (options.pickPage) {
          const pick = options.pickPage
          insertGroup.addItem('page-link', {
            icon: pageIcon,
            active: () => false,
            onRun: (ctx) => {
              void pick().then((link) => {
                if (!link) {
                  return
                }
                const view = ctx.get(editorViewCtx)
                const schema = ctx.get(schemaCtx)
                const mark = schema.marks.link!.create({ href: link.href })
                view.dispatch(view.state.tr.replaceSelectionWith(schema.text(link.label, [mark]), false))
                view.focus()
              })
            },
          })
        }
      },
    })

  crepe.editor
    .config((ctx) => {
      // How Crepe writes what it rewrites (§5.4): `-` lists and `---` rules, as most Markdown is written.
      ctx.update(remarkStringifyOptionsCtx, (previous) => ({ ...previous, bullet: '-' as const, rule: '-' as const }))
      ctx.update(editorViewOptionsCtx, (previous) => ({
        ...previous,
        attributes: { ...(previous.attributes ?? {}), 'aria-label': 'Visual editor', spellcheck: 'true' },
        handlePaste: (view, event) => {
          const files = filesOf(event.clipboardData)
          if (!files.length) {
            return false
          }
          const html = event.clipboardData?.getData('text/html') ?? ''
          // Word, Outlook and web pages paste text with a picture of it: the text is what's meant.
          if (html && htmlHasText(html)) {
            return false
          }
          if (!upload) {
            options.notice('Files can’t be pasted here.')
            return true
          }
          insertFiles(view, files, view.state.selection.from, 'paste', upload)
          return true
        },
        handleDrop: (view, event) => {
          const files = filesOf(event.dataTransfer)
          if (!files.length) {
            return false
          }
          event.preventDefault()
          if (!upload) {
            options.notice('Files can’t be dropped here.')
            return true
          }
          const pos = view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos ?? view.state.selection.from
          insertFiles(view, files, pos, 'paste', upload)
          return true
        },
        transformPastedHTML: (html, view) => pastedHtml(html, view, upload, options.notice),
      }))
    })
    .use(imageBlockKeepingAlt)
    .use(uploadPlaceholders)

  crepe.on((listener) => listener.markdownUpdated((_ctx, markdown) => options.onChange(markdown)))

  await crepe.create()

  const hasPageLink = !!options.pickPage
  labelTopBar(root, hasPageLink)
  const labels = new MutationObserver(() => labelTopBar(root, hasPageLink))
  labels.observe(root, { childList: true, subtree: true })

  return {
    editor: crepe.editor,
    markdown: () => crepe.getMarkdown(),
    setMarkdown: (markdown) => crepe.editor.action(replaceAll(markdown, true)),
    insertMarkdown: (markdown) => crepe.editor.action(insert(markdown, true)),
    focus: () => crepe.editor.action((ctx) => ctx.get(editorViewCtx).focus()),
    destroy: async () => {
      labels.disconnect()
      await crepe.destroy()
    },
  }
}
