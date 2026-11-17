<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import { formatPath, type Segment } from '@/views/sensors/advanced/advanced'
import JsonTreeMarks from './JsonTreeMarks.vue'
import JsonTreeValue from './JsonTreeValue.vue'
import {
  CHIP_LIMIT,
  compareCells,
  hasChildren,
  isScalar,
  jsonTreeKey,
  keysOf,
  PAGE_SIZE,
  summaryOf,
  tableShape,
  type Container,
  type TableRow,
} from './jsonTree'

/**
 * The inside of an object or a list in JsonTree (docs/advanced-sensor-plan.md §10.3), recursive: members as rows and
 * nested sections (open for the first two levels), a list of objects or an object of same-shaped objects as a sortable
 * table with nested values expandable in their cells, a list of scalars as chips (+n more past 50), and lists past 100
 * items in pages. While filtering, only what matches shows, and the way to it.
 */
const props = withDefaults(defineProps<{ value: Container; segments: Segment[]; depth: number; whole?: boolean }>(), {
  whole: false,
})
const ctx = inject(jsonTreeKey)!

interface Member {
  key: string | number
  label: string
  value: unknown
  segments: Segment[]
  path: string
  nested: boolean
}

const members = computed<Member[]>(() => {
  const v = props.value
  const member = (key: string | number, value: unknown): Member => {
    const at = [...props.segments, key]
    return {
      key,
      label: typeof key === 'number' ? `[${key}]` : key,
      value,
      segments: at,
      path: formatPath(at),
      nested: hasChildren(value),
    }
  }
  return Array.isArray(v) ? v.map((x, i) => member(i, x)) : keysOf(v).map((k) => member(k, v[k]))
})

const filter = ctx.filter
const visible = (path: string) => props.whole || !filter.value || filter.value.show.has(path)
const wholeAt = (path: string) => props.whole || !!filter.value?.whole.has(path)
const shownMembers = computed(() => members.value.filter((m) => visible(m.path)))

const shape = computed(() => tableShape(props.value, props.segments))
const chips = computed(
  () => !shape.value && Array.isArray(props.value) && props.value.length > 0 && props.value.every(isScalar),
)

// --- sections: open for the first two levels, all of them while filtering ----------------------------------------------
const toggled = ref<Record<string, boolean>>({})
watch(
  () => filter.value,
  () => (toggled.value = {}),
)
const isOpen = (path: string) => toggled.value[path] ?? (filter.value ? true : props.depth === 0)
const toggle = (path: string) => (toggled.value = { ...toggled.value, [path]: !isOpen(path) })
const changedInside = (path: string) => !!ctx.changes.value?.inside.has(path) || !!ctx.changes.value?.changed.has(path)

// --- pages and chips ---------------------------------------------------------------------------------------------------
const page = ref(0)
const allChips = ref(false)
watch(
  () => filter.value,
  () => (page.value = 0),
)
const pages = (n: number) => Math.max(1, Math.ceil(n / PAGE_SIZE))
const paged = <T,>(list: T[]) => list.slice(page.value * PAGE_SIZE, (page.value + 1) * PAGE_SIZE)
const shownChips = computed(() => {
  const list = shownMembers.value
  if (!allChips.value && list.length > CHIP_LIMIT) return list.slice(0, CHIP_LIMIT)
  return list.length > PAGE_SIZE ? paged(list) : list
})
const chipPages = computed(() =>
  allChips.value || shownMembers.value.length <= CHIP_LIMIT ? pages(shownMembers.value.length) : 1,
)

// --- the table ---------------------------------------------------------------------------------------------------------
const sortKey = ref<string | null>(null)
const sortDir = ref<1 | -1>(1)
const NAME = '\u0000name'
function sortBy(column: string) {
  if (sortKey.value === column) {
    if (sortDir.value === 1) sortDir.value = -1
    else sortKey.value = null
  } else {
    sortKey.value = column
    sortDir.value = 1
  }
  page.value = 0
}
const ariaSort = (column: string) =>
  sortKey.value !== column ? 'none' : sortDir.value === 1 ? 'ascending' : 'descending'
const sortIcon = (column: string) =>
  sortKey.value !== column
    ? 'pi pi-sort-alt'
    : sortDir.value === 1
      ? 'pi pi-sort-amount-up-alt'
      : 'pi pi-sort-amount-down'

const rows = computed<TableRow[]>(() => {
  const s = shape.value
  if (!s) return []
  const list = s.rows.filter((r) => visible(r.path) || visible(r.memberPath))
  const key = sortKey.value
  if (key === null) return list
  const cell = (r: TableRow) => (key === NAME ? r.key : r.value[key])
  return [...list].sort((a, b) => compareCells(cell(a), cell(b)) * sortDir.value)
})
const shownRows = computed(() => (rows.value.length > PAGE_SIZE ? paged(rows.value) : rows.value))
const rowPages = computed(() => pages(rows.value.length))

const has = (row: TableRow, column: string) => Object.prototype.hasOwnProperty.call(row.value, column)
const openCells = ref<Record<string, boolean>>({})
const cellPath = (row: TableRow, column: string) => formatPath([...row.segments, column])
const cellOpen = (row: TableRow, column: string) =>
  openCells.value[cellPath(row, column)] ?? (!!filter.value && !!filter.value.show.has(cellPath(row, column)))
const toggleCell = (row: TableRow, column: string) =>
  (openCells.value = { ...openCells.value, [cellPath(row, column)]: !cellOpen(row, column) })
