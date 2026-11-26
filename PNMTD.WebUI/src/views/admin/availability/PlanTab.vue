<script setup lang="ts">
import { useInfiniteQuery, useQueryClient, type InfiniteData } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import ColumnEdge from '@/components/ColumnEdge.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'
import { useColumnWidths } from '@/composables/useColumnWidths'
import CodeChip from './CodeChip.vue'
import FillAgainDialog from './FillAgainDialog.vue'
import GoToDay from './GoToDay.vue'
import {
  addDays,
  after,
  barShare,
  before,
  belowMinimum,
  chipStyle,
  clockLine,
  datesBetween,
  exactCode,
  fallbackWarning,
  formatPlanDate,
  inZone,
  mergeDays,
  movePoint,
  noteSummary,
  noteTime,
  planRows,
  responsibleWarning,
  selectionRect,
  shouldBeSet,
  typeAhead,
  type GridColumn,
  type GridPoint,
  type GridSelection,
  type PlanDay,
  type PlanNote,
  type PlanType,
} from './plan'
import { planKey } from './useAvailability'

/**
 * The plan (docs/cookie-plan.md §8.2, §9.4) as a spreadsheet: a row per day, the header and the first columns in place,
 * loading 60 days further into the past or the future as it scrolls, and only the rows in view drawn, so years of days
 * for a dozen people stay quick. Today's row is marked, Today goes back to it. A click on a cell picks a type, typing a
 * code sets it; shift-click or dragging selects several. Everyone in the plan edits their own column from today on;
 * Availability: Edit edits everything, the responsible, the fallback and the comments too. The warnings of §8.2 are red.
 * Each month starts with a row of its own; the columns are as wide as a person drags them (docs/adjustments1.md).
 *
 * docs/availability-extensions-plan.md: a year's row before each January's (§4); *Go to a day* beside *Today*, kept in the
 * URL as ?day=, which loads the days around a day not loaded yet (§5); the notes, a second comment column for everyone in
 * the plan (§6); the responsible as the settings call it (§7); PNMT's zone with its date and time in the toolbar (§3.3).
 */
type Plan = S['AvailabilityPlanItem']
type Range = { from: string; to: string } | null
type Person = S['AvailabilityPersonItem']

const OVERSCAN = 10
/** Rows from the first or last one loaded at which the next chunk loads. */
const NEAR = 15
const STAFF = ['responsible', 'fallback'] as const

const queryClient = useQueryClient()
const { run, notify } = useActions()
const route = useRoute()
const router = useRouter()

const isDay = (v: unknown): v is string => typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v)
/**
 * The day the first page loads around (§5): none for the server's default (a week back, two months ahead). A day not
 * loaded yet gets a query of its own, from a week before it to 53 days after, which scrolls on both ways from there.
 */
const anchor = ref<string | null>(isDay(route.query.day) ? route.query.day : null)
/** The day to scroll to once it is loaded. */
const target = ref<string | null>(anchor.value)
const planQueryKey = computed<readonly unknown[]>(() => (anchor.value ? [...planKey, { day: anchor.value }] : planKey))

const plan = useInfiniteQuery(
  computed(() => ({
    queryKey: planQueryKey.value,
    queryFn: ({ pageParam }: { pageParam: Range }) => api.availability.plan(pageParam ?? {}),
    initialPageParam: (anchor.value
      ? { from: addDays(anchor.value, -7), to: addDays(anchor.value, 53) }
      : null) as Range,
    getPreviousPageParam: (first: Plan): Range | undefined => (first.days.length ? before(first.from) : undefined),
    getNextPageParam: (last: Plan): Range | undefined => (last.days.length ? after(last.to) : undefined),
    staleTime: 30_000,
  })),
)
const pages = computed(() => plan.data.value?.pages ?? [])
const meta = computed<Plan | undefined>(() => pages.value[pages.value.length - 1])
const days = computed(() => mergeDays(pages.value))
/** Where a date is among the days loaded. */
const index = computed(() => new Map(days.value.map((d, i) => [d.date, i])))
/** The table's rows: the days and a row per month (planRows); and where a date's row is among them. */
const rows = computed(() => planRows(days.value))
const rowIndex = computed(() => new Map(rows.value.flatMap((r, i) => (r.kind === 'day' ? [[r.date, i] as const] : []))))
const today = computed(() => meta.value?.today ?? '')
const people = computed(() => meta.value?.people ?? [])
const peopleById = computed(() => new Map(people.value.map((p) => [p.id, p])))
const columns = computed(() => meta.value?.columns ?? [])
const typesById = computed(() => {
  const all = new Map<string, PlanType>()
  for (const p of pages.value) for (const t of p.types) all.set(t.id, t)
  return all
})
const choosable = computed(() =>
  [...typesById.value.values()].filter((t) => !t.retired).sort((a, b) => a.order - b.order),
)
const canEditAll = computed(() => meta.value?.canEditAll ?? false)
const inPlan = computed(() => meta.value?.inPlan ?? false)
/** What the settings call the day's responsible (§7). */
const label = computed(() => meta.value?.responsibleLabel || 'Responsible')
/** Notes are written by everyone in the plan and by Edit (§6.2). */
const canWriteNotes = computed(() => inPlan.value || canEditAll.value)
const me = computed(() => people.value.find((p) => p.isMe))
const empty = computed(() => plan.isSuccess.value && pages.value.length > 0 && days.value.length === 0)
const cellsByDay = computed(() => new Map(days.value.map((d) => [d.date, new Map(d.cells.map((c) => [c.userId, c]))])))
const cellOf = (d: PlanDay, userId: string) => cellsByDay.value.get(d.date)?.get(userId)
const typeOf = (d: PlanDay, userId: string) => {
  const c = cellOf(d, userId)
  return c ? typesById.value.get(c.typeId) : undefined
}

// --- the rows in view: all as high as each other, a month's row too ---------------------------------------------------
const scroller = ref<HTMLElement | null>(null)
const pickerEl = ref<HTMLElement | null>(null)
const scrollTop = ref(0)
const viewHeight = ref(600)
/** Measured once drawn: a table row is as high as the browser makes it. */
const rowHeight = ref(40)
const headHeight = ref(40)
const start = computed(() => Math.max(0, Math.floor(scrollTop.value / rowHeight.value) - OVERSCAN))
const end = computed(() =>
  Math.min(rows.value.length, Math.ceil((scrollTop.value + viewHeight.value) / rowHeight.value) + OVERSCAN),
)
const visible = computed(() => rows.value.slice(start.value, end.value))
const padTop = computed(() => start.value * rowHeight.value)
const padBottom = computed(() => Math.max(0, rows.value.length - end.value) * rowHeight.value)
const span = computed(() => 6 + columns.value.length + people.value.length)

const resize =
  typeof ResizeObserver === 'undefined'
    ? null
    : new ResizeObserver(() => {
        if (scroller.value) viewHeight.value = scroller.value.clientHeight
      })
