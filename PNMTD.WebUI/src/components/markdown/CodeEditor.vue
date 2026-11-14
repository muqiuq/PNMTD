<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useMedia } from '@/composables/useMedia'
import { usePreferences } from '@/stores/preferences'
import { fileMarkdown } from './files'
import MarkdownView from './MarkdownView.vue'
import { defineTheme, fontFamily, monaco } from './monacoSetup'
import type { MarkdownFile } from './types'
import type { UploadHandler } from './useUpload'

/**
 * The code editor: Monaco (docs/locations-knowledge-plan.md §5.3), its own chunk, loaded when the
 * Markdown mode is first used. Word wrap on, the minimap off but a toggle away, JetBrains Mono, the
 * theme following PNMT's. A pasted or dropped file is uploaded and inserted at the cursor (or where it
 * was dropped). `preview` shows the page as MarkdownView renders it beside the code, 300 ms after
 * typing stops, scrolling with it; on a narrow screen the preview takes the editor's place.
 */
const props = withDefaults(
  defineProps<{
    modelValue: string
    upload: UploadHandler | null
    preview?: boolean
    /** For the preview's attachment chips. */
    files?: readonly MarkdownFile[] | null
    placeholder?: string
  }>(),
  { preview: false, files: null, placeholder: '' },
)
const emit = defineEmits<{
  'update:modelValue': [markdown: string]
  ready: []
  failed: [error: unknown]
  notice: [message: string]
}>()

const preferences = usePreferences()
const narrow = useMedia('(max-width: 959px)')
const host = ref<HTMLElement | null>(null)
const previewPane = ref<HTMLElement | null>(null)
const editor = shallowRef<monaco.editor.IStandaloneCodeEditor | null>(null)
const minimap = ref(false)
const previewSource = ref(props.modelValue)
let last = props.modelValue
let previewTimer: ReturnType<typeof setTimeout> | undefined
let placeholderCount = 0

const split = computed(() => props.preview && !narrow.value)
const previewOnly = computed(() => props.preview && narrow.value)

function insertAt(position: monaco.IPosition | null, text: string) {
  const e = editor.value
  if (!e) {
    return
  }
  const selection = e.getSelection()
  const range = position
    ? new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column)
    : (selection ?? new monaco.Range(1, 1, 1, 1))
  e.executeEdits('pnmt', [{ range, text, forceMoveMarkers: true }])
  e.pushUndoStop()
  e.focus()
}

function replacePlaceholder(placeholder: string, text: string) {
  const e = editor.value
  const model = e?.getModel()
  if (!e || !model) {
    return
  }
  const [match] = model.findMatches(placeholder, false, false, true, null, false)
  if (match) {
    e.executeEdits('pnmt', [{ range: match.range, text, forceMoveMarkers: true }])
    e.pushUndoStop()
  }
}

/** Uploads files and puts each where the cursor (or the drop) was, as `![…](…)` or `[…](…)`. */
function insertFiles(files: File[], position: monaco.IPosition | null) {
  if (!props.upload) {
    emit('notice', 'Files can’t be added here.')
    return
  }
  const upload = props.upload
  for (const file of files) {
    const placeholder = `[Uploading ${file.name.replace(/[[\]]/g, '')}… #${++placeholderCount}]()`
    insertAt(position, placeholder)
    position = null
    void upload(file, 'paste').then((uploaded) =>
      replacePlaceholder(placeholder, uploaded ? fileMarkdown(uploaded) : ''),
    )
  }
}

function onPaste(event: ClipboardEvent) {
  const files = Array.from(event.clipboardData?.files ?? [])
  // Text with a picture of it (Office): the text is what's meant.
  if (!files.length || event.clipboardData?.getData('text/plain')) {
    return
  }
  event.preventDefault()
  event.stopPropagation()
  insertFiles(files, null)
}

function onDragOver(event: DragEvent) {
  if (event.dataTransfer?.types.includes('Files')) {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }
}

function onDrop(event: DragEvent) {
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (!files.length) {
    return
  }
  event.preventDefault()
  event.stopPropagation()
  const target = editor.value?.getTargetAtClientPoint(event.clientX, event.clientY)
  insertFiles(files, target?.position ?? null)
}

function schedulePreview() {
  clearTimeout(previewTimer)
  previewTimer = setTimeout(() => {
    previewSource.value = last
  }, 300)
}

