<script setup lang="ts">
/**
 * The Markdown editor (docs/locations-knowledge-plan.md §5): one component for knowledge base pages
 * and location fields. Crepe (Visual) and Monaco (Markdown) behind a switch, a <textarea> where they
 * can't run, uploads by paste, drop and buttons, drafts, and the dialogs of §5.7.
 *
 * ## Use
 *
 * Mount it once the text is loaded (v-if), on its own route (`layout="page"`, the default):
 *
 *     <MarkdownEditor
 *       ref="editor"
 *       v-model="content"                       the Markdown, as the active editor writes it
 *       mode="page"                             'page' | 'field': which remembered mode (§5.4)
 *       :base-revision="page.revision"          the revision the edit started from
 *       :draft-key="`kb:${page.id}`"            drafts in this browser (§5.7); null: none
 *       back-label="KB-42"
 *       :saving="saving"
 *       :files="page.files"                     the files' metadata, for previews' chips
 *       details-summary="summary · 2 tags"      the folded details row (pages); leave out for none
 *       v-model:details-open="detailsOpen"      a new page opens them
 *       :details-dirty="metaChanged"            the details' own unsaved changes, for leaving
 *       :pick-page="pickPage"                   *Link to a page…*: resolves to { label, href } or null
 *       can-mark-sensitive                      pages: the credentials warning offers the sensitive mark
 *       v-model:conflict="conflict"             set after a 409 edit_conflict (§5.7)
 *       v-model:credentials="credentials"       set when the text looks like it holds a credential
 *       @save="save" @cancel="router.back()" @dirty="dirty = $event">
 *       <template #title>…</template>           the title field (pages), or path and label (fields)
 *       <template #status>…</template>          the status (pages)
 *       <template #others>…</template>          *Anna Muster is editing this page (since 10:42)*
 *       <template #details>…</template>         summary, tags, links, note (pages)
 *     </MarkdownEditor>
 *
 * ## Props
 *
 * - `modelValue` (v-model): the Markdown. It changes only when the person edits: opening the visual
 *   editor, or switching back and forth, never rewrites it by itself.
 * - `mode`: 'page' or 'field'. The last mode is remembered per browser, apart for the two.
 * - `layout`: 'page' (EditingPage: the whole window, the header, the drawer as a rail, Ctrl/⌘ S, and
 *   leaving with changes asks) or 'inline' (a framed box of `height`, for the documentation's example).
 * - `baseRevision`, `draftKey`: a draft is kept per key while editing; one from a crash is offered on
 *   opening (*Restore*, *Discard*). Restoring a draft made on another revision makes the save carry
 *   that revision, so that the server answers 409 rather than overwriting.
 * - `uploads` (default true): false refuses pasted and dropped files (Viewers, an example).
 * - `saving`, `canSave`, `saveLabel`, `backLabel`, `placeholder`, `files`, `pickPage`,
 *   `canMarkSensitive`, `detailsSummary`, `detailsDirty`, `height` (inline only).
 *
 * ## Events
 *
 * - `save` (MarkdownSave): from Save, Ctrl/⌘ S or a dialog. `revision` is what to send; `overwrite`,
 *   `acknowledgeCredentials` and `markSensitive` say which dialog answer led to it.
 * - `cancel`: Cancel or back, after the person agreed to drop their changes. Navigate away.
 * - `dirty` (boolean): unsaved changes, in the text or (`detailsDirty`) the details.
 *
 * ## Methods (template ref)
 *
 * - `saved()`: call once the server accepted the save, before navigating: the draft is cleared and
 *   the text counts as saved, so leaving doesn't ask.
 * - `focus()`, `insertMarkdown(text)`.
 *
 * ## The conflict and the credentials warning: the view drives them
 *
 * The view saves; on 409 edit_conflict it sets `conflict` ({ by, at, revision, theirs, base? }). The
 * editor shows *Their changes* and *My changes* (diffs from the base) and *Save mine anyway*, which
 * emits `save` with the conflict's revision and `overwrite: true`; closing sets `conflict` to null.
 *
 * When the view learns that the text looks like it holds a credential (its check before saving, or
 * the server's answer), it sets `credentials` ({ lines, kinds? }). The editor warns (*Line 12 looks
 * like a password. PNMT is not a password manager.*) and offers *Save anyway* and *Mark as sensitive
 * and save* (emitting `save` again with `acknowledgeCredentials`, and `markSensitive`), or *Edit*,
 * which goes to the line. It doesn't block.
 */
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useMedia } from '@/composables/useMedia'
import { useNotify } from '@/composables/useNotify'
import { usePreferences, type EditorMode } from '@/stores/preferences'
import EditingPage from './EditingPage.vue'
import { fileMarkdown } from './files'
import { rewriteSummary } from './lineDiff'
import MarkdownView from './MarkdownView.vue'
import type {
  CredentialsWarning,
  EditConflict,
  EditorModeKey,
  MarkdownFile,
  MarkdownSave,
  PageLink,
  Revision,
} from './types'
import {
  chooseKind,
  UploadError,
  useUpload,
  type UploadHandler,
  type UploadIntent,
  type UploadResult,
} from './useUpload'

