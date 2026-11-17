<script setup lang="ts">
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { computed, onBeforeUnmount, provide, ref, shallowRef, watch } from 'vue'
import FilterBar from '@/components/FilterBar.vue'
import JsonTreeNode from '@/components/jsonTree/JsonTreeNode.vue'
import JsonTreeValue from '@/components/jsonTree/JsonTreeValue.vue'
import {
  changesSince,
  filterTree,
  hasChildren,
  isContainer,
  isObject,
  jsonTreeKey,
  scalarText,
  stringifyOrdered,
  type Container,
  type JsonMark,
  type JsonPick,
} from '@/components/jsonTree/jsonTree'
import { useCopy } from '@/composables/useCopy'
import { useNow } from '@/composables/useNow'
import { formatPath, wildcardPath, type Segment } from '@/views/sensors/advanced/advanced'

/**
 * A JSON document shown readably with no setup (docs/advanced-sensor-plan.md §10.3, *The report*): objects as nested
 * sections in the app's order, lists of objects and objects of same-shaped objects as tables, lists of scalars as chips,
 * times, booleans, null and health words made readable. Optional: rule marks per concrete path (`marks`), the values that
 * changed since `previous` (another document, parsed), and a filter over keys and values. Each value has a menu: *Copy
 * path*, *Copy value* and, with `canWrite`, *Add rule…* (for an element of a list also *for every element*) and *Add
 * field…*, which the parent answers (`add-rule`, `add-field`). Everything is text: no value becomes HTML (§13).
 */
const props = withDefaults(
  defineProps<{
    value: unknown
    marks?: ReadonlyMap<string, readonly JsonMark[]> | null
    /** The document to compare with; undefined: no comparison. */
    previous?: unknown
    canWrite?: boolean
    label?: string
  }>(),
  { marks: null, previous: undefined, canWrite: false, label: 'The report' },
)
const emit = defineEmits<{ 'add-rule': [pick: JsonPick]; 'add-field': [pick: JsonPick] }>()
const copy = useCopy()
const now = useNow()

const search = ref('')
const debounced = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (text) => {
  clearTimeout(timer)
  timer = setTimeout(() => (debounced.value = text), 150)
})
onBeforeUnmount(() => clearTimeout(timer))

const filter = computed(() => filterTree(props.value, debounced.value))
const changes = computed(() => (props.previous === undefined ? null : changesSince(props.value, props.previous)))
const marks = computed<ReadonlyMap<string, readonly JsonMark[]>>(() => props.marks ?? new Map())

// --- the menu of a value: one for the whole tree ------------------------------------------------------------------------
const menu = ref<InstanceType<typeof Menu> | null>(null)
const target = shallowRef<{ segments: Segment[]; value: unknown } | null>(null)
/** Opens the menu at a value's button; a click while it is open closes it, as the ⋯ menus elsewhere. */
function openMenu(event: Event, segments: Segment[], value: unknown) {
  target.value = { segments, value }
  menu.value?.toggle(event)
}

const menuItems = computed<MenuItem[]>(() => {
  const t = target.value
  if (!t) return []
  const path = formatPath(t.segments)
  const inList = t.segments.some((s) => typeof s === 'number')
  const pick = (every: boolean): JsonPick => ({
    path: every ? wildcardPath(t.segments) : path,
    value: t.value,
    segments: t.segments,
    every,
  })
  const items: MenuItem[] = [
    { label: 'Copy path', icon: 'pi pi-copy', disabled: !path, command: () => copy(path, path) },
    {
      label: 'Copy value',
      icon: 'pi pi-clone',
      command: () => copy(isContainer(t.value) ? stringifyOrdered(t.value) : scalarText(t.value), path || undefined),
    },
  ]
  if (props.canWrite && path) {
    items.push(
      { separator: true },
      {
        label: inList ? 'Add rule for this value…' : 'Add rule…',
        icon: 'pi pi-plus',
        command: () => emit('add-rule', pick(false)),
      },
    )
    if (inList) {
      items.push({
        label: 'Add rule for every element…',
        icon: 'pi pi-list-check',
        command: () => emit('add-rule', pick(true)),
      })
    }
    if (!isObject(t.value)) {
      items.push({ label: 'Add field…', icon: 'pi pi-chart-line', command: () => emit('add-field', pick(false)) })
    }
  }
  return items
})

provide(jsonTreeKey, { marks, changes, filter, now, openMenu })

