<script setup lang="ts">
import Button from 'primevue/button'
import type { MenuItem } from 'primevue/menuitem'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import MoreMenu from '@/components/MoreMenu.vue'
import { useCopy } from '@/composables/useCopy'
import { usePreferences } from '@/stores/preferences'
import type { DiagramResult } from './mermaid'

/**
 * A ` ```mermaid ` block drawn as a diagram (docs/locations-knowledge-plan.md §5.8). It draws when it
 * scrolls into view, again when the theme changes; `auto` false waits for *Show diagram* (after the
 * first 50 of a page). A syntax error shows Mermaid's message, then the source; the page never breaks.
 * The menu: *Show source*, *Copy source*, *Download SVG*. accTitle and accDescr label the figure.
 */
const props = withDefaults(defineProps<{ source: string; auto?: boolean }>(), { auto: true })

const preferences = usePreferences()
const copy = useCopy()
const root = ref<HTMLElement | null>(null)
const result = ref<DiagramResult | null>(null)
const drawing = ref(false)
const wanted = ref(props.auto)
const visible = ref(false)
const sourceShown = ref(false)
let observer: IntersectionObserver | null = null
let generation = 0

async function draw() {
  const mine = ++generation
  drawing.value = true
  try {
    const { drawDiagram, readTheme } = await import('./mermaid')
    const next = await drawDiagram(props.source, readTheme(preferences.isDark()))
    if (mine === generation) {
      result.value = next
    }
  } catch (e) {
    // The chunk didn't load (offline, blocked): the source stands in for the diagram.
    if (mine === generation) {
      result.value = { ok: false, tooLarge: false, message: e instanceof Error ? e.message : String(e) }
    }
  } finally {
    if (mine === generation) {
      drawing.value = false
    }
  }
}

function maybeDraw() {
  if (wanted.value && visible.value) {
    void draw()
  }
}

onMounted(() => {
  if (typeof IntersectionObserver === 'undefined' || !root.value) {
    visible.value = true
    maybeDraw()
    return
  }
  observer = new IntersectionObserver(
    (entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        visible.value = true
        observer?.disconnect()
        observer = null
        maybeDraw()
      }
    },
    { rootMargin: '400px 0px' },
  )
  observer.observe(root.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  generation++
})

watch(
  () => [props.source, preferences.isDark()],
  () => {
    if (result.value || drawing.value) {
      maybeDraw()
    }
  },
)

function show() {
  wanted.value = true
  visible.value = true
  maybeDraw()
}

const drawn = computed(() => (result.value?.ok ? result.value : null))
const failed = computed(() => (result.value && !result.value.ok ? result.value : null))
const label = computed(() => drawn.value?.title ?? 'Diagram')

function downloadSvg() {
  if (!drawn.value) {
    return
  }
  const blob = new Blob([`<?xml version="1.0" encoding="UTF-8"?>\n${drawn.value.svg}`], { type: 'image/svg+xml' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `${(drawn.value.title ?? 'diagram').replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '') || 'diagram'}.svg`
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

const items = computed<MenuItem[]>(() => [
  {
    label: sourceShown.value ? 'Hide source' : 'Show source',
    icon: 'pi pi-code',
    command: () => (sourceShown.value = !sourceShown.value),
  },
  { label: 'Copy source', icon: 'pi pi-copy', command: () => copy(props.source, 'The diagram’s source') },
  { label: 'Download SVG', icon: 'pi pi-download', disabled: !drawn.value, command: downloadSvg },
])
</script>

<template>
  <figure
    ref="root"
    class="md-diagram"
    :class="{ failed: !!failed }"
    :aria-label="label"
    :aria-busy="drawing || undefined"
  >
    <div class="menu" data-md-skip><MoreMenu :items="items" label="Diagram actions" /></div>
    <template v-if="drawn">
      <!-- Sanitised by mermaid.ts (DOMPurify's SVG profile) before it gets here. -->
      <!-- eslint-disable-next-line vue/no-v-html -->
      <div class="svg" role="img" :aria-label="label" v-html="drawn.svg" />
      <figcaption v-if="drawn.description" class="sr-only">{{ drawn.description }}</figcaption>
    </template>
    <div v-else-if="failed" class="error" role="status">
      <strong>{{ failed.tooLarge ? 'Too large to draw' : 'The diagram couldn’t be drawn' }}</strong>
      <pre class="message">{{ failed.message }}</pre>
    </div>
    <div v-else-if="!wanted" class="waiting">
      <Button label="Show diagram" icon="pi pi-sitemap" size="small" severity="secondary" outlined @click="show" />
    </div>
    <div v-else class="waiting muted"><i class="pi pi-spin pi-spinner" aria-hidden="true" /> Drawing the diagram…</div>
    <pre v-if="sourceShown || failed || !wanted" class="source"><code>{{ source }}</code></pre>
  </figure>
</template>

<style scoped>
.md-diagram {
  position: relative;
  margin: 0 0 1em;
  padding: var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  overflow-x: auto;
}

.menu {
  position: absolute;
  top: 2px;
  right: 2px;
  opacity: 0.4;
  transition: opacity 0.15s;
}

.md-diagram:hover .menu,
.md-diagram:focus-within .menu {
  opacity: 1;
}

.svg {
  display: flex;
  justify-content: center;
}

.svg :deep(svg) {
  max-width: 100%;
  height: auto;
}

.waiting {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 3rem;
  justify-content: center;
}

.error strong {
  color: var(--pnmt-fail);
}

.message,
.source {
  margin: var(--pnmt-space-2) 0 0;
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border-radius: var(--p-border-radius-sm);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  white-space: pre-wrap;
  overflow-x: auto;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

@media print {
  .menu {
    display: none;
  }

  .md-diagram {
    break-inside: avoid;
    border: none;
  }
}
</style>