watch(scroller, (el) => {
  resize?.disconnect()
  if (el) {
    resize?.observe(el)
    viewHeight.value = el.clientHeight || viewHeight.value
  }
})

watch(
  visible,
  () => {
    const row = scroller.value?.querySelector<HTMLElement>('tr.day')
    const head = scroller.value?.querySelector<HTMLElement>('thead')
    if (row) {
      const h = row.getBoundingClientRect().height
      if (h > 0 && Math.abs(h - rowHeight.value) > 0.5) rowHeight.value = h
    }
    if (head) headHeight.value = head.getBoundingClientRect().height
  },
  { flush: 'post' },
)

function onScroll() {
  if (scroller.value) scrollTop.value = scroller.value.scrollTop
  loadNearEdges()
}

/** The next chunk, when the rows in view come near the first or the last day loaded; one request at a time. */
function loadNearEdges() {
  const el = scroller.value
  if (!el || plan.isFetching.value || !days.value.length) return
  const first = Math.floor(el.scrollTop / rowHeight.value)
  const last = Math.ceil((el.scrollTop + el.clientHeight) / rowHeight.value)
  if (first < NEAR && plan.hasPreviousPage.value) void plan.fetchPreviousPage()
  else if (last > rows.value.length - NEAR && plan.hasNextPage.value) void plan.fetchNextPage()
}
watch(
  () => plan.isFetching.value,
  (fetching) => !fetching && nextTick(loadNearEdges),
)

// Earlier days loaded: the rows in view move down by as many rows as came before them (months' rows too), so the view
// stays where it was.
watch(
  () => rows.value[0]?.key,
  (now, was) => {
    if (!now || !was || now === was) return
    const moved = rows.value.findIndex((r) => r.key === was)
    if (moved <= 0) return
    const shift = moved * rowHeight.value
    scrollTop.value += shift
    if (picker.value) picker.value = { ...picker.value, top: picker.value.top + shift }
    if (notesAt.value) notesAt.value = { ...notesAt.value, top: notesAt.value.top + shift }
    void nextTick(() => {
      if (scroller.value) scroller.value.scrollTop = scrollTop.value
    })
  },
)

function scrollToRow(i: number) {
  const el = scroller.value
  if (!el) return
  el.scrollTop = Math.max(0, i * rowHeight.value)
  scrollTop.value = el.scrollTop
}

/** A day's row with two rows before it, outlined for a moment when asked; false while the day isn't loaded. */
const flashed = ref<string | null>(null)
const announced = ref('')
let flashTimer: ReturnType<typeof setTimeout> | undefined
function scrollToDay(date: string, flash = false): boolean {
  const i = rowIndex.value.get(date)
  if (i === undefined) return false
  scrollToRow(i - 2)
  if (flash) {
    flashed.value = date
    const d = days.value[index.value.get(date) ?? -1]
    announced.value = `Showing ${d?.weekday ?? ''} ${formatPlanDate(date)}`
    clearTimeout(flashTimer)
    flashTimer = setTimeout(() => (flashed.value = null), 2_000)
  }
  return true
}

/** *Go to a day* (§5): scrolls to a loaded day, or loads the days around it; ?day= keeps it in the URL. */
function goTo(date: string) {
  if (route.query.day !== date) void router.replace({ query: { ...route.query, day: date } })
  if (scrollToDay(date, true)) return
  target.value = date
  anchor.value = date
}

/** Today's row: back to the server's default days when today isn't loaded (after a jump far away). */
function scrollToToday() {
  if (route.query.day) {
    const rest = { ...route.query }
    delete rest.day
    void router.replace({ query: rest })
  }
  if (scrollToDay(today.value)) return
  target.value = null
  anchor.value = null
  positioned.value = false
}

const positioned = ref(false)
watch(
  () => [days.value.length, planQueryKey.value] as const,
  ([n]) => {
    if (!n || !scroller.value) return
    if (target.value && scrollToDay(target.value, true)) {
      target.value = null
      positioned.value = true
    } else if (!positioned.value && !target.value) {
      positioned.value = true
      scrollToToday()
    }
  },
  { flush: 'post', immediate: true },
)
// Another day's query: the table is drawn anew, so what was open on the old one closes.
watch(planQueryKey, () => {
  closeNotes()
  clearSelection()
})
// Back and forward between days in the URL.
watch(
  () => route.query.day,
  (day) => {
    if (isDay(day) && day !== anchor.value && !rowIndex.value.has(day)) goTo(day)
  },
)

// --- the clock (§3.3): PNMT's zone, its date and time; a new day there loads the plan again ---------------------------
const now = ref(new Date())
const clock = setInterval(() => (now.value = new Date()), 30_000)
const zone = computed(() => meta.value?.timeZone || 'UTC')
const clockText = computed(() => clockLine(now.value, zone.value))
const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone
const clockTitle = computed(() => {
  const here = inZone(now.value, zone.value)
  const lines = [`PNMT's time zone: ${zone.value} (${here.offset}). The plan's days and hours are in it.`]
  if (browserZone && browserZone !== zone.value)
    lines.push(`Your browser: ${inZone(now.value, browserZone).time}, ${browserZone}.`)
  return lines.join('\n')
})
watch(
  () => inZone(now.value, zone.value).date,
  (date, was) => {
    if (was && date !== was && today.value && date !== today.value)
      void queryClient.invalidateQueries({ queryKey: planKey })
  },
)

/** A row brought into view, below the header. */
function reveal(date: string) {
  const el = scroller.value
  const i = rowIndex.value.get(date)
  if (!el || i === undefined) return
  const top = i * rowHeight.value
  if (top < el.scrollTop) el.scrollTop = top
  else if (top + rowHeight.value > el.scrollTop + el.clientHeight - headHeight.value)
    el.scrollTop = top + rowHeight.value - el.clientHeight + headHeight.value
  scrollTop.value = el.scrollTop
}

// --- who may edit what ----------------------------------------------------------------------------------------------
const staffColumn = (col: string) => col === 'responsible' || col === 'fallback'
function editable(date: string, col: string): boolean {
  if (staffColumn(col)) return canEditAll.value
  if (canEditAll.value) return true
  return peopleById.value.get(col)?.isMe === true && date >= today.value
}

const gridColumns = computed<GridColumn[]>(() => [
  ...(canEditAll.value ? STAFF.map((key) => ({ key, kind: 'staff' as const })) : []),
  ...people.value.map((p) => ({ key: p.id, kind: 'person' as const })),
])

// --- selecting ------------------------------------------------------------------------------------------------------
const selection = ref<GridSelection | null>(null)
const dragging = ref(false)
const picker = ref<{ top: number; left: number } | null>(null)
const typed = ref('')
let typedTimer: ReturnType<typeof setTimeout> | undefined
const rect = computed(() => (selection.value ? selectionRect(selection.value, gridColumns.value) : null))
const selectedKind = computed(() =>
  rect.value?.cols.length ? (staffColumn(rect.value.cols[0]) ? 'staff' : 'person') : null,
)
const isSelected = (date: string, col: string) =>
  !!rect.value && date >= rect.value.from && date <= rect.value.to && rect.value.cols.includes(col)
