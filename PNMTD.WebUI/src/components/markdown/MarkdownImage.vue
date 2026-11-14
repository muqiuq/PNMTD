<script setup lang="ts">
import { ref } from 'vue'

/** An image of PNMT's store in a page (§5.6): lazily loaded; *Image removed* when the file is gone. */
defineProps<{ src: string; alt?: string; title?: string }>()
const failed = ref(false)
</script>

<template>
  <span v-if="failed" class="md-image-removed" :title="src">
    <i class="pi pi-image" aria-hidden="true" /> Image removed<template v-if="alt">: {{ alt }}</template>
  </span>
  <img v-else :src="src" :alt="alt ?? ''" :title="title" loading="lazy" decoding="async" @error="failed = true" />
</template>

<style scoped>
.md-image-removed {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border: 1px dashed var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  color: var(--pnmt-muted);
}
</style>
