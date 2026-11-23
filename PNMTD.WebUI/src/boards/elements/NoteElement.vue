<script setup lang="ts">
import { computed, defineComponent } from 'vue'
import { noteNodes } from '../boardMarkdown'
import type { BoardElement, BoardElementData } from '../types'

/** A text written on the board (§7.1): Markdown without images or HTML. */
const props = defineProps<{ element: BoardElement; data: BoardElementData }>()
const text = computed(() => props.data.note ?? props.element.settings.text ?? '')
const Rendered = defineComponent({
  props: { markdown: { type: String, required: true } },
  setup: (p) => () => noteNodes(p.markdown),
})
</script>

<template>
  <div class="board-note" :data-large="element.settings.large" :data-center="element.settings.center">
    <Rendered v-if="text" :markdown="text" />
    <p v-else class="board-muted">No text yet.</p>
  </div>
</template>
