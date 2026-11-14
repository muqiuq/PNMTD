<script setup lang="ts">
import { computed } from 'vue'
import { chipStyle, isColour } from './plan'

/** A short code on its colour (docs/cookie-plan.md §8.1, §8.2): a person's (ADM) or a type's (HOME), readable on it. */
const props = defineProps<{ code: string | null | undefined; colour?: string | null; title?: string }>()
const style = computed(() => chipStyle(props.colour))
</script>

<template>
  <span class="code-chip mono" :class="{ plain: !isColour(colour) }" :style="style" :title="title">{{
    code || '–'
  }}</span>
</template>

<style scoped>
.code-chip {
  display: inline-block;
  min-width: 2.6rem;
  padding: 0 0.35rem;
  border-radius: 4px;
  font-size: 0.857rem;
  font-weight: 600;
  line-height: 1.5;
  text-align: center;
  border: 1px solid color-mix(in srgb, currentColor 18%, transparent);
}

.plain {
  border-color: var(--pnmt-border);
}
</style>
