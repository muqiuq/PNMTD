<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import DiffView from '@/components/DiffView.vue'
import { useMedia } from '@/composables/useMedia'
import { usePreferences } from '@/stores/preferences'
import { lineDiff } from './lineDiff'

/**
 * Two Markdown texts compared in Monaco's diff editor (docs/locations-knowledge-plan.md §6.3): side by
 * side, inline on narrow screens; read-only. Where Monaco doesn't run (phones, or its chunk didn't
 * load or start), DiffView shows the same diff as an HTML table (§5.4).
 *
 * The history (KbHistoryView) and the editor's dialogs (the switch's rewrite, a conflict) use it.
 */
const props = withDefaults(
  defineProps<{
    original: string
    modified: string
    /** For screen readers: what the two sides are. */
    originalLabel?: string
    modifiedLabel?: string
    /** The height of the diff; it scrolls inside. */
    height?: string
  }>(),
  { originalLabel: 'Before', modifiedLabel: 'After', height: '28rem' },
)

const preferences = usePreferences()
const phone = useMedia('(max-width: 599px), (pointer: coarse) and (max-width: 959px)')
const narrow = useMedia('(max-width: 959px)')
const host = ref<HTMLElement | null>(null)
const failed = ref(false)
type Monaco = typeof import('./monacoSetup')
const setup = shallowRef<Monaco | null>(null)
let diff: import('monaco-editor/editor/editor.api').editor.IStandaloneDiffEditor | null = null

const table = computed(() => phone.value || failed.value)
const fallback = computed(() => (table.value ? lineDiff(props.original, props.modified) : null))

function models(m: Monaco) {
  return {
    original: m.monaco.editor.createModel(props.original, 'markdown'),
    modified: m.monaco.editor.createModel(props.modified, 'markdown'),
  }
}

function dispose() {
  const model = diff?.getModel()
  diff?.dispose()
  model?.original.dispose()
  model?.modified.dispose()
  diff = null
}

async function create() {
  if (table.value || !host.value) {
    return
  }
  try {
    const m = (setup.value ??= await import('./monacoSetup'))
    dispose()
    diff = m.monaco.editor.createDiffEditor(host.value, {
      theme: m.defineTheme(preferences.isDark()),
      readOnly: true,
      originalEditable: false,
      automaticLayout: true,
      renderSideBySide: !narrow.value,
      useInlineViewWhenSpaceIsLimited: true,
      wordWrap: 'on',
      diffWordWrap: 'on',
      minimap: { enabled: false },
      fontFamily: m.fontFamily,
      fontSize: 13,
      lineHeight: 20,
      scrollBeyondLastLine: false,
      renderOverviewRuler: false,
      originalAriaLabel: props.originalLabel,
      modifiedAriaLabel: props.modifiedLabel,
      hideUnchangedRegions: { enabled: true, contextLineCount: 3, minimumLineCount: 5 },
    })
    diff.setModel(models(m))
  } catch {
    failed.value = true
  }
}

onMounted(create)
onBeforeUnmount(dispose)

watch(
  () => [props.original, props.modified],
  () => {
    const m = setup.value
    if (diff && m) {
      const previous = diff.getModel()
      diff.setModel(models(m))
      previous?.original.dispose()
      previous?.modified.dispose()
    }
  },
)
watch(narrow, (n) => diff?.updateOptions({ renderSideBySide: !n }))
watch(
  () => preferences.isDark(),
  (dark) => setup.value?.monaco.editor.setTheme(setup.value.defineTheme(dark)),
)
watch(table, (t) => (t ? dispose() : void create()), { flush: 'post' })
</script>

<template>
  <DiffView v-if="fallback" :diff="fallback" class="monaco-diff-fallback" />
  <div v-else ref="host" class="monaco-diff" :style="{ height }" role="region" aria-label="Changes" />
</template>

<style scoped>
.monaco-diff {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}
</style>