const props = withDefaults(
  defineProps<{
    mode: EditorModeKey
    layout?: 'page' | 'inline'
    baseRevision?: Revision | null
    draftKey?: string | null
    backLabel?: string
    saving?: boolean
    canSave?: boolean
    saveLabel?: string
    detailsSummary?: string
    detailsDirty?: boolean
    uploads?: boolean
    pickPage?: (() => Promise<PageLink | null>) | null
    canMarkSensitive?: boolean
    files?: readonly MarkdownFile[] | null
    placeholder?: string
    /** The inline layout's height. */
    height?: string
  }>(),
  {
    layout: 'page',
    baseRevision: null,
    draftKey: null,
    backLabel: 'Back',
    saving: false,
    canSave: true,
    saveLabel: 'Save',
    detailsSummary: undefined,
    detailsDirty: false,
    uploads: true,
    pickPage: null,
    canMarkSensitive: false,
    files: null,
    placeholder: undefined,
    height: '28rem',
  },
)
const markdown = defineModel<string>({ required: true })
const conflict = defineModel<EditConflict | null>('conflict', { default: null })
const credentials = defineModel<CredentialsWarning | null>('credentials', { default: null })
const detailsOpen = defineModel<boolean>('detailsOpen', { default: false })
const emit = defineEmits<{
  save: [save: MarkdownSave]
  cancel: []
  dirty: [dirty: boolean]
}>()

const preferences = usePreferences()
const notify = useNotify()
// Monaco isn't for phones (§5.3): narrow and touch, or very narrow.
const phone = useMedia('(max-width: 599px), (pointer: coarse) and (max-width: 959px)')
const wide = useMedia('(min-width: 960px)')

// --- the editors and the switch (§5.4) -----------------------------------------------------------

/** Why the textarea took over: an editor's chunk didn't load, or it didn't start. */
const failed = ref<string | null>(null)
function fail(what: 'visual' | 'code', error: unknown) {
  console.error(`The ${what} editor didn't start`, error)
  checking.value = false
  failed.value = 'The editor couldn’t start; editing as plain text.'
}

const VisualEditor = defineAsyncComponent({
  loader: () => import('./VisualEditor.vue'),
  onError: (error, _retry, giveUp) => {
    giveUp()
    fail('visual', error)
  },
})
const CodeEditor = defineAsyncComponent({
  loader: () => import('./CodeEditor.vue'),
  onError: (error, _retry, giveUp) => {
    giveUp()
    fail('code', error)
  },
})

const wanted = computed<EditorMode>({
  get: () => preferences.editorModes[props.mode],
  set: (mode) => (preferences.editorModes[props.mode] = mode),
})
type Surface = 'visual' | 'code' | 'textarea'
const surface = computed<Surface>(() =>
  failed.value ? 'textarea' : wanted.value === 'visual' ? 'visual' : phone.value ? 'textarea' : 'code',
)
const markdownMode = computed(() => surface.value !== 'visual')
const modeOptions = [
  { label: 'Visual', value: 'visual' },
  { label: 'Markdown', value: 'markdown' },
]

const visual = ref<{
  markdown(): string
  flush(): string
  focus(): void
  insertMarkdown(text: string): void
} | null>(null)
/** The visual editor has edits it hasn't written as Markdown yet (VisualEditor's `pending`). */
const visualPending = ref(false)

/**
 * The text as the active editor holds it now, for a save, a draft or a switch: v-model reaches the
 * view a moment later, and the visual editor writes its Markdown only after a pause in typing.
 */