/** The cells of the selection one may change. */
const targets = computed<GridPoint[]>(() => {
  const r = rect.value
  if (!r) return []
  const out: GridPoint[] = []
  for (const date of datesBetween(r.from, r.to)) {
    if (!index.value.has(date)) continue
    for (const col of r.cols) if (editable(date, col)) out.push({ date, col })
  }
  return out
})
/** What typing chooses from: the types for people's cells, the people for the responsible and the fallback. */
const choices = computed<{ id: string; code: string }[]>(() =>
  selectedKind.value === 'staff' ? people.value : choosable.value,
)

function clearSelection() {
  selection.value = null
  picker.value = null
  typed.value = ''
}

function down(e: MouseEvent, date: string, col: string) {
  if (e.button !== 0) return
  commitComment()
  if (!editable(date, col)) {
    clearSelection()
    return
  }
  e.preventDefault()
  scroller.value?.focus({ preventScroll: true })
  picker.value = null
  typed.value = ''
  const point = { date, col }
  selection.value =
    e.shiftKey && selection.value ? { anchor: selection.value.anchor, focus: point } : { anchor: point, focus: point }
  dragging.value = true
}

function enter(date: string, col: string) {
  if (dragging.value && selection.value) selection.value = { anchor: selection.value.anchor, focus: { date, col } }
}

function released() {
  if (!dragging.value) return
  dragging.value = false
  void openPicker()
}

function pressedOutside(e: MouseEvent) {
  const wrap = scroller.value
  if (wrap && !wrap.contains(e.target as Node)) clearSelection()
  if (notesAt.value && !notesEl.value?.contains(e.target as Node)) closeNotes()
}

onMounted(() => {
  document.addEventListener('mouseup', released)
  document.addEventListener('mousedown', pressedOutside)
})
onBeforeUnmount(() => {
  document.removeEventListener('mouseup', released)
  document.removeEventListener('mousedown', pressedOutside)
  resize?.disconnect()
  clearTimeout(typedTimer)
  clearTimeout(flashTimer)
  clearInterval(clock)
})

/**
 * The menu of types (or people) beside the cell the selection ends at, so the column stays free for a shift-click: on
 * its right, or on its left near the edge; moved up when it would reach below the view.
 */
async function openPicker() {
  const s = selection.value
  const el = scroller.value
  if (!s || !el || !targets.value.length) return
  const cell = el.querySelector<HTMLElement>(`[data-cell="${s.focus.date}|${s.focus.col}"]`)
  if (!cell) return
  const box = el.getBoundingClientRect()
  const c = cell.getBoundingClientRect()
  const width = 256
  const right = c.right - box.left + el.scrollLeft + 4
  const left =
    right + width <= el.scrollLeft + el.clientWidth
      ? right
      : Math.max(el.scrollLeft, c.left - box.left + el.scrollLeft - width - 4)
  picker.value = { top: c.top - box.top + el.scrollTop, left }
  await nextTick()
  const p = pickerEl.value
  if (p && picker.value) {
    const bottom = el.scrollTop + el.clientHeight - 4
    if (picker.value.top + p.offsetHeight > bottom)
      picker.value = { ...picker.value, top: Math.max(el.scrollTop + headHeight.value, bottom - p.offsetHeight) }
  }
}

function key(e: KeyboardEvent) {
  if (e.key === 'Escape') {
    clearSelection()
    return
  }
  const target = e.target as HTMLElement
  if (target.closest('input, textarea, button')) return
  const s = selection.value
  if (!s || !days.value.length) return
  const arrows: Record<string, [number, number]> = {
    ArrowUp: [-1, 0],
    ArrowDown: [1, 0],
    ArrowLeft: [0, -1],
    ArrowRight: [0, 1],
  }
  const move = arrows[e.key]
  if (move) {
    e.preventDefault()
    const focus = movePoint(s.focus, move[0], move[1], gridColumns.value, days.value[0].date, days.value.at(-1)!.date)
    selection.value = e.shiftKey ? { anchor: s.anchor, focus } : { anchor: focus, focus }
    picker.value = null
    typed.value = ''
    reveal(focus.date)
    void nextTick(() =>
      scroller.value
        ?.querySelector(`[data-cell="${focus.date}|${focus.col}"]`)
        ?.scrollIntoView({ block: 'nearest', inline: 'nearest' }),
    )
    return
  }
  if (e.key === 'Enter') {
    e.preventDefault()
    const chosen = exactCode(typed.value, choices.value)
    typed.value = ''
    if (chosen) void choose(chosen.id)
    else void openPicker()
    return
  }
  if (e.key === 'Backspace' || e.key === 'Delete') {
    e.preventDefault()
    if (typed.value) typed.value = typed.value.slice(0, -1)
    else void choose(null)
    return
  }
  if (/^[a-z0-9]$/i.test(e.key) && !e.ctrlKey && !e.metaKey && !e.altKey) {
    e.preventDefault()
    const next = typeAhead(typed.value, e.key, choices.value)
    typed.value = next.typed
    clearTimeout(typedTimer)
    typedTimer = setTimeout(() => (typed.value = ''), 2_000)
    if (next.chosen) void choose(next.chosen.id)
  }
}

// --- saving ---------------------------------------------------------------------------------------------------------
/** Changes the days loaded in place: what was just set, before the server's answer, then the server's days. */
function updateDays(change: (d: PlanDay) => PlanDay | undefined) {
  queryClient.setQueryData<InfiniteData<Plan, Range>>(planQueryKey.value, (data) =>
    data ? { ...data, pages: data.pages.map((p) => ({ ...p, days: p.days.map((d) => change(d) ?? d) })) } : data,
  )
}

/** The days of a range as the server has them now: counts, warnings and who works follow a change. */
async function refresh(dates: readonly string[]) {
  if (!dates.length) return
  const sorted = [...dates].sort()
  try {
    for (let from = sorted[0]; from <= sorted[sorted.length - 1]; from = addDays(from, 400)) {
      const to = [addDays(from, 399), sorted[sorted.length - 1]].sort()[0]
      const fresh = new Map((await api.availability.plan({ from, to })).days.map((d) => [d.date, d]))
      updateDays((d) => fresh.get(d.date))
    }
  } catch {
    await queryClient.invalidateQueries({ queryKey: planKey })
  }
}

/** A failed save in a toast, with the server's messages by field (a day's responsible not in the plan …) when there are. */
function failed(e: unknown) {
  const messages = e instanceof ApiError ? Object.values(e.errors).flat() : []
  notify.error(messages.length ? new Error(messages.join(' ')) : e, 'Not saved')
}

