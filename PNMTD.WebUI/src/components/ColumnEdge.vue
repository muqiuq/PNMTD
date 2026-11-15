<script setup lang="ts">
import type { useColumnWidths } from '@/composables/useColumnWidths'

/**
 * A header cell's right edge that resizes its column (useColumnWidths): drag it; a double-click gives the column its
 * default width back. The header cell needs `position: relative` or `sticky`. Hidden from assistive technology, as
 * PrimeVue's own column resizer: inside the header cell, a label of its own would become part of the column's name.
 */
defineProps<{ widths: ReturnType<typeof useColumnWidths>; column: string; label: string }>()
</script>

<template>
  <span
    class="column-edge"
    :class="{ active: widths.resizing.value === column }"
    aria-hidden="true"
    :data-column="column"
    :title="`${label}: drag to resize; double-click for the default width`"
    @pointerdown="widths.start($event, column)"
    @dblclick.stop="widths.reset(column)"
    @click.stop
    @mousedown.stop
  />
</template>

<style scoped>
.column-edge {
  position: absolute;
  top: 0;
  right: 0;
  bottom: 0;
  width: 7px;
  cursor: col-resize;
  touch-action: none;
  z-index: 1;
}

.column-edge::after {
  content: '';
  position: absolute;
  top: 20%;
  bottom: 20%;
  right: 2px;
  width: 2px;
  border-radius: 1px;
  background: transparent;
}

.column-edge:hover::after,
.column-edge.active::after {
  background: var(--p-primary-color);
}
</style>