function current(): string {
  if (surface.value === 'visual' && visual.value) {
    return visual.value.flush()
  }
  if (surface.value === 'code' && code.value) {
    return code.value.value()
  }
  if (surface.value === 'textarea' && textarea.value) {
    return textarea.value.value
  }
  return markdown.value
}
const code = ref<{
  value(): string
  focus(): void
  insertMarkdown(text: string): void
  revealLine(line: number): void
} | null>(null)
const textarea = ref<HTMLTextAreaElement | null>(null)

/** Markdown → Visual: the visual editor opens hidden first, and what it would write is compared (§5.4). */
const checking = ref(false)
const rewrite = ref<{ lines: number; example: { from: string; to: string } | null; text: string } | null>(null)
const rewriteDiff = ref(false)
const MonacoDiff = defineAsyncComponent(() => import('./MonacoDiff.vue'))

/**
 * Crepe has started. Until then the text is shown read-only in its place: what was typed into an
 * editor that isn't there yet would be lost.
 */
const visualReady = ref(false)
watch(
  () => surface.value === 'visual' || checking.value,
  (mounted) => {
    if (!mounted) {
      visualReady.value = false
    }
  },
)

function setMode(mode: EditorMode | null) {
  if (!mode || mode === wanted.value) {
    return
  }
  if (mode === 'markdown') {
    current()
    checking.value = false
    wanted.value = 'markdown'
    return
  }
  checking.value = true
}

function onVisualReady(written: string) {
  visualReady.value = true
  if (!checking.value) {
    return
  }
  const summary = rewriteSummary(markdown.value, written)
  if (summary.lines === 0) {
    checking.value = false
    wanted.value = 'visual'
    return
  }
  rewrite.value = { ...summary, text: written }
  rewriteDiff.value = false
}

function switchAnyway() {
  rewrite.value = null
  checking.value = false
  wanted.value = 'visual'
}

function stay() {
  rewrite.value = null
  checking.value = false
}

const previewOpen = ref(false)

// --- the text as it started, drafts (§5.7) -------------------------------------------------------

const pristine = ref(markdown.value)
const dirty = computed(() => markdown.value !== pristine.value || visualPending.value || props.detailsDirty)
watch(dirty, (value) => emit('dirty', value))

interface Draft {
  revision: Revision | null
  text: string
  savedAt: string
}
const draftStorage = computed(() => (props.draftKey ? `pnmt.draft.${props.draftKey}` : null))
const draftOffer = ref<Draft | null>(null)
/** The revision of a restored draft that began on another one; undefined: the base revision. */
const draftRevision = ref<Revision | null | undefined>(undefined)
let draftTimer: ReturnType<typeof setTimeout> | undefined

function readDraft(): Draft | null {
  if (!draftStorage.value) {
    return null
  }
  try {
    const draft = JSON.parse(localStorage.getItem(draftStorage.value) ?? 'null') as Draft | null
    return draft && typeof draft.text === 'string' ? draft : null
  } catch {
    return null
  }
}

function removeDraft() {
  clearTimeout(draftTimer)
  if (draftStorage.value) {
    localStorage.removeItem(draftStorage.value)
  }
}

function writeDraft() {
  if (!draftStorage.value || draftOffer.value) {
    return
  }
  const text = current()
  if (text === pristine.value) {
    localStorage.removeItem(draftStorage.value)
    return
  }
  const draft: Draft = { revision: revision(), text, savedAt: new Date().toISOString() }
  try {
    localStorage.setItem(draftStorage.value, JSON.stringify(draft))
  } catch {
    // Full storage: the draft is a convenience, the editor goes on.
  }
}

watch(markdown, () => {
  clearTimeout(draftTimer)
  draftTimer = setTimeout(writeDraft, 800)
})

function restoreDraft() {
  const draft = draftOffer.value
  draftOffer.value = null
  if (!draft) {
    return
  }
  if (draft.revision !== props.baseRevision) {
    draftRevision.value = draft.revision
  }
  markdown.value = draft.text
}

function discardDraft() {
  draftOffer.value = null
  removeDraft()
}

const draftTime = computed(() => {
  const at = draftOffer.value ? new Date(draftOffer.value.savedAt) : null
  if (!at || Number.isNaN(at.getTime())) {
    return ''
  }
  const today = new Date().toDateString() === at.toDateString()
  return today
    ? at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : at.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
})

// A new base revision (the view saved and reloaded): the text counts as saved.
watch(
  () => props.baseRevision,
  () => saved(),
)

