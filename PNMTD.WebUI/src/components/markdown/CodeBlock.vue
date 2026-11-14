<script setup lang="ts">
import Button from 'primevue/button'
import { useCopy } from '@/composables/useCopy'

/** A highlighted code block with a copy button (docs/locations-knowledge-plan.md §5.6). */
const props = defineProps<{ source: string; language?: string }>()
const copy = useCopy()
</script>

<template>
  <div class="md-code" :data-language="language || undefined">
    <Button
      v-tooltip.left="'Copy'"
      class="copy"
      icon="pi pi-copy"
      size="small"
      text
      rounded
      severity="secondary"
      :aria-label="language ? `Copy the ${language} code` : 'Copy the code'"
      @click="copy(props.source.replace(/\n$/, ''), language ? `The ${language} code` : 'The code')"
    />
    <slot />
  </div>
</template>

<style scoped>
.md-code {
  position: relative;
}

.copy {
  position: absolute;
  top: 2px;
  right: 2px;
  opacity: 0.5;
}

.md-code:hover .copy,
.copy:focus-visible {
  opacity: 1;
}

@media print {
  .copy {
    display: none;
  }
}
</style>