const nestedColumns = (row: TableRow) =>
  (shape.value?.columns ?? []).filter((c) => has(row, c) && hasChildren(row.value[c]) && cellOpen(row, c))

const pageCount = computed(() =>
  shape.value ? rowPages.value : chips.value ? chipPages.value : pages(shownMembers.value.length),
)
const pageTotal = computed(() => (shape.value ? rows.value.length : shownMembers.value.length))
const shownList = computed(() =>
  shownMembers.value.length > PAGE_SIZE ? paged(shownMembers.value) : shownMembers.value,
)
</script>

<template>
  <!-- A table: a list of objects, or an object whose members share their shape. -->
  <div v-if="shape" class="jt-table-wrap">
    <table class="jt-table">
      <thead>
        <tr>
          <th v-if="shape.members" scope="col" :aria-sort="ariaSort(NAME)">
            <button type="button" class="jt-sort" @click="sortBy(NAME)">
              Name <i :class="sortIcon(NAME)" aria-hidden="true" />
            </button>
          </th>
          <th v-else scope="col" class="jt-index">#</th>
          <th v-for="c in shape.columns" :key="c" scope="col" :aria-sort="ariaSort(c)">
            <button type="button" class="jt-sort" @click="sortBy(c)">
              {{ c }} <i :class="sortIcon(c)" aria-hidden="true" />
            </button>
          </th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in shownRows" :key="row.path">
          <tr :class="{ 'jt-changed-row': changedInside(row.path) }">
            <th scope="row" class="jt-rowkey">
              <span class="jt-key">{{ row.key }}</span>
              <JsonTreeMarks :segments="row.segments" :value="row.value" />
            </th>
            <td v-for="c in shape.columns" :key="c">
              <span v-if="!has(row, c)" class="jt-null" aria-label="missing">–</span>
              <button
                v-else-if="hasChildren(row.value[c])"
                type="button"
                class="jt-cell-toggle"
                :class="{ 'jt-changed-inside': changedInside(cellPath(row, c)) }"
                :aria-expanded="cellOpen(row, c)"
                :aria-label="`${c} of ${row.key}: ${summaryOf(row.value[c])}`"
                @click="toggleCell(row, c)"
              >
                <i :class="cellOpen(row, c) ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
                {{ summaryOf(row.value[c]) }}
              </button>
              <JsonTreeValue
                v-else
                :value="row.value[c]"
                :segments="[...row.segments, c]"
                :unit="shape.unitBeside && c === 'observedValue' ? row.value.observedUnit : undefined"
              />
            </td>
          </tr>
          <tr v-for="c in nestedColumns(row)" :key="`${row.path}-${c}`" class="jt-nested-row">
            <td :colspan="shape.columns.length + 1">
              <div class="jt-nested-title">
                <span class="jt-key">{{ row.key }} · {{ c }}</span>
                <JsonTreeMarks :segments="[...row.segments, c]" :value="row.value[c]" />
              </div>
              <JsonTreeNode
                :value="row.value[c] as Container"
                :segments="[...row.segments, c]"
                :depth="depth + 2"
                :whole="wholeAt(row.memberPath) || wholeAt(row.path) || wholeAt(cellPath(row, c))"
              />
            </td>
          </tr>
        </template>
      </tbody>
    </table>
  </div>

  <!-- Chips: a list of scalars. -->
  <div v-else-if="chips" class="jt-chips">
    <span v-for="m in shownChips" :key="m.path" class="jt-chip">
      <JsonTreeValue :value="m.value" :segments="m.segments" />
    </span>
    <button v-if="!allChips && shownMembers.length > CHIP_LIMIT" type="button" class="jt-link" @click="allChips = true">
      +{{ shownMembers.length - CHIP_LIMIT }} more
    </button>
  </div>

  <!-- Members and items, as rows and nested sections. -->
  <div v-else class="jt-members">
    <template v-for="m in shownList" :key="m.path">
      <div v-if="!m.nested" class="jt-row">
        <span class="jt-key">{{ m.label }}</span>
        <JsonTreeValue :value="m.value" :segments="m.segments" />
      </div>
      <div v-else class="jt-section">
        <div class="jt-row jt-head">
          <button
            type="button"
            class="jt-toggle"
            :aria-expanded="isOpen(m.path)"
            :class="{ 'jt-changed-inside': changedInside(m.path) }"
            @click="toggle(m.path)"
          >
            <i :class="isOpen(m.path) ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
            <span class="jt-key">{{ m.label }}</span>
          </button>
          <span class="jt-summary">{{ summaryOf(m.value) }}</span>
          <JsonTreeMarks :segments="m.segments" :value="m.value" />
        </div>
        <div v-if="isOpen(m.path)" class="jt-children">
          <JsonTreeNode
            :value="m.value as Container"
            :segments="m.segments"
            :depth="depth + 1"
            :whole="wholeAt(m.path)"
          />
        </div>
      </div>
    </template>
  </div>

  <nav v-if="pageCount > 1" class="jt-pager" :aria-label="`Pages of ${formatPath(segments) || 'the report'}`">
    <button type="button" class="jt-link" :disabled="page === 0" aria-label="Previous page" @click="page--">
      <i class="pi pi-angle-left" aria-hidden="true" />
    </button>
    <span> {{ page * PAGE_SIZE + 1 }}–{{ Math.min((page + 1) * PAGE_SIZE, pageTotal) }} of {{ pageTotal }} </span>
    <button type="button" class="jt-link" :disabled="page >= pageCount - 1" aria-label="Next page" @click="page++">
      <i class="pi pi-angle-right" aria-hidden="true" />
    </button>
  </nav>
</template>