function revision(): Revision | null {
  return draftRevision.value !== undefined ? draftRevision.value : props.baseRevision
}

/** The server accepted the save: no draft, nothing unsaved (leaving doesn't ask). */
function saved() {
  // What was saved; anything typed while the save was on its way stays unsaved.
  pristine.value = lastSave?.markdown ?? current()
  lastSave = null
  draftRevision.value = undefined
  draftOffer.value = null
  removeDraft()
}

/** Cancel or leaving, confirmed: the changes and their draft go. */
function discard() {
  removeDraft()
  visualPending.value = false
  pristine.value = current()
}

// --- saving and its dialogs (§5.7) --------------------------------------------------------------

let lastSave: MarkdownSave | null = null

function save(flags: Partial<MarkdownSave> = {}) {
  if (!props.canSave || props.saving) {
    return
  }
  lastSave = {
    markdown: current(),
    revision: revision(),
    overwrite: false,
    acknowledgeCredentials: false,
    markSensitive: false,
    ...flags,
  }
  emit('save', lastSave)
}

const conflictView = ref<'theirs' | 'mine'>('theirs')
const conflictBase = computed(() => conflict.value?.base ?? pristine.value)
const conflictTime = computed(() =>
  conflict.value ? new Date(conflict.value.at).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) : '',
)

function saveMineAnyway() {
  const theirs = conflict.value
  conflict.value = null
  if (theirs) {
    save({ revision: theirs.revision, overwrite: true })
  }
}

const credentialsText = computed(() => {
  const lines = credentials.value?.lines ?? []
  const kinds = credentials.value?.kinds ?? []
  if (lines.length === 1) {
    return `Line ${lines[0]} looks like ${kinds[0] ?? 'a password'}.`
  }
  const list = lines.length > 2 ? `${lines.slice(0, -1).join(', ')} and ${lines.at(-1)}` : lines.join(' and ')
  return `Lines ${list} look like credentials.`
})

function answerCredentials(answer: 'save' | 'sensitive' | 'edit') {
  const first = credentials.value?.lines[0]
  credentials.value = null
  if (answer === 'edit') {
    if (first) {
      revealLine(first)
    }
    return
  }
  save({
    ...(lastSave ?? {}),
    markdown: current(),
    acknowledgeCredentials: true,
    markSensitive: answer === 'sensitive',
  })
}

function revealLine(line: number) {
  if (surface.value === 'code') {
    code.value?.revealLine(line)
  } else if (surface.value === 'textarea' && textarea.value) {
    const lines = markdown.value.split('\n')
    const start = lines.slice(0, line - 1).reduce((n, l) => n + l.length + 1, 0)
    textarea.value.focus()
    textarea.value.setSelectionRange(start, start + (lines[line - 1]?.length ?? 0))
  } else {
    visual.value?.focus()
  }
}

// --- uploads (§5.5) -----------------------------------------------------------------------------

interface Notice {
  id: number
  severity: 'info' | 'warn'
  text: string
  action?: { label: string; run: () => void }
}
const notices = ref<Notice[]>([])
let noticeCount = 0
const uploader = useUpload()
const uploadedHere = ref<MarkdownFile[]>([])
// Without the view's list, chips show their link's text; with it, this session's uploads join it.
const previewFiles = computed<MarkdownFile[] | null>(() =>
  props.files ? [...props.files, ...uploadedHere.value] : null,
)
const inFlight = uploader.active

function addNotice(notice: Omit<Notice, 'id'>) {
  notices.value = [...notices.value.slice(-3), { ...notice, id: ++noticeCount }]
}

function dismiss(id: number) {
  notices.value = notices.value.filter((n) => n.id !== id)
}

const handleUpload: UploadHandler = async (file, intent, onProgress) => {
  if (!props.uploads) {
    addNotice({ severity: 'info', text: 'Files can’t be added here.' })
    return null
  }
  const kind = chooseKind(file, intent)
  let result: UploadResult
  try {
    result = await uploader.upload(file, kind, onProgress)
  } catch (error) {
    if (error instanceof UploadError && error.attachInstead && kind === 'image') {
      addNotice({
        severity: 'warn',
        text: error.message,
        action: { label: 'Attach it as a file', run: () => void attachInstead(file) },
      })
    } else {
      notify.error(error, 'Not uploaded')
    }
    return null
  }
  const uploaded = result.file
  uploadedHere.value = [
    ...uploadedHere.value,
    {
      id: uploaded.id,
      name: uploaded.name,
      size: uploaded.size,
      contentType: uploaded.contentType,
      kind: uploaded.kind as MarkdownFile['kind'],
    },
  ]
  if (result.notice && result.original) {
    const original = result.original
    addNotice({
      severity: 'info',
      text: result.notice,
      action: { label: 'Attach the original instead', run: () => void attachOriginal(uploaded.url, original) },
    })
  }
  return uploaded
}
const upload = computed<UploadHandler | null>(() => (props.uploads ? handleUpload : null))