async function choose(id: string | null) {
  scroller.value?.focus({ preventScroll: true })
  if (selectedKind.value === 'staff') await setStaff(id)
  else await setType(id)
}

async function setType(typeId: string | null) {
  const cells = targets.value.filter((t) => !staffColumn(t.col)).map((t) => ({ userId: t.col, date: t.date, typeId }))
  picker.value = null
  typed.value = ''
  if (!cells.length) return
  const byDate = new Map<string, Map<string, string | null>>()
  for (const c of cells) byDate.set(c.date, (byDate.get(c.date) ?? new Map()).set(c.userId, c.typeId))
  updateDays((d) => {
    const changes = byDate.get(d.date)
    if (!changes) return undefined
    const kept = d.cells.filter((c) => !changes.has(c.userId))
    const added = [...changes]
      .filter((entry): entry is [string, string] => entry[1] !== null)
      .map(([userId, id]) => ({ userId, typeId: id, filled: false }))
    return { ...d, cells: [...kept, ...added] }
  })
  try {
    await api.availability.saveCells(cells)
  } catch (e) {
    failed(e)
  }
  await refresh([...byDate.keys()])
}

async function saveDays(changes: S['AvailabilityDayChange'][]) {
  const byDate = new Map(changes.map((c) => [c.date, c]))
  updateDays((d) => {
    const c = byDate.get(d.date)
    if (!c) return undefined
    return {
      ...d,
      responsibleId: c.responsibleId,
      fallbackId: c.fallbackId,
      comment: c.comment,
      responsibleWorks: c.responsibleId === d.responsibleId ? d.responsibleWorks : null,
      fallbackWorks: c.fallbackId === d.fallbackId ? d.fallbackWorks : null,
    }
  })
  try {
    await api.availability.saveDays(changes)
  } catch (e) {
    failed(e)
  }
  await refresh([...byDate.keys()])
}

async function setStaff(personId: string | null) {
  const r = rect.value
  picker.value = null
  typed.value = ''
  if (!r || !canEditAll.value) return
  const changes = datesBetween(r.from, r.to)
    .map((date) => days.value[index.value.get(date) ?? -1])
    .filter((d): d is PlanDay => !!d)
    .map((d) => ({
      date: d.date,
      responsibleId: r.cols.includes('responsible') ? personId : (d.responsibleId ?? null),
      fallbackId: r.cols.includes('fallback') ? personId : (d.fallbackId ?? null),
      comment: d.comment ?? null,
    }))
  if (changes.length) await saveDays(changes)
}

// --- the comment ----------------------------------------------------------------------------------------------------
const editingComment = ref<string | null>(null)
const commentDraft = ref('')
function editComment(d: PlanDay) {
  if (!canEditAll.value || editingComment.value === d.date) return
  clearSelection()
  editingComment.value = d.date
  commentDraft.value = d.comment ?? ''
  void nextTick(() => scroller.value?.querySelector<HTMLInputElement>('input.comment-input')?.focus())
}
async function saveComment(d: PlanDay) {
  if (editingComment.value !== d.date) return
  editingComment.value = null
  const text = commentDraft.value.trim()
  if (text === (d.comment ?? '')) return
  await saveDays([
    { date: d.date, responsibleId: d.responsibleId ?? null, fallbackId: d.fallbackId ?? null, comment: text || null },
  ])
  scroller.value?.focus({ preventScroll: true })
}
/** A comment being written is saved when a cell is clicked (which keeps the focus where it is). */
function commitComment() {
  const date = editingComment.value
  const d = date ? days.value[index.value.get(date) ?? -1] : undefined
  if (d) void saveComment(d)
  else editingComment.value = null
}
function cancelComment() {
  editingComment.value = null
  scroller.value?.focus({ preventScroll: true })
}

// --- the notes (§6) -------------------------------------------------------------------------------------------------
/** The open list of a day's notes, placed under its cell like the picker. */
const notesAt = ref<{ date: string; top: number; left: number } | null>(null)
const notesEl = ref<HTMLElement | null>(null)
const noteDraft = ref('')
const editingNote = ref<string | null>(null)
const editDraft = ref('')
const noteBusy = ref(false)
const notesOf = (date: string): PlanNote[] => days.value[index.value.get(date) ?? -1]?.notes ?? []
const openDay = computed(() => (notesAt.value ? days.value[index.value.get(notesAt.value.date) ?? -1] : undefined))
const summaryOf = (d: PlanDay) => noteSummary(d.notes)
const mayDelete = (n: PlanNote) => n.mine || canEditAll.value

async function openNotes(date: string) {
  commitComment()
  clearSelection()
  const el = scroller.value
  const cell = el?.querySelector<HTMLElement>(`[data-notes="${date}"]`)
  if (!el || !cell) return
  const box = el.getBoundingClientRect()
  const c = cell.getBoundingClientRect()
  const width = 320
  const left = Math.max(
    el.scrollLeft,
    Math.min(c.left - box.left + el.scrollLeft, el.scrollLeft + el.clientWidth - width - 4),
  )
  notesAt.value = { date, top: c.bottom - box.top + el.scrollTop + 2, left }
  noteDraft.value = ''
  editingNote.value = null
  await nextTick()
  const pop = notesEl.value
  if (pop && notesAt.value) {
    const bottom = el.scrollTop + el.clientHeight - 4
    if (notesAt.value.top + pop.offsetHeight > bottom)
      notesAt.value = { ...notesAt.value, top: Math.max(el.scrollTop + headHeight.value, bottom - pop.offsetHeight) }
    pop.querySelector<HTMLInputElement>('input.note-new')?.focus()
  }
}
function closeNotes() {
  notesAt.value = null
  editingNote.value = null
}
function closeNotesToGrid() {
  closeNotes()
  scroller.value?.focus({ preventScroll: true })
}
/** A day's notes changed in the days loaded, as the server answered. */
function updateNotes(date: string, change: (notes: PlanNote[]) => PlanNote[]) {
  updateDays((d) => (d.date === date ? { ...d, notes: change(d.notes ?? []) } : undefined))
}
async function addNote() {
  const at = notesAt.value
  const text = noteDraft.value.trim()
  if (!at || !text || noteBusy.value) return
  noteBusy.value = true
  try {
    const note = await api.availability.addNote({ date: at.date, text })
    updateNotes(at.date, (notes) => [...notes, note])
    noteDraft.value = ''
  } catch (e) {
    failed(e)
  } finally {
    noteBusy.value = false
  }
}
function editNote(n: PlanNote) {
  editingNote.value = n.id
  editDraft.value = n.text
  void nextTick(() => notesEl.value?.querySelector<HTMLInputElement>('input.note-edit')?.focus())
}
async function saveNote(n: PlanNote) {
  const text = editDraft.value.trim()
  editingNote.value = null
  if (!text || text === n.text) return
  try {
    const saved = await api.availability.changeNote(n.id, text)
    updateNotes(n.date, (notes) => notes.map((x) => (x.id === n.id ? saved : x)))
  } catch (e) {
    failed(e)
  }
}
async function deleteNote(n: PlanNote) {
  try {
    await api.availability.deleteNote(n.id)
    updateNotes(n.date, (notes) => notes.filter((x) => x.id !== n.id))
    // The button is gone with its note: the focus stays in the list, so Escape still closes it.
    await nextTick()
    notesEl.value?.querySelector<HTMLInputElement>('input.note-new')?.focus()
  } catch (e) {
    failed(e)
  }
}