/** The preview follows the code: the block the editor's first visible line belongs to, at the top. */
function syncPreview() {
  const e = editor.value
  const pane = previewPane.value
  if (!e || !pane || !split.value) {
    return
  }
  const line = e.getVisibleRanges()[0]?.startLineNumber ?? 1
  const blocks = Array.from(pane.querySelectorAll<HTMLElement>('[data-line]'))
  let target: HTMLElement | null = null
  for (const block of blocks) {
    if (Number(block.dataset.line) <= line) {
      target = block
    } else {
      break
    }
  }
  pane.scrollTop = target ? target.offsetTop - pane.offsetTop : 0
}

onMounted(() => {
  try {
    const created = monaco.editor.create(host.value!, {
      value: props.modelValue,
      language: 'markdown',
      theme: defineTheme(preferences.isDark()),
      ariaLabel: 'Markdown editor',
      automaticLayout: true,
      wordWrap: 'on',
      wrappingIndent: 'same',
      minimap: { enabled: false },
      fontFamily,
      fontSize: 13,
      lineHeight: 20,
      fontLigatures: false,
      lineNumbers: 'on',
      folding: true,
      showFoldingControls: 'mouseover',
      stickyScroll: { enabled: true },
      scrollBeyondLastLine: false,
      renderWhitespace: 'selection',
      bracketPairColorization: { enabled: true },
      quickSuggestions: false,
      wordBasedSuggestions: 'off',
      suggestOnTriggerCharacters: false,
      unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: true },
      padding: { top: 8, bottom: 8 },
      placeholder: props.placeholder,
      fixedOverflowWidgets: true,
      dragAndDrop: true,
    })
    created.onDidChangeModelContent(() => {
      last = created.getValue()
      if (last !== props.modelValue) {
        emit('update:modelValue', last)
      }
      schedulePreview()
    })
    created.onDidScrollChange(() => syncPreview())
    created.addAction({
      id: 'pnmt.toggleMinimap',
      label: 'Toggle Minimap',
      run: () => {
        minimap.value = !minimap.value
      },
    })
    host.value!.addEventListener('paste', onPaste, true)
    host.value!.addEventListener('dragover', onDragOver, true)
    host.value!.addEventListener('drop', onDrop, true)
    editor.value = created
    emit('ready')
  } catch (error) {
    emit('failed', error)
  }
})

watch(minimap, (enabled) => editor.value?.updateOptions({ minimap: { enabled } }))
watch(
  () => preferences.isDark(),
  (dark) => monaco.editor.setTheme(defineTheme(dark)),
)
watch(
  () => props.modelValue,
  (value) => {
    const e = editor.value
    const model = e?.getModel()
    if (e && model && value !== last) {
      // From outside (a restored draft): one edit, so that undo brings the text back.
      last = value
      e.executeEdits('pnmt', [{ range: model.getFullModelRange(), text: value }])
      e.pushUndoStop()
      schedulePreview()
    }
  },
)
watch(split, (on) => {
  if (on) {
    previewSource.value = last
    void nextTick(syncPreview)
  }
})

onBeforeUnmount(() => {
  clearTimeout(previewTimer)
  host.value?.removeEventListener('paste', onPaste, true)
  host.value?.removeEventListener('dragover', onDragOver, true)
  host.value?.removeEventListener('drop', onDrop, true)
  editor.value?.getModel()?.dispose()
  editor.value?.dispose()
  editor.value = null
})

defineExpose({
  focus: () => editor.value?.focus(),
  /** Inserts Markdown at the cursor. */
  insertMarkdown: (markdown: string) => insertAt(null, markdown),
  /** Puts the cursor on a line and shows it (the credentials warning's *Edit*). */
  revealLine: (line: number) => {
    editor.value?.setPosition({ lineNumber: line, column: 1 })
    editor.value?.revealLineInCenter(line)
    editor.value?.focus()
  },
  toggleMinimap: () => (minimap.value = !minimap.value),
  minimap,
})
</script>

<template>
  <div class="code-editor" :class="{ split, 'preview-only': previewOnly }">
    <div v-show="!previewOnly" ref="host" class="monaco-host" />
    <section v-if="preview" ref="previewPane" class="preview" aria-label="Preview">
      <MarkdownView :source="previewSource" :files="files" />
    </section>
  </div>
</template>

<style scoped>
.code-editor {
  display: flex;
  height: 100%;
  min-height: 0;
}

.monaco-host {
  flex: 1 1 0;
  min-width: 0;
  height: 100%;
}

.preview {
  flex: 1 1 0;
  min-width: 0;
  height: 100%;
  overflow: auto;
  padding: var(--pnmt-space-4) var(--pnmt-space-6);
  border-left: 1px solid var(--pnmt-border);
  background: var(--pnmt-panel);
}

.preview-only .preview {
  border-left: none;
}
</style>