async function attachInstead(file: File) {
  const uploaded = await handleUpload(file, 'attach')
  if (uploaded) {
    insertMarkdown(fileMarkdown(uploaded))
  }
}

/** *Attach the original instead* (§5.5): the recompressed image becomes a link to the original. */
async function attachOriginal(imageUrl: string, original: File) {
  const uploaded = await handleUpload(original, 'attach')
  if (!uploaded) {
    return
  }
  const escaped = imageUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const image = new RegExp(`!\\[[^\\]]*\\]\\(${escaped}(?:\\s+"[^"]*")?\\)`)
  if (image.test(markdown.value)) {
    markdown.value = markdown.value.replace(image, fileMarkdown(uploaded))
  } else {
    insertMarkdown(fileMarkdown(uploaded))
  }
}

function insertMarkdown(text: string) {
  if (surface.value === 'visual') {
    visual.value?.insertMarkdown(text)
  } else if (surface.value === 'code') {
    code.value?.insertMarkdown(text)
  } else {
    insertInTextarea(text)
  }
}

/** *Image* and *Attach file* in the Markdown modes (Crepe has its own in its top bar). */
function pickFiles(intent: UploadIntent) {
  const input = document.createElement('input')
  input.type = 'file'
  input.multiple = true
  input.accept = intent === 'image' ? 'image/png,image/jpeg,image/gif,image/webp' : ''
  input.onchange = () => {
    for (const file of Array.from(input.files ?? [])) {
      void handleUpload(file, intent).then((uploaded) => uploaded && insertMarkdown(fileMarkdown(uploaded)))
    }
  }
  input.click()
}

// --- the textarea (§5.4) ------------------------------------------------------------------------

let textareaUploads = 0

function insertInTextarea(text: string) {
  const el = textarea.value
  const start = el?.selectionStart ?? markdown.value.length
  const end = el?.selectionEnd ?? start
  markdown.value = markdown.value.slice(0, start) + text + markdown.value.slice(end)
  void nextTick(() => {
    el?.focus()
    el?.setSelectionRange(start + text.length, start + text.length)
  })
}

function uploadIntoTextarea(files: File[]) {
  for (const file of files) {
    const placeholder = `[Uploading ${file.name.replace(/[[\]]/g, '')}… #${++textareaUploads}]()`
    insertInTextarea(placeholder)
    void handleUpload(file, 'paste').then((uploaded) => {
      markdown.value = markdown.value.replace(placeholder, uploaded ? fileMarkdown(uploaded) : '')
    })
  }
}

function onTextareaPaste(event: ClipboardEvent) {
  const files = Array.from(event.clipboardData?.files ?? [])
  if (files.length && !event.clipboardData?.getData('text/plain')) {
    event.preventDefault()
    uploadIntoTextarea(files)
  }
}

function onTextareaDrop(event: DragEvent) {
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (files.length) {
    event.preventDefault()
    uploadIntoTextarea(files)
  }
}

function onTextareaDragOver(event: DragEvent) {
  if (event.dataTransfer?.types.includes('Files')) {
    event.preventDefault()
  }
}

// --- the inline layout ----------------------------------------------------------------------------

function onInlineKey(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 's') {
    event.preventDefault()
    save()
  }
}

onMounted(() => {
  const draft = readDraft()
  if (draft && draft.text !== markdown.value) {
    draftOffer.value = draft
  }
})

onBeforeUnmount(() => {
  clearTimeout(draftTimer)
  // A draft still waiting to be written (a closed tab writes nothing more).
  if (dirty.value || current() !== pristine.value) {
    writeDraft()
  }
})

defineExpose({
  saved,
  focus: () =>
    surface.value === 'visual'
      ? visual.value?.focus()
      : surface.value === 'code'
        ? code.value?.focus()
        : textarea.value?.focus(),
  insertMarkdown,
})
</script>