// --- Fill -----------------------------------------------------------------------------------------------------------
/** *Fill again* opens its options (docs/adjustments1.md); *Fill* and *Fill mine* fill the empty days at once. */
const fillingAgain = ref(false)

async function fill(again: boolean, mine = false, body?: S['AvailabilityFillRequest']) {
  const request = body ?? { again, overwriteOwn: false }
  const result = await run(() => (mine ? api.availability.fillMine(request) : api.availability.fill(request)), {
    failure: 'Not filled',
    invalidate: [planKey],
  })
  if (result)
    notify.success(
      'Filled',
      result.written || result.replaced
        ? `${result.written} cells written${result.replaced ? `, ${result.replaced} replaced` : ''}.`
        : 'Nothing was empty.',
    )
}

// --- how cells look -------------------------------------------------------------------------------------------------
const staffId = (d: PlanDay, col: string) => (col === 'responsible' ? d.responsibleId : d.fallbackId)
const staffWarning = (d: PlanDay, col: string) =>
  col === 'responsible' ? responsibleWarning(d, label.value) : fallbackWarning(d)
const staffName = (col: string) => (col === 'responsible' ? label.value : 'Fallback')
function staffTitle(d: PlanDay, col: string): string {
  const id = staffId(d, col)
  const p = id ? peopleById.value.get(id) : undefined
  const who = id ? (p ? `${p.name} (${p.code})` : 'Someone who is no longer in the plan') : 'Nobody'
  const warning = staffWarning(d, col)
  return `${staffName(col)}: ${who}${warning ? `. ${warning}` : ''}`
}
const staffLabel = (d: PlanDay, col: string) => {
  const id = staffId(d, col)
  return `${staffName(col)} ${formatPlanDate(d.date)}: ${id ? (peopleById.value.get(id)?.code ?? '?') : 'nobody'}`
}
const missingHere = (d: PlanDay, p: Person) => !cellOf(d, p.id) && shouldBeSet(d, today.value)
function cellTitle(d: PlanDay, p: Person): string {
  const t = typeOf(d, p.id)
  const c = cellOf(d, p.id)
  const what = t ? `${t.name}${t.retired ? ' (retired)' : ''}${c?.filled ? ', written by the fill' : ''}` : 'empty'
  return `${p.name}, ${d.weekday} ${formatPlanDate(d.date)}: ${what}${missingHere(d, p) ? ' (to be set)' : ''}`
}
const cellLabel = (d: PlanDay, p: Person) => `${p.code} ${formatPlanDate(d.date)}: ${typeOf(d, p.id)?.code ?? 'empty'}`
function countTitle(d: PlanDay, i: number): string {
  const c = columns.value[i]
  const below = belowMinimum(d, i, columns.value)
  return `${c?.name}: ${d.counts[i] ?? 0}${c?.minimum !== null && c?.minimum !== undefined ? `, the minimum is ${c.minimum}` : ''}${below ? ' (below)' : ''}`
}
const dateLine = (d: PlanDay) => (d.holiday ? `${d.holiday} · ${d.hours}` : d.hours)
const rowClass = (d: PlanDay) => ({
  closed: d.closed,
  today: d.date === today.value,
  past: d.date < today.value,
  flash: d.date === flashed.value,
})

// --- the columns' widths --------------------------------------------------------------------------------------------
const widths = useColumnWidths('availability-plan')
const colStyle = (key: string) => ({ width: widths.width(key) })
/** The date column sticks right of the day's, however wide that is. */
const dateLeft = computed(() => ({ left: widths.width('day') }))
const scrollPad = computed(() => ({
  scrollPaddingLeft: `calc(${widths.width('day') ?? '2.6rem'} + ${widths.width('date') ?? '7.6rem'})`,
}))
</script>

