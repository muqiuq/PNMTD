<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import type { BoardCatalog } from '@/api/boards'
import { add, canAdd, find, move, newElement, remove, replace, setColumns, step } from '@/boards/layout'
import { typeNames } from '@/boards/names'
import type { BoardElement, BoardElementType, BoardLayout } from '@/boards/types'
import AddElementDialog from './AddElementDialog.vue'
import ElementSettings from './ElementSettings.vue'

/**
 * Layout (docs/boards-plan.md §10.3): the board's name, its columns with their widths, and each column's elements in order: dragged
 * within and between columns, or moved with ↑ ↓ from the keyboard. An element's ⚙ (or a click on it in the preview) opens its settings.
 */
const props = defineProps<{ catalog: BoardCatalog | null; manage: boolean; boardId?: string | null }>()
const layout = defineModel<BoardLayout>('layout', { required: true })
const name = defineModel<string>('name', { required: true })
const description = defineModel<string | null>('description', { required: true })
const selected = defineModel<string | null>('selected', { required: true })

const widths = [1, 2, 3, 4].map((w) => ({ value: w, label: w === 1 ? '1 share' : `${w} shares` }))
const adding = ref<number | null>(null)
const current = computed(() => (selected.value ? find(layout.value, selected.value) : null))
/** §7.2: an element the editor may not change, with why. */
function lockOf(element: BoardElement): string | null {
  const t = props.catalog?.types.find((x) => x.type === element.type)
  return t && !t.available ? (t.reason ?? 'You may not change it.') : null
}

function addElement(type: BoardElementType) {
  if (adding.value === null) return
  const element = newElement(type)
  layout.value = add(layout.value, adding.value, element)
  selected.value = element.id
}

// --- drag and drop -------------------------------------------------------------------------------------------------------
const dragging = ref<string | null>(null)
function dragStart(event: DragEvent, id: string) {
  dragging.value = id
  event.dataTransfer?.setData('text/plain', id)
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
}
function drop(event: DragEvent, column: number, index: number) {
  event.preventDefault()
  const id = dragging.value ?? event.dataTransfer?.getData('text/plain')
  dragging.value = null
  if (!id) return
  const at = find(layout.value, id)
  // Within its column, an element dropped below itself lands one higher once it is taken out.
  const target = at && at.column === column && at.index < index ? index - 1 : index
  layout.value = move(layout.value, id, column, target)
}
</script>

<template>
  <ElementSettings
    v-if="current"
    :element="current.element"
    :catalog="catalog"
    :columns="layout.columns.length"
    :column="current.column"
    :date-language="layout.look.dateLanguage ?? 'Browser'"
    :people="layout.look.people"
    :board-id="boardId"
    :locked="manage ? lockOf(current.element) : 'Boards: View only.'"
    @change="(e) => (layout = replace(layout, e))"
    @move="(c) => (layout = move(layout, current!.element.id, c, Number.MAX_SAFE_INTEGER))"
    @remove="((layout = remove(layout, current!.element.id)), (selected = null))"
    @close="selected = null"
  />
  <fieldset v-else :disabled="!manage" class="form layout">
    <div class="field">
      <label for="board-name">Name</label>
      <InputText id="board-name" v-model="name" />
    </div>
    <div class="field">
      <label for="board-description">Description</label>
      <InputText
        id="board-description"
        :model-value="description ?? ''"
        placeholder="For the list only"
        @update:model-value="(v) => (description = v?.trim() ? v : null)"
      />
    </div>
    <div class="field">
      <label>Columns</label>
      <SelectButton
        :model-value="layout.columns.length"
        :options="[1, 2, 3, 4]"
        :allow-empty="false"
        aria-label="Columns"
        @update:model-value="(n) => (layout = setColumns(layout, n))"
      />
      <small class="field-hint">Fewer columns move the elements of the others to the last one; nothing is lost.</small>
    </div>
    <section
      v-for="(column, c) in layout.columns"
      :key="c"
      class="column"
      @dragover.prevent
      @drop="drop($event, c, column.elements.length)"
    >
      <header>
        <strong>Column {{ c + 1 }}</strong>
        <Select
          :model-value="column.width"
          :options="widths"
          option-label="label"
          option-value="value"
          size="small"
          :aria-label="`Width of column ${c + 1}`"
          @update:model-value="
            (w) => (layout = { ...layout, columns: layout.columns.map((x, i) => (i === c ? { ...x, width: w } : x)) })
          "
        />
      </header>
      <ul class="elements">
        <li
          v-for="(e, i) in column.elements"
          :key="e.id"
          :draggable="manage"
          :class="{ dragging: dragging === e.id }"
          :data-test="`element-${e.type}`"
          @dragstart="dragStart($event, e.id)"
          @dragend="dragging = null"
          @dragover.prevent
          @drop.stop="drop($event, c, i)"
        >
          <i class="pi pi-bars handle" aria-hidden="true" />
          <span class="name" @click="selected = e.id">{{ e.title ?? typeNames[e.type] }}</span>
          <i v-if="lockOf(e)" class="pi pi-lock muted" :title="lockOf(e) ?? ''" />
          <Button
            icon="pi pi-arrow-up"
            text
            size="small"
            severity="secondary"
            :disabled="i === 0"
            :aria-label="`Move ${typeNames[e.type]} up`"
            @click="layout = step(layout, e.id, -1)"
          />
          <Button
            icon="pi pi-arrow-down"
            text
            size="small"
            severity="secondary"
            :disabled="i === column.elements.length - 1"
            :aria-label="`Move ${typeNames[e.type]} down`"
            @click="layout = step(layout, e.id, 1)"
          />
          <Button
            icon="pi pi-cog"
            text
            size="small"
            severity="secondary"
            :aria-label="`Settings of ${typeNames[e.type]}`"
            @click="selected = e.id"
          />
        </li>
        <li v-if="!column.elements.length" class="empty muted">Empty: drop an element here, or add one.</li>
      </ul>
      <Button
        label="Add element"
        icon="pi pi-plus"
        text
        size="small"
        :disabled="!canAdd(layout, c)"
        :data-test="`add-element-${c}`"
        @click="adding = c"
      />
    </section>
  </fieldset>
  <AddElementDialog
    :visible="adding !== null"
    :catalog="catalog"
    :column="adding ?? 0"
    @update:visible="(v) => !v && (adding = null)"
    @add="addElement"
  />
</template>

<style scoped>
.layout {
  border: none;
  margin: 0;
  padding: 0;
  min-width: 0;
}

.column {
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  padding: var(--pnmt-space-2);
}

.column header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
  margin-bottom: var(--pnmt-space-2);
}

.elements {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.elements li {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 2px var(--pnmt-space-1);
  border-radius: 4px;
  background: var(--pnmt-bg);
}

.elements li.dragging {
  opacity: 0.4;
}

.handle {
  cursor: grab;
  color: var(--pnmt-muted);
  padding: 0 4px;
}

.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.empty {
  padding: var(--pnmt-space-2);
  font-size: 0.85rem;
  background: none !important;
}
</style>