<template>
  <component
    :is="layout === 'page' ? EditingPage : 'section'"
    v-bind="
      layout === 'page'
        ? { backLabel, saving, canSave, dirty, detailsSummary, saveLabel, detailsOpen }
        : { class: 'md-inline', style: { '--md-inline-height': height }, 'aria-label': 'Markdown editor' }
    "
    @update:details-open="(open: boolean) => (detailsOpen = open)"
    @save="save()"
    @cancel="emit('cancel')"
    @discard="discard"
    @keydown="layout === 'inline' ? onInlineKey($event) : undefined"
  >
    <template v-if="layout === 'page'" #title><slot name="title" /></template>
    <template v-if="layout === 'page' && $slots.status" #status><slot name="status" /></template>
    <template v-if="layout === 'page' && $slots.others" #others><slot name="others" /></template>
    <template v-if="layout === 'page'" #details><slot name="details" /></template>
    <template v-if="layout === 'page'" #mode>
      <div class="md-mode">
        <SelectButton
          v-if="!failed"
          :model-value="checking ? 'visual' : surface === 'visual' ? 'visual' : 'markdown'"
          :options="modeOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-label="Editor"
          @update:model-value="setMode"
        />
        <template v-if="markdownMode">
          <Button
            v-tooltip.bottom="'Preview'"
            :label="wide ? 'Preview' : undefined"
            icon="pi pi-eye"
            size="small"
            :severity="previewOpen ? undefined : 'secondary'"
            :outlined="!previewOpen"
            :aria-pressed="previewOpen"
            aria-label="Preview"
            @click="previewOpen = !previewOpen"
          />
          <template v-if="uploads">
            <Button
              v-tooltip.bottom="'Image'"
              icon="pi pi-image"
              size="small"
              text
              severity="secondary"
              aria-label="Add an image"
              @click="pickFiles('image')"
            />
            <Button
              v-tooltip.bottom="'Attach file'"
              icon="pi pi-paperclip"
              size="small"
              text
              severity="secondary"
              aria-label="Attach a file"
              @click="pickFiles('attach')"
            />
          </template>
        </template>
      </div>
    </template>
    <template v-if="layout === 'page'" #notices>
      <template v-if="draftOffer || failed || notices.length || inFlight.length">
        <Message v-if="draftOffer" severity="warn" class="md-notice">
          <span>You have unsaved changes from {{ draftTime }}.</span>
          <Button label="Restore" size="small" text @click="restoreDraft" />
          <Button label="Discard" size="small" text severity="secondary" @click="discardDraft" />
        </Message>
        <Message v-if="failed" severity="info" class="md-notice">{{ failed }}</Message>
        <Message v-for="n in notices" :key="n.id" :severity="n.severity" class="md-notice">
          <span>{{ n.text }}</span>
          <Button v-if="n.action" :label="n.action.label" size="small" text @click="(n.action.run(), dismiss(n.id))" />
          <Button
            icon="pi pi-times"
            size="small"
            text
            severity="secondary"
            aria-label="Dismiss"
            @click="dismiss(n.id)"
          />
        </Message>
        <div v-for="u in inFlight" :key="u.id" class="md-progress muted" role="status">
          <i class="pi pi-spin pi-spinner" aria-hidden="true" /> Uploading {{ u.name }}…
          {{ Math.round(u.progress * 100) }} %
        </div>
      </template>
    </template>

    <!-- The inline layout's own bar: the same controls. -->
    <div v-if="layout === 'inline'" class="md-inline-bar">
      <SelectButton
        v-if="!failed"
        :model-value="checking ? 'visual' : surface === 'visual' ? 'visual' : 'markdown'"
        :options="modeOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Editor"
        @update:model-value="setMode"
      />
      <template v-if="markdownMode">
        <Button
          label="Preview"
          icon="pi pi-eye"
          size="small"
          :severity="previewOpen ? undefined : 'secondary'"
          :outlined="!previewOpen"
          :aria-pressed="previewOpen"
          @click="previewOpen = !previewOpen"
        />
        <template v-if="uploads">
          <Button
            icon="pi pi-image"
            size="small"
            text
            severity="secondary"
            aria-label="Add an image"
            @click="pickFiles('image')"
          />
          <Button
            icon="pi pi-paperclip"
            size="small"
            text
            severity="secondary"
            aria-label="Attach a file"
            @click="pickFiles('attach')"
          />
        </template>
      </template>
      <span class="spacer" />
      <slot name="bar" />
    </div>
    <div
      v-if="layout === 'inline' && (draftOffer || failed || notices.length || inFlight.length)"
      class="md-inline-notices"
    >
      <Message v-if="draftOffer" severity="warn" class="md-notice">
        <span>You have unsaved changes from {{ draftTime }}.</span>
        <Button label="Restore" size="small" text @click="restoreDraft" />
        <Button label="Discard" size="small" text severity="secondary" @click="discardDraft" />
      </Message>
      <Message v-if="failed" severity="info" class="md-notice">{{ failed }}</Message>
      <Message v-for="n in notices" :key="n.id" :severity="n.severity" class="md-notice">
        <span>{{ n.text }}</span>
        <Button v-if="n.action" :label="n.action.label" size="small" text @click="(n.action.run(), dismiss(n.id))" />
        <Button icon="pi pi-times" size="small" text severity="secondary" aria-label="Dismiss" @click="dismiss(n.id)" />
      </Message>
      <div v-for="u in inFlight" :key="u.id" class="md-progress muted" role="status">
        <i class="pi pi-spin pi-spinner" aria-hidden="true" /> Uploading {{ u.name }}…
        {{ Math.round(u.progress * 100) }} %
      </div>
    </div>

    <div class="md-surface" :class="`surface-${surface}`">
      <div v-if="surface === 'visual' && !visualReady" class="md-starting">
        <p class="md-starting-note muted" role="status">
          <i class="pi pi-spin pi-spinner" aria-hidden="true" /> Starting the visual editor…
        </p>
        <textarea
          class="md-textarea"
          readonly
          :value="markdown"
          aria-label="Markdown, read-only until the editor has started"
        />
      </div>
      <VisualEditor
        v-if="surface === 'visual' || checking"
        v-show="surface === 'visual' && visualReady"
        ref="visual"
        v-model="markdown"
        :upload="upload"
        :pick-page="pickPage"
        :placeholder="placeholder"
        @ready="onVisualReady"
        @pending="(p: boolean) => (visualPending = p)"
        @failed="(e: unknown) => fail('visual', e)"
        @notice="(text: string) => addNotice({ severity: 'info', text })"
      />
      <CodeEditor
        v-if="surface === 'code'"
        ref="code"
        v-model="markdown"
        :upload="upload"
        :preview="previewOpen"
        :files="previewFiles"
        :placeholder="placeholder"
        :readonly="checking"
        @failed="(e: unknown) => fail('code', e)"
        @notice="(text: string) => addNotice({ severity: 'info', text })"
      />
      <div
        v-if="surface === 'textarea'"
        class="md-plain"
        :class="{ split: previewOpen && wide, 'preview-only': previewOpen && !wide }"
      >
        <textarea
          v-show="!(previewOpen && !wide)"
          ref="textarea"
          v-model="markdown"
          class="md-textarea"
          spellcheck="true"
          aria-label="Markdown"
          :readonly="checking"
          :placeholder="placeholder ?? 'Markdown'"
          @paste="onTextareaPaste"
          @drop="onTextareaDrop"
          @dragover="onTextareaDragOver"
        />
        <section v-if="previewOpen" class="md-plain-preview" aria-label="Preview">
          <MarkdownView :source="markdown" :files="previewFiles" />
        </section>
      </div>
    </div>
  </component>

  <Dialog
    :visible="!!rewrite"
    modal
    header="Switch to the visual editor?"
    :style="{ width: rewriteDiff ? 'min(64rem, 95vw)' : '34rem' }"
    @update:visible="(v: boolean) => !v && stay()"
  >
    <p v-if="rewrite">
      The visual editor will rewrite {{ rewrite.lines }} {{ rewrite.lines === 1 ? 'line' : 'lines'
      }}<template v-if="rewrite.example"
        >, for example <code>{{ rewrite.example.from || '(nothing)' }}</code> becomes
        <code>{{ rewrite.example.to || '(nothing)' }}</code></template
      >. Nothing changes until you edit there.
    </p>
    <MonacoDiff
      v-if="rewrite && rewriteDiff"
      :original="markdown"
      :modified="rewrite.text"
      original-label="Now"
      modified-label="As the visual editor writes it"
      height="24rem"
    />
    <template #footer>
      <Button :label="rewriteDiff ? 'Hide' : 'Show'" severity="secondary" text @click="rewriteDiff = !rewriteDiff" />
      <Button label="Stay" severity="secondary" outlined @click="stay" />
      <Button label="Switch anyway" @click="switchAnyway" />
    </template>
  </Dialog>

  <Dialog
    :visible="!!conflict"
    modal
    header="Saved in between"
    :style="{ width: 'min(64rem, 95vw)' }"
    @update:visible="(v: boolean) => !v && (conflict = null)"
  >
    <template v-if="conflict">
      <p>
        <strong>{{ conflict.by }}</strong> saved at {{ conflictTime }}, while you were editing. Your changes aren’t
        saved yet.
      </p>
      <SelectButton
        v-model="conflictView"
        :options="[
          { label: 'Their changes', value: 'theirs' },
          { label: 'My changes', value: 'mine' },
        ]"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Changes to show"
      />
      <MonacoDiff
        class="md-dialog-diff"
        :original="conflictBase"
        :modified="conflictView === 'theirs' ? conflict.theirs : markdown"
        original-label="When you started"
        :modified-label="conflictView === 'theirs' ? `${conflict.by}'s version` : 'Yours'"
        height="24rem"
      />
      <p class="muted">Saving yours anyway makes a new revision on top of theirs. Theirs stays in the history.</p>
    </template>
    <template #footer>
      <Button label="Keep editing" severity="secondary" outlined @click="conflict = null" />
      <Button label="Save mine anyway" severity="warn" @click="saveMineAnyway" />
    </template>
  </Dialog>

  <Dialog
    :visible="!!credentials"
    modal
    header="This looks like a credential"
    :style="{ width: '34rem' }"
    @update:visible="(v: boolean) => !v && (credentials = null)"
  >
    <p>{{ credentialsText }} PNMT is not a password manager.</p>
    <p class="muted">
      Put passwords, keys and codes into the password manager, and link to its entry.<template v-if="canMarkSensitive">
        A sensitive page is never sent to an AI or shown to MCP.</template
      >
    </p>
    <template #footer>
      <Button label="Edit" severity="secondary" outlined @click="answerCredentials('edit')" />
      <Button label="Save anyway" severity="secondary" @click="answerCredentials('save')" />
      <Button
        v-if="canMarkSensitive"
        label="Mark as sensitive and save"
        icon="pi pi-lock"
        @click="answerCredentials('sensitive')"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.md-mode {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.md-notice :deep(.p-message-text) {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.md-progress {
  font-size: 0.9286rem;
  padding: 0 var(--pnmt-space-1);
}

.md-surface {
  height: 100%;
  min-height: 0;
}

.md-plain {
  display: flex;
  height: 100%;
}

.md-textarea {
  flex: 1 1 0;
  min-width: 0;
  height: 100%;
  padding: var(--pnmt-space-3) var(--pnmt-space-4);
  border: none;
  outline: none;
  resize: none;
  background: var(--pnmt-panel);
  color: var(--pnmt-text);
  font-family: var(--pnmt-mono);
  font-size: 0.9286rem;
  line-height: 1.55;
  tab-size: 4;
}

.md-textarea:focus-visible {
  box-shadow: inset 0 0 0 2px var(--p-primary-color);
}

.md-starting {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.md-starting-note {
  margin: 0;
  padding: var(--pnmt-space-1) var(--pnmt-space-4);
  font-size: 0.9286rem;
}

.md-starting .md-textarea {
  flex: 1 1 auto;
  height: auto;
  color: var(--pnmt-muted);
}

.md-plain-preview {
  flex: 1 1 0;
  min-width: 0;
  height: 100%;
  overflow: auto;
  padding: var(--pnmt-space-4) var(--pnmt-space-6);
  border-left: 1px solid var(--pnmt-border);
}

.preview-only .md-plain-preview {
  border-left: none;
}

/* The inline layout: a framed box in a page (the documentation's example). */
.md-inline {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  overflow: hidden;
}

.md-inline .md-surface {
  height: var(--md-inline-height);
}

.md-inline-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
}

.md-inline-bar .spacer {
  flex: 1;
}

.md-inline-notices {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) var(--pnmt-space-2) 0;
}

.md-dialog-diff {
  margin-top: var(--pnmt-space-2);
}
</style>