<template>
  <ErrorState
    v-if="plan.isError.value && !pages.length"
    :error="plan.error.value"
    what="the plan"
    @retry="plan.refetch()"
  />
  <div v-else-if="!pages.length" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <section v-else-if="empty" class="panel">
    <EmptyState
      icon="pi pi-calendar"
      title="The plan isn't yours to see."
      text="You aren't in the availability plan, and seeing it needs Availability: View. Admins and Availability: Manage put people in the plan."
    />
  </section>
  <div v-else class="plan">
    <div class="toolbar">
      <Button label="Today" icon="pi pi-calendar" size="small" severity="secondary" outlined @click="scrollToToday" />
      <GoToDay :today="today" @go="goTo" />
      <template v-if="canEditAll">
        <Button
          label="Fill"
          icon="pi pi-pen-to-square"
          size="small"
          severity="secondary"
          outlined
          title="Writes every empty working day of the next 365 from each person's default week"
          @click="fill(false)"
        />
        <Button label="Fill again…" size="small" severity="secondary" text @click="fillingAgain = true" />
      </template>
      <Button
        v-if="inPlan"
        label="Fill mine"
        icon="pi pi-user-edit"
        size="small"
        severity="secondary"
        outlined
        title="Writes the empty working days of your column from your default week"
        @click="fill(false, true)"
      />
      <i v-if="plan.isFetching.value" class="pi pi-spin pi-spinner muted" aria-label="Loading" />
      <span class="legend">
        <span v-for="t in choosable" :key="t.id" class="legend-item">
          <CodeChip :code="t.code" :colour="t.color" />
          <span class="muted small">{{ t.name }}</span>
        </span>
      </span>
      <span class="clock muted small" :title="clockTitle" data-testid="plan-clock">
        <i class="pi pi-clock" aria-hidden="true" />
        {{ clockText }}
      </span>
      <span class="sr-only" aria-live="polite">{{ announced }}</span>
    </div>
    <FillAgainDialog
      v-model:visible="fillingAgain"
      :people="people"
      :types="choosable"
      :today="today"
      @fill="(body) => fill(true, false, body)"
    />
    <Message v-if="!people.length" severity="info" :closable="false" size="small">
      Nobody is in the plan yet. Availability: Manage adds people on the People tab, admins under Admin → Users.
    </Message>

    <div
      ref="scroller"
      class="scroller"
      tabindex="0"
      role="region"
      aria-label="The plan"
      :class="{ dragging, resizing: !!widths.resizing.value }"
      :style="scrollPad"
      @scroll.passive="onScroll"
      @keydown="key"
    >
      <table class="grid">
        <colgroup>
          <col class="w-day" :style="colStyle('day')" />
          <col class="w-date" :style="colStyle('date')" />
          <col class="w-staff" :style="colStyle('responsible')" />
          <col class="w-staff" :style="colStyle('fallback')" />
          <col class="w-comment" :style="colStyle('comment')" />
          <col class="w-notes" :style="colStyle('notes')" />
          <col v-for="c in columns" :key="c.id" class="w-count" :style="colStyle(`count:${c.id}`)" />
          <col v-for="p in people" :key="p.id" class="w-person" :style="colStyle(`person:${p.id}`)" />
        </colgroup>
        <thead>
          <tr>
            <th class="c-day sticky-1" scope="col">Day<ColumnEdge :widths="widths" column="day" label="Day" /></th>
            <th class="c-date sticky-2" scope="col" :style="dateLeft">
              Date<ColumnEdge :widths="widths" column="date" label="Date" />
            </th>
            <th scope="col">{{ label }}<ColumnEdge :widths="widths" column="responsible" :label="label" /></th>
            <th scope="col">Fallback<ColumnEdge :widths="widths" column="fallback" label="Fallback" /></th>
            <th scope="col">Comment<ColumnEdge :widths="widths" column="comment" label="Comment" /></th>
            <th scope="col">Notes<ColumnEdge :widths="widths" column="notes" label="Notes" /></th>
            <th
              v-for="c in columns"
              :key="c.id"
              scope="col"
              class="count-head"
              :title="c.minimum !== null ? `${c.name}: at least ${c.minimum}` : c.name"
            >
              {{ c.name }}<ColumnEdge :widths="widths" :column="`count:${c.id}`" :label="c.name" />
            </th>
            <th v-for="p in people" :key="p.id" scope="col" class="person-head" :title="p.name">
              <CodeChip :code="p.code" :colour="p.color" />
              <ColumnEdge :widths="widths" :column="`person:${p.id}`" :label="p.name" />
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="padTop" class="pad" aria-hidden="true" :style="{ height: `${padTop}px` }">
            <td :colspan="span" />
          </tr>
          <template v-for="d in visible" :key="d.key">
            <!-- A year's row before January's (§4), and a month's before its first day (docs/adjustments1.md), each as high
                 as a day's: the view counts in rows. -->
            <tr v-if="d.kind === 'year'" class="year">
              <td colspan="2" class="c-year sticky-1">{{ d.label }}</td>
              <td :colspan="span - 2" class="c-year-rest" />
            </tr>
            <tr v-else-if="d.kind === 'month'" class="month">
              <td colspan="2" class="c-month sticky-1">{{ d.label }}</td>
              <td :colspan="span - 2" class="c-month-rest" />
            </tr>
            <tr v-else class="day" :class="rowClass(d)" :aria-current="d.date === today ? 'date' : undefined">
              <td class="c-day sticky-1">{{ d.weekday }}</td>
              <td class="c-date sticky-2" :style="dateLeft" :title="dateLine(d)">
                <div class="date">
                  {{ formatPlanDate(d.date) }}
                  <i
                    v-if="d.missing"
                    class="pi pi-exclamation-circle missing-icon"
                    :title="`${d.missing} empty ${d.missing === 1 ? 'cell' : 'cells'}`"
                    :aria-label="`${d.missing} empty`"
                    role="img"
                  />
                </div>
                <div class="hours ellipsis" :class="{ holiday: d.holiday }">{{ dateLine(d) }}</div>
              </td>
              <td
                v-for="col in STAFF"
                :key="col"
                class="c-staff cell"
                :class="{
                  selected: isSelected(d.date, col),
                  editable: editable(d.date, col),
                  warning: !!staffWarning(d, col),
                }"
                :data-cell="`${d.date}|${col}`"
                :title="staffTitle(d, col)"
                :aria-label="staffLabel(d, col)"
                @mousedown="down($event, d.date, col)"
                @mouseenter="enter(d.date, col)"
              >
                <CodeChip
                  v-if="staffId(d, col)"
                  :code="peopleById.get(staffId(d, col)!)?.code ?? '?'"
                  :colour="peopleById.get(staffId(d, col)!)?.color"
                />
                <span v-else-if="staffWarning(d, col)" class="nobody">–</span>
              </td>
              <td
                class="c-comment"
                :class="{ editable: canEditAll }"
                :title="d.comment ?? undefined"
                @click="editComment(d)"
              >
                <InputText
                  v-if="editingComment === d.date"
                  v-model="commentDraft"
                  size="small"
                  class="comment-input"
                  maxlength="500"
                  :aria-label="`Comment on ${formatPlanDate(d.date)}`"
                  @keydown.enter.prevent="saveComment(d)"
                  @keydown.esc.stop.prevent="cancelComment"
                  @blur="saveComment(d)"
                />
                <span v-else class="ellipsis comment">{{ d.comment }}</span>
              </td>
              <td
                class="c-notes"
                :class="{ open: notesAt?.date === d.date, clickable: canWriteNotes || !!d.notes?.length }"
                :data-notes="d.date"
                :title="summaryOf(d) ? `${d.notes.length} ${d.notes.length === 1 ? 'note' : 'notes'}` : undefined"
                @click="(canWriteNotes || d.notes?.length) && openNotes(d.date)"
              >
                <span v-if="summaryOf(d)" class="note-line">
                  <span class="ellipsis">
                    <strong>{{ summaryOf(d)!.newest.author }}</strong> {{ summaryOf(d)!.newest.text }}
                  </span>
                  <span v-if="summaryOf(d)!.more" class="note-more">+{{ summaryOf(d)!.more }}</span>
                </span>
                <button
                  v-if="canWriteNotes"
                  type="button"
                  class="note-add"
                  :class="{ alone: !summaryOf(d) }"
                  :aria-label="`Add a note on ${formatPlanDate(d.date)}`"
                  title="Add a note"
                  @click.stop="openNotes(d.date)"
                >
                  <i class="pi pi-plus" aria-hidden="true" />
                </button>
              </td>
              <td
                v-for="(c, i) in columns"
                :key="c.id"
                class="c-count"
                :class="{ low: belowMinimum(d, i, columns) }"
                :title="d.closed ? undefined : countTitle(d, i)"
              >
                <template v-if="!d.closed">
                  <span class="bar" :style="{ width: `${barShare(d.counts[i] ?? 0, people.length)}%` }" />
                  <span class="n">{{ d.counts[i] ?? 0 }}</span>
                </template>
              </td>
              <td
                v-for="p in people"
                :key="p.id"
                class="c-person cell"
                :class="{
                  selected: isSelected(d.date, p.id),
                  editable: editable(d.date, p.id),
                  missing: missingHere(d, p),
                  typed: !!typeOf(d, p.id),
                  retired: typeOf(d, p.id)?.retired,
                }"
                :style="chipStyle(typeOf(d, p.id)?.color)"
                :data-cell="`${d.date}|${p.id}`"
                :title="cellTitle(d, p)"
                :aria-label="cellLabel(d, p)"
                @mousedown="down($event, d.date, p.id)"
                @mouseenter="enter(d.date, p.id)"
              >
                {{ typeOf(d, p.id)?.code }}
              </td>
            </tr>
          </template>
          <tr v-if="padBottom" class="pad" aria-hidden="true" :style="{ height: `${padBottom}px` }">
            <td :colspan="span" />
          </tr>
        </tbody>
      </table>

      <div
        v-if="picker && selectedKind"
        ref="pickerEl"
        class="picker"
        role="group"
        aria-label="Set the selected cells"
        :style="{ top: `${picker.top}px`, left: `${picker.left}px` }"
        @mousedown.stop
      >
        <div class="picker-head muted small">
          {{ targets.length }} {{ targets.length === 1 ? 'cell' : 'cells'
          }}<template v-if="typed"> · typed {{ typed }}</template>
        </div>
        <template v-if="selectedKind === 'person'">
          <button v-for="t in choosable" :key="t.id" type="button" class="pick" @click="choose(t.id)">
            <CodeChip :code="t.code" :colour="t.color" />
            <span class="ellipsis">{{ t.name }}</span>
          </button>
          <button type="button" class="pick" @click="choose(null)">
            <CodeChip code="" />
            <span>Empty</span>
          </button>
        </template>
        <template v-else>
          <button v-for="p in people" :key="p.id" type="button" class="pick" @click="choose(p.id)">
            <CodeChip :code="p.code" :colour="p.color" />
            <span class="ellipsis">{{ p.name }}</span>
          </button>
          <button type="button" class="pick" @click="choose(null)">
            <CodeChip code="" />
            <span>Nobody</span>
          </button>
        </template>
      </div>

      <div
        v-if="notesAt"
        ref="notesEl"
        class="notes-pop"
        role="dialog"
        :aria-label="`Notes on ${formatPlanDate(notesAt.date)}`"
        :style="{ top: `${notesAt.top}px`, left: `${notesAt.left}px` }"
        @mousedown.stop
        @keydown.esc.stop.prevent="closeNotesToGrid"
      >
        <div class="notes-head">
          <span>Notes on {{ openDay?.weekday }} {{ formatPlanDate(notesAt.date) }}</span>
          <Button
            icon="pi pi-times"
            size="small"
            severity="secondary"
            text
            aria-label="Close the notes"
            @click="closeNotesToGrid"
          />
        </div>
        <p v-if="!notesOf(notesAt.date).length" class="muted small note-none">No notes yet.</p>
        <ul class="note-list">
          <li v-for="n in notesOf(notesAt.date)" :key="n.id" class="note-item">
            <div class="note-meta">
              <strong>{{ n.author }}</strong>
              <span class="muted small">
                {{ noteTime(n.createdAt, zone) }}{{ n.changedAt !== n.createdAt ? ' (changed)' : '' }}
              </span>
              <span class="note-actions">
                <Button
                  v-if="n.mine && editingNote !== n.id"
                  icon="pi pi-pencil"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Change your note: ${n.text}`"
                  @click="editNote(n)"
                />
                <Button
                  v-if="mayDelete(n)"
                  icon="pi pi-trash"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Delete the note by ${n.author}: ${n.text}`"
                  @click="deleteNote(n)"
                />
              </span>
            </div>
            <InputText
              v-if="editingNote === n.id"
              v-model="editDraft"
              size="small"
              class="note-edit"
              maxlength="300"
              aria-label="The note"
              @keydown.enter.prevent="saveNote(n)"
              @keydown.esc.stop.prevent="editingNote = null"
            />
            <div v-else class="note-text">{{ n.text }}</div>
          </li>
        </ul>
        <form v-if="canWriteNotes" class="note-form" @submit.prevent="addNote">
          <InputText
            v-model="noteDraft"
            size="small"
            class="note-new"
            maxlength="300"
            placeholder="Add a note"
            aria-label="Add a note"
            :disabled="noteBusy"
          />
          <Button
            type="submit"
            icon="pi pi-plus"
            size="small"
            aria-label="Add"
            :disabled="!noteDraft.trim() || noteBusy"
          />
        </form>
      </div>
    </div>

    <div class="notes muted small">
      <span v-if="canEditAll"
        >Click a cell to choose, or type its code; shift-click or drag for several; Delete empties them. Click a comment
        to write one.</span
      >
      <span v-else-if="me"
        >You edit your own column ({{ me.code }}) from today on: click a cell, or type a code; shift-click or drag for
        several.</span
      >
      <span
        >Red: no {{ label }} on a working day, a {{ label }} or fallback who doesn't work that day, a count below its
        minimum, an empty cell.</span
      >
      <span v-if="canWriteNotes">Notes: <i class="pi pi-plus" aria-hidden="true" /> adds one under your name.</span>
      <span>Days and hours in {{ zone }}.</span>
      <span v-if="meta?.globalFallback">Global fallback: {{ meta.globalFallback }}.</span>
      <span
        >Drag a column's edge in the header to make it wider or narrower; a double-click gives it its default
        width.</span
      >
    </div>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.plan {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.toolbar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.legend {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  margin-left: auto;
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.scroller {
  position: relative;
  overflow: auto;
  overflow-anchor: none;
  height: calc(100vh - 300px);
  min-height: 320px;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  scroll-padding: 3rem 0 0 10rem;
  outline: none;
}

.scroller:focus-visible {
  box-shadow: 0 0 0 2px var(--p-primary-color);
}

.dragging {
  cursor: cell;
  user-select: none;
}

.grid {
  table-layout: fixed;
  border-collapse: separate;
  border-spacing: 0;
  width: max-content;
  font-size: 0.929rem;
}

.w-day {
  width: 2.6rem;
}
.w-date {
  width: 7.6rem;
}
.w-staff {
  width: 5.6rem;
}
.w-comment {
  width: 12rem;
}
.w-count {
  width: 5.4rem;
}
.w-person {
  width: 4.2rem;
}

th,
td {
  border-right: 1px solid var(--pnmt-border);
  border-bottom: 1px solid var(--pnmt-border);
  padding: 0 var(--pnmt-space-1);
  background: var(--pnmt-panel);
  white-space: nowrap;
  overflow: hidden;
}

th {
  position: sticky;
  top: 0;
  z-index: 2;
  padding: var(--pnmt-space-1);
  font-weight: 600;
  font-size: 0.857rem;
  text-align: left;
  vertical-align: bottom;
  white-space: normal;
  line-height: 1.2;
}

.person-head,
.count-head {
  text-align: center;
}

.sticky-1,
.sticky-2 {
  position: sticky;
  z-index: 1;
}

.sticky-1 {
  left: 0;
}

.sticky-2 {
  left: 2.6rem;
  border-right: 2px solid var(--pnmt-border);
}

th.sticky-1,
th.sticky-2 {
  z-index: 3;
}

tr.day td {
  height: 40px;
}

tr.pad td {
  padding: 0;
  border: none;
  background: transparent;
}

.c-day {
  text-align: center;
  font-weight: 500;
}

.date {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  line-height: 1.2;
}

.hours {
  font-size: 0.786rem;
  line-height: 1.2;
  color: var(--pnmt-muted);
}

.hours.holiday {
  color: var(--pnmt-text);
  font-weight: 500;
}

.missing-icon {
  color: var(--pnmt-fail);
  font-size: 0.75rem;
}

.cell {
  text-align: center;
  cursor: default;
}

.cell.editable {
  cursor: pointer;
}

.c-person {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  font-weight: 600;
}

.c-person.retired {
  text-decoration: line-through;
}

.c-person.missing {
  background: var(--pnmt-fail-soft);
}

.warning {
  background: var(--pnmt-fail-soft);
  box-shadow: inset 0 0 0 1px var(--pnmt-fail);
}

.nobody {
  color: var(--pnmt-fail);
  font-weight: 600;
}

.selected {
  box-shadow: inset 0 0 0 2px var(--p-primary-color) !important;
}

.c-comment {
  font-size: 0.857rem;
}

.c-comment.editable {
  cursor: text;
}

.w-notes {
  width: 12rem;
}

.c-notes {
  position: relative;
  font-size: 0.857rem;
}

.c-notes.clickable {
  cursor: pointer;
}

.c-notes.open {
  box-shadow: inset 0 0 0 2px var(--p-primary-color);
}

.note-line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-width: 0;
  padding-right: 1.4rem;
}

.note-more {
  flex: none;
  padding: 0 0.3rem;
  border-radius: 999px;
  background: color-mix(in srgb, var(--pnmt-text) 12%, var(--pnmt-panel));
  font-size: 0.786rem;
}

.note-add {
  position: absolute;
  right: 2px;
  top: 50%;
  transform: translateY(-50%);
  display: none;
  border: none;
  border-radius: var(--p-border-radius-sm);
  padding: 0.15rem 0.3rem;
  background: var(--pnmt-panel);
  color: var(--pnmt-muted);
  cursor: pointer;
}

.note-add.alone,
tr.day:hover .note-add,
.note-add:focus-visible {
  display: inline-flex;
}

.note-add.alone {
  opacity: 0.35;
}

tr.day:hover .note-add {
  opacity: 1;
}

.note-add:hover {
  color: var(--p-primary-color);
}

.notes-pop {
  position: absolute;
  z-index: 5;
  width: 320px;
  max-height: 360px;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  box-shadow: 0 6px 20px rgb(0 0 0 / 18%);
  white-space: normal;
}

.notes-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-weight: 600;
}