const changeNote = computed(() => {
  const c = changes.value
  if (!c) return null
  const n = c.changed.size
  if (!n && !c.removed) return 'Nothing changed since the previous report.'
  const parts = [`${n} value${n === 1 ? '' : 's'} changed or new`]
  if (c.removed) parts.push(`${c.removed} gone`)
  return `${parts.join(', ')} since the previous report.`
})
</script>

<template>
  <div class="json-tree">
    <div class="jt-toolbar">
      <FilterBar v-model:search="search" placeholder="Filter keys and values" />
      <span v-if="filter" class="jt-note" aria-live="polite">{{
        filter.matches ? `${filter.matches} match${filter.matches === 1 ? '' : 'es'}` : 'Nothing matches'
      }}</span>
      <span v-if="changeNote" class="jt-note"><span class="jt-swatch" aria-hidden="true" /> {{ changeNote }}</span>
      <slot name="toolbar" />
    </div>
    <div class="jt-body" role="region" :aria-label="label">
      <JsonTreeNode v-if="hasChildren(value)" :value="value as Container" :segments="[]" :depth="0" />
      <JsonTreeValue v-else :value="value" :segments="[]" />
    </div>
    <Menu ref="menu" :model="menuItems" popup />
  </div>
</template>

<!-- Not scoped: the tree is several components; everything is under .json-tree. -->
<style>
.json-tree {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.json-tree .jt-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
}

.json-tree .jt-note {
  color: var(--pnmt-muted);
  font-size: 0.857rem;
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.json-tree .jt-swatch {
  display: inline-block;
  width: 0.75rem;
  height: 0.75rem;
  border-radius: 2px;
  background: var(--pnmt-warn-soft);
  border: 1px solid var(--pnmt-warn);
}

.json-tree .jt-body {
  min-width: 0;
  overflow-wrap: anywhere;
}

.json-tree .jt-members {
  display: flex;
  flex-direction: column;
}

.json-tree .jt-row {
  display: grid;
  grid-template-columns: minmax(6rem, max-content) minmax(0, 1fr);
  gap: var(--pnmt-space-3);
  align-items: baseline;
  padding: 2px var(--pnmt-space-1);
  border-radius: var(--p-border-radius-sm);
}

.json-tree .jt-row:hover {
  background: var(--pnmt-none-soft);
}

.json-tree .jt-head {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.json-tree .jt-key {
  color: var(--pnmt-muted);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  min-width: 0;
  overflow-wrap: anywhere;
}

.json-tree .jt-toggle,
.json-tree .jt-cell-toggle,
.json-tree .jt-sort,
.json-tree .jt-link,
.json-tree .jt-act {
  font: inherit;
  color: inherit;
  background: none;
  border: 0;
  padding: 0;
  cursor: pointer;
  border-radius: var(--p-border-radius-sm);
}

.json-tree .jt-toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-width: 0;
}

.json-tree .jt-toggle .jt-key {
  color: var(--pnmt-text);
  font-weight: 600;
}

.json-tree .jt-toggle i,
.json-tree .jt-cell-toggle i {
  font-size: 0.7rem;
  color: var(--pnmt-muted);
}

.json-tree :is(.jt-toggle, .jt-cell-toggle, .jt-sort, .jt-link, .jt-act):focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.json-tree .jt-summary,
.json-tree .jt-null,
.json-tree .jt-ago,
.json-tree .jt-unit {
  color: var(--pnmt-muted);
}

.json-tree .jt-summary,
.json-tree .jt-ago {
  font-size: 0.857rem;
}

.json-tree .jt-children {
  margin-left: 0.4rem;
  padding-left: var(--pnmt-space-3);
  border-left: 1px solid var(--pnmt-border);
}

.json-tree .jt-section + .jt-section,
.json-tree .jt-row + .jt-section {
  margin-top: 2px;
}

.json-tree .jt-value {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0 var(--pnmt-space-1);
  min-width: 0;
  max-width: 100%;
  border-radius: var(--p-border-radius-sm);
}

.json-tree .jt-value.jt-has-block {
  display: flex;
}

.json-tree .jt-number {
  font-family: var(--pnmt-mono);
  font-size: 0.9286rem;
}

.json-tree .jt-time {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 0 var(--pnmt-space-1);
}

.json-tree .jt-health {
  font-weight: 600;
}

.json-tree .jt-bool {
  font-weight: 700;
}

.json-tree .jt-false {
  color: var(--pnmt-muted);
}

.json-tree .jt-block {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  flex: 1 1 100%;
  min-width: 0;
}

.json-tree .jt-text {
  white-space: pre-wrap;
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  max-width: 100%;
  overflow-wrap: anywhere;
}

.json-tree .jt-link {
  color: var(--p-primary-color);
  font-size: 0.857rem;
}

.json-tree .jt-link:disabled {
  color: var(--pnmt-muted);
  cursor: default;
}

.json-tree .jt-changed {
  background: var(--pnmt-warn-soft);
  box-shadow: 0 0 0 2px var(--pnmt-warn-soft);
}

.json-tree .jt-changed-inside .jt-key::after,
.json-tree .jt-cell-toggle.jt-changed-inside::after {
  content: '•';
  color: var(--pnmt-warn);
  margin-left: 0.25rem;
}

.json-tree .jt-changed-row > th {
  box-shadow: inset 3px 0 0 var(--pnmt-warn);
}

.json-tree .jt-mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 1.15rem;
  height: 1.15rem;
  padding: 0 0.2rem;
  border-radius: 999px;
  font-size: 0.75rem;
  font-weight: 700;
  line-height: 1;
  align-self: center;
  cursor: help;
}

