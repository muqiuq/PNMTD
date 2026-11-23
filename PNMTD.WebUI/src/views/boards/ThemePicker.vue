<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { ref, watch } from 'vue'
import { themes } from '@/boards/themes/registry'
import '@/boards/board.css'
import '@/boards/themes/themes.css'

/**
 * The theme picker (docs/boards-plan.md §9.1), as panag's: a tile per theme in rows of five, each preview drawn by its own theme with no
 * text (a strip at the top, a panel with two lines and an accent block), its name and line below. A click shows the theme on the board's
 * preview at once; Use theme keeps it in the draft.
 */
const visible = defineModel<boolean>('visible', { required: true })
const theme = defineModel<string>('theme', { required: true })
const emit = defineEmits<{ try: [id: string] }>()
const picked = ref(theme.value)
const before = ref(theme.value)
watch(visible, (open) => {
  if (open) {
    picked.value = theme.value
    before.value = theme.value
  }
})
function pick(id: string) {
  picked.value = id
  emit('try', id)
}
function use() {
  theme.value = picked.value
  visible.value = false
}
function cancel() {
  emit('try', before.value)
  visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Theme"
    modal
    :style="{ width: 'min(72rem, 96vw)' }"
    @hide="picked !== theme && emit('try', theme)"
  >
    <div class="tiles" role="radiogroup" aria-label="Theme">
      <button
        v-for="t in themes"
        :key="t.id"
        type="button"
        role="radio"
        class="tile"
        :aria-checked="picked === t.id"
        :data-test="`theme-${t.id}`"
        @click="pick(t.id)"
      >
        <span class="board board-theme-preview" :data-board-theme="t.id" aria-hidden="true">
          <span class="board-header board-theme-strip" />
          <span class="board-element board-theme-card">
            <span class="board-theme-line" />
            <span class="board-theme-line board-theme-highlight" />
            <span class="board-theme-accent" />
          </span>
        </span>
        <span class="name">{{ t.name }}</span>
        <span class="line">{{ t.line }}</span>
      </button>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="cancel" />
      <Button label="Use theme" data-test="use-theme" @click="use" />
    </template>
  </Dialog>
</template>

<style scoped>
.tiles {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 0.75rem;
}

@media (max-width: 900px) {
  .tiles {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.tile {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 0.5rem;
  border: 1px solid var(--pnmt-border);
  border-radius: 8px;
  background: var(--pnmt-panel);
  color: var(--pnmt-text);
  text-align: left;
  font: inherit;
  cursor: pointer;
}

.tile[aria-checked='true'] {
  outline: 3px solid var(--p-primary-color);
  outline-offset: 2px;
}

.tile:focus-visible {
  outline: 3px dashed var(--p-primary-color);
  outline-offset: 2px;
}

.name {
  margin-top: 0.4rem;
  font-weight: 700;
}

.line {
  font-size: 0.82rem;
  color: var(--pnmt-muted);
}
</style>
