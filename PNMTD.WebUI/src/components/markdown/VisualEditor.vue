<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { usePreferences } from '@/stores/preferences'
import { createVisualEditor, type VisualEditorHandle } from './crepeSetup'
import type { PageLink } from './types'
import type { UploadHandler } from './useUpload'
import './crepeTheme.css'

/**
 * The visual editor: Crepe (docs/locations-knowledge-plan.md §5.2), set up by crepeSetup.ts. It is
 * its own chunk, loaded when the visual mode is first used.
 *
 * `ready` gives the Markdown Crepe writes for the text it was opened with; MarkdownEditor compares it
 * before switching (§5.4). Until the document is edited, v-model keeps the text as it came: switching
 * back and forth without editing changes nothing, and undoing every edit gives the original back.
 * `failed` means Crepe didn't start: the textarea takes over.
 */
const props = defineProps<{
  modelValue: string
  upload: UploadHandler | null
  pickPage?: (() => Promise<PageLink | null>) | null
  placeholder?: string
}>()
const emit = defineEmits<{
  'update:modelValue': [markdown: string]
  /** Edits not yet in v-model (they follow within 150 ms, or at once with flush()). */
  pending: [pending: boolean]
  ready: [markdown: string]
  failed: [error: unknown]
  notice: [message: string]
}>()

const preferences = usePreferences()
const root = ref<HTMLElement | null>(null)
let handle: VisualEditorHandle | null = null
let destroyed = false
// The text it was opened with, and what Crepe writes for it: an update back to the latter is no change.
let original = props.modelValue
let baseline: string | null = null
let last = props.modelValue
// Edits not yet written as Markdown: writing it is work for a long page, so it waits for a pause in
// typing, or until someone asks (flush).
let pending = false
let pause: ReturnType<typeof setTimeout> | undefined

/** Writes the document as Markdown now and updates v-model; returns the text. */
function flush(): string {
  clearTimeout(pause)
  if (handle && baseline !== null && pending) {
    const markdown = handle.markdown()
    last = markdown === baseline ? original : markdown
    if (last !== props.modelValue) {
      emit('update:modelValue', last)
    }
  }
  if (pending) {
    pending = false
    emit('pending', false)
  }
  return last
}

function onDocChange() {
  if (baseline === null) {
    return
  }
  if (!pending) {
    pending = true
    emit('pending', true)
  }
  clearTimeout(pause)
  pause = setTimeout(flush, 150)
}

/** A `mermaid` block's diagram under its code (§5.2, §5.8). */
function renderDiagram(source: string, apply: (preview: HTMLElement | null) => void) {
  void import('./mermaid')
    .then(async ({ drawDiagram, readTheme }) => {
      const result = await drawDiagram(source, readTheme(preferences.isDark()))
      const box = document.createElement('div')
      box.className = result.ok ? 'pnmt-crepe-diagram' : 'pnmt-crepe-diagram failed'
      if (result.ok) {
        // Sanitised by mermaid.ts (DOMPurify's SVG profile).
        box.innerHTML = result.svg
      } else {
        const title = document.createElement('strong')
        title.textContent = result.tooLarge ? 'Too large to draw' : 'The diagram can’t be drawn yet'
        const message = document.createElement('pre')
        message.textContent = result.message
        box.append(title, message)
      }
      apply(box)
    })
    .catch(() => apply(null))
}

onMounted(async () => {
  try {
    const created = await createVisualEditor({
      root: root.value!,
      markdown: props.modelValue,
      placeholder: props.placeholder ?? 'Write, or type / for blocks…',
      upload: props.upload,
      pickPage: props.pickPage ?? null,
      renderDiagram,
      onDocChange,
      notice: (message) => emit('notice', message),
    })
    if (destroyed) {
      await created.destroy()
      return
    }
    handle = created
    baseline = created.markdown()
    emit('ready', baseline)
  } catch (error) {
    emit('failed', error)
  }
})

// A text set from outside (a restored draft, *Attach the original instead*): the document follows.
watch(
  () => props.modelValue,
  (value) => {
    if (handle && value !== last) {
      original = value
      last = value
      handle.setMarkdown(value)
      baseline = handle.markdown()
    }
  },
)

onBeforeUnmount(() => {
  flush()
  destroyed = true
  void handle?.destroy()
  handle = null
})

defineExpose({
  /** The Markdown Crepe writes for the document now. */
  markdown: () => handle?.markdown() ?? props.modelValue,
  /** Brings v-model up to date with the document at once, and returns the text (before a save or a switch). */
  flush,
  focus: () => handle?.focus(),
  /** Inserts Markdown at the cursor: an uploaded file, a link. */
  insertMarkdown: (markdown: string) => handle?.insertMarkdown(markdown),
})
</script>

<template>
  <div ref="root" class="pnmt-md-editor pnmt-visual-editor" />
</template>