.json-tree .jt-mark.tone-ok {
  color: var(--pnmt-ok);
  background: var(--pnmt-ok-soft);
}

.json-tree .jt-mark.tone-warn {
  color: var(--pnmt-warn);
  background: var(--pnmt-warn-soft);
}

.json-tree .jt-mark.tone-fail {
  color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}

.json-tree .jt-mark.tone-muted {
  color: var(--pnmt-muted);
  background: var(--pnmt-none-soft);
}

.json-tree .jt-act {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.4rem;
  height: 1.4rem;
  color: var(--pnmt-muted);
  opacity: 0;
  align-self: center;
}

.json-tree .jt-act i {
  font-size: 0.75rem;
}

.json-tree :is(.jt-row, .jt-chip, td, th, .jt-nested-title):hover .jt-act,
.json-tree .jt-act:focus-visible,
.json-tree .jt-act[aria-expanded='true'] {
  opacity: 1;
}

.json-tree .jt-act:hover {
  background: var(--pnmt-none-soft);
  color: var(--pnmt-text);
}

@media (hover: none) {
  .json-tree .jt-act {
    opacity: 0.6;
  }
}

.json-tree .tone-ok {
  color: var(--pnmt-ok);
}

.json-tree .tone-warn {
  color: var(--pnmt-warn);
}

.json-tree .tone-fail {
  color: var(--pnmt-fail);
}

.json-tree .jt-chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  padding: 2px 0;
}

.json-tree .jt-chip {
  display: inline-flex;
  align-items: center;
  padding: 0 var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  background: var(--pnmt-bg);
  max-width: 100%;
}

.json-tree .jt-table-wrap {
  overflow-x: auto;
  max-width: 100%;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  margin: 2px 0;
}

.json-tree .jt-table {
  border-collapse: collapse;
  width: 100%;
  font-size: 0.9286rem;
}

.json-tree .jt-table th,
.json-tree .jt-table td {
  text-align: left;
  vertical-align: top;
  padding: 3px var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
}

.json-tree .jt-table thead th {
  background: var(--pnmt-bg);
  font-weight: 600;
  white-space: nowrap;
}

.json-tree .jt-table tbody tr:last-child > * {
  border-bottom: 0;
}

.json-tree .jt-table tbody tr:hover > * {
  background: var(--pnmt-none-soft);
}

.json-tree .jt-sort {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  font-weight: 600;
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
}

.json-tree .jt-sort i {
  font-size: 0.7rem;
  color: var(--pnmt-muted);
}

.json-tree .jt-index {
  width: 1%;
}

.json-tree .jt-rowkey {
  font-weight: 400;
  white-space: nowrap;
}

.json-tree .jt-rowkey .jt-key {
  color: var(--pnmt-text);
}

.json-tree .jt-cell-toggle {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  color: var(--pnmt-muted);
  font-size: 0.857rem;
}

.json-tree .jt-nested-row > td {
  background: var(--pnmt-bg);
  padding-left: var(--pnmt-space-4);
}

.json-tree .jt-nested-title {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  margin-bottom: 2px;
}

.json-tree .jt-pager {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-size: 0.857rem;
  color: var(--pnmt-muted);
  padding: 2px var(--pnmt-space-1);
}

.json-tree .jt-sr {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}

@media (max-width: 599px) {
  .json-tree .jt-row {
    grid-template-columns: minmax(0, 1fr);
    gap: 0;
  }

  .json-tree .jt-children {
    padding-left: var(--pnmt-space-2);
  }
}
</style>
