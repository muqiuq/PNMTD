<script setup lang="ts">
import { computed } from 'vue'
import { colourName, isColour } from './colours'

/**
 * A responsibility's colour as a dot (docs/responsibilities-extensions-plan.md §4.1), with an outline in the border colour so a light
 * or a dark colour still has an edge; an empty ring without one. Never the only sign: the name always stands next to it.
 */
const props = withDefaults(defineProps<{ colour?: string | null; size?: string }>(), { colour: null, size: '0.7rem' })
const set = computed(() => isColour(props.colour))
</script>

<template>
  <span
    class="colour-dot"
    :class="{ none: !set }"
    :style="{ width: size, height: size, background: set ? colour! : undefined }"
    :title="colourName(colour)"
    aria-hidden="true"
    data-test="colour-dot"
  />
</template>

<style scoped>
.colour-dot {
  display: inline-block;
  flex: none;
  border-radius: 50%;
  border: 1px solid var(--pnmt-border);
  vertical-align: middle;
}

.colour-dot.none {
  background: transparent;
  border-color: var(--pnmt-muted);
  border-style: dashed;
}
</style>
