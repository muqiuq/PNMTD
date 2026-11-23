<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import BoardCanvas from '@/boards/BoardCanvas.vue'
import type { BoardLayout, BoardView } from '@/boards/types'

/**
 * The preview (docs/boards-plan.md §10.4): the board drawn at a chosen screen's size and scaled down to the space beside the settings,
 * so that fit to window and the text size look as on the wall. "This window" is the space itself. Without fit, a page of that width
 * that scrolls. A click on an element selects it.
 */
const props = defineProps<{
  layout: BoardLayout
  view: BoardView | null
  name: string
  size: string
  selected: string | null
}>()
const emit = defineEmits<{ select: [id: string] }>()

const frame = ref<HTMLElement | null>(null)
const available = ref({ width: 800, height: 450 })
let observer: ResizeObserver | null = null
onMounted(() => {
  if (!frame.value) return
  const measure = () => {
    if (frame.value) available.value = { width: frame.value.clientWidth, height: frame.value.clientHeight }
  }
  measure()
  observer = new ResizeObserver(measure)
  observer.observe(frame.value)
})
onBeforeUnmount(() => observer?.disconnect())

const screen = computed(() => {
  if (props.size === 'window') return { width: available.value.width, height: available.value.height }
  const [w, h] = props.size.split('x').map(Number)
  return { width: w || 1920, height: h || 1080 }
})
const scale = computed(() => {
  const s = screen.value
  const byWidth = available.value.width / s.width
  return props.layout.look.fit ? Math.min(byWidth, available.value.height / s.height) : byWidth
})
const style = computed(() => ({
  width: `${screen.value.width}px`,
  height: props.layout.look.fit ? `${screen.value.height}px` : 'auto',
  minHeight: props.layout.look.fit ? undefined : `${screen.value.height}px`,
  transform: `scale(${scale.value})`,
}))
</script>

<template>
  <div ref="frame" class="preview-frame" :class="{ scroll: !layout.look.fit }">
    <div class="preview-screen" :style="style" data-test="board-preview">
      <BoardCanvas
        :layout="layout"
        :view="view"
        :height="screen.height"
        :width="screen.width"
        :name="name"
        selectable
        :selected="selected"
        @select="(id) => emit('select', id)"
      />
    </div>
  </div>
</template>

<style scoped>
.preview-frame {
  position: relative;
  flex: 1 1 auto;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  background: repeating-conic-gradient(var(--pnmt-bg) 0% 25%, var(--pnmt-panel) 0% 50%) 50% / 20px 20px;
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
}

.preview-frame.scroll {
  overflow-y: auto;
}

.preview-screen {
  transform-origin: top left;
  box-shadow: 0 4px 24px rgb(0 0 0 / 25%);
}
</style>