.note-none {
  margin: 0;
}

.note-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.note-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding-bottom: var(--pnmt-space-1);
  border-bottom: 1px solid var(--pnmt-border);
}

.note-meta {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.note-actions {
  margin-left: auto;
  display: inline-flex;
}

.note-text {
  overflow-wrap: anywhere;
}

.note-form {
  display: flex;
  gap: var(--pnmt-space-1);
}

.note-new,
.note-edit {
  flex: 1;
  min-width: 0;
}

.clock {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  white-space: nowrap;
}

tr.flash td {
  box-shadow: inset 0 0 0 2px var(--p-primary-color);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

.comment {
  display: block;
}

.comment-input {
  width: 100%;
}

.c-count {
  position: relative;
  text-align: center;
}

.bar {
  position: absolute;
  left: 0;
  top: 25%;
  bottom: 25%;
  background: color-mix(in srgb, var(--p-primary-color) 22%, transparent);
  border-radius: 0 3px 3px 0;
}

.n {
  position: relative;
  font-weight: 500;
}

/* A month's row (docs/adjustments1.md): as high as a day's, its name where the day and the date are. Every month as strong
   as January's was before 1.28; a year's row before January's, stronger still (docs/availability-extensions-plan.md §4). */
tr.month td,
tr.year td {
  height: 40px;
  border-right: none;
}

tr.month td {
  background: color-mix(in srgb, var(--pnmt-text) 16%, var(--pnmt-panel));
  box-shadow: inset 0 2px 0 var(--pnmt-text);
}

.c-month,
.c-year {
  border-right: 2px solid var(--pnmt-border) !important;
  padding-left: var(--pnmt-space-2);
  font-weight: 700;
  letter-spacing: 0.02em;
}

tr.month td.c-month {
  background: var(--pnmt-text);
  color: var(--pnmt-bg);
  font-size: 0.929rem;
}

tr.year td {
  background: color-mix(in srgb, var(--p-primary-color) 30%, var(--pnmt-panel));
  box-shadow:
    inset 0 3px 0 var(--pnmt-text),
    inset 0 -1px 0 var(--pnmt-text);
}

tr.year td.c-year {
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
  font-size: 1.143rem;
  letter-spacing: 0.08em;
}

.resizing {
  cursor: col-resize;
  user-select: none;
}

.c-count.low {
  background: var(--pnmt-fail-soft);
}

.c-count.low .bar {
  background: color-mix(in srgb, var(--pnmt-fail) 35%, transparent);
}

.c-count.low .n {
  color: var(--pnmt-fail);
  font-weight: 700;
}

tr.closed td:not(.typed) {
  background: var(--pnmt-none-soft);
}

tr.closed td {
  color: var(--pnmt-muted);
}

tr.past td.c-day,
tr.past td.c-date {
  color: var(--pnmt-muted);
}

/* Today: the day and the date stand out, and a line under the row (a shadow, so the row keeps its height). */
tr.today td.c-day,
tr.today td.c-date {
  background: color-mix(in srgb, var(--p-primary-color) 18%, var(--pnmt-panel));
  font-weight: 700;
  color: var(--pnmt-text);
}

tr.today td.c-day {
  box-shadow: inset 4px 0 0 var(--p-primary-color);
}

tr.today td:not(.c-day):not(.selected):not(.warning) {
  box-shadow: inset 0 -2px 0 var(--p-primary-color);
}

.picker {
  position: absolute;
  z-index: 5;
  width: 256px;
  max-height: 320px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  padding: var(--pnmt-space-1);
  background: var(--pnmt-panel);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 6px 18px rgb(0 0 0 / 18%);
}

.picker-head {
  padding: 2px var(--pnmt-space-2) var(--pnmt-space-1);
}

.pick {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  padding: 3px var(--pnmt-space-2);
  border: none;
  border-radius: var(--p-border-radius-sm);
  background: transparent;
  color: var(--pnmt-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.pick:hover,
.pick:focus-visible {
  background: var(--p-content-hover-background);
  outline: none;
}

.notes {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.small {
  font-size: 0.857rem;
}
</style>
