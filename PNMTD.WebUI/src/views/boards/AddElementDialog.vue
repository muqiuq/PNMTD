<script setup lang="ts">
import Dialog from 'primevue/dialog'
import { computed } from 'vue'
import type { BoardCatalog, BoardCatalogType } from '@/api/boards'
import type { BoardElementType } from '@/boards/types'

/**
 * The catalog (docs/boards-plan.md §7.1, §10.3): every element type by its group, with its line. A type the editor may not add, or whose
 * integration isn't set up, is greyed with the reason.
 */
const props = defineProps<{ catalog: BoardCatalog | null; column: number }>()
const visible = defineModel<boolean>('visible', { required: true })
const emit = defineEmits<{ add: [type: BoardElementType] }>()
const groups = computed(() => {
  const by = new Map<string, BoardCatalogType[]>()
  for (const t of props.catalog?.types ?? []) by.set(t.group, [...(by.get(t.group) ?? []), t])
  return [...by.entries()]
})
function choose(t: BoardCatalogType) {
  if (!t.available) return
  emit('add', t.type)
  visible.value = false
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Add to column ${column + 1}`" modal :style="{ width: '46rem' }">
    <div v-for="[group, types] in groups" :key="group" class="group">
      <h3>{{ group }}</h3>
      <div class="types">
        <button
          v-for="t in types"
          :key="t.type"
          type="button"
          class="type"
          :disabled="!t.available"
          :data-test="`add-${t.type}`"
          @click="choose(t)"
        >
          <i :class="t.icon" aria-hidden="true" />
          <span class="name">{{ t.name }}</span>
          <span class="line">{{ t.available ? t.line : t.reason }}</span>
        </button>
      </div>
    </div>
  </Dialog>
</template>

<style scoped>
.group h3 {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
  font-size: 0.9rem;
  color: var(--pnmt-muted);
}

.types {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(13rem, 1fr));
  gap: var(--pnmt-space-2);
}

.type {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 2px var(--pnmt-space-2);
  align-items: start;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  background: var(--pnmt-panel);
  color: var(--pnmt-text);
  text-align: left;
  font: inherit;
  cursor: pointer;
}

.type:hover:not(:disabled) {
  border-color: var(--p-primary-color);
}

.type:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.type i {
  grid-row: span 2;
  margin-top: 3px;
  color: var(--p-primary-color);
}

.name {
  font-weight: 600;
}

.line {
  font-size: 0.82rem;
  color: var(--pnmt-muted);
}
</style>
