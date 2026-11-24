<script setup lang="ts">
import { computed, defineComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { noteNodes } from './boardMarkdown'
import { fitText } from './fitText'
import MessageIcon from './MessageIcon.vue'
import { countdown, kindNames, next, open, type ShownMessage } from './messages'
import type { BoardTime } from './time'

/**
 * The message box (docs/board-messages-plan.md §5): over the board's columns, the status bar left visible, the board dimmed behind it.
 * The kind's band, its icon and the title in large type, the text as large as fits, who sent it and when, the count of open ones and the
 * time left with a bar that drains. Several open: the most important first, a new one at once, then turn by turn every 15 s. Esc or the
 * ✕ hides it on this screen until a new one arrives. Sizes come from the box's height, not the board's text size.
 */
const props = withDefaults(
  defineProps<{
    messages: ShownMessage[]
    size?: 'Large' | 'Full'
    time: BoardTime
    /** Below 700 px wide: the full width, as tall as the text needs. */
    narrow?: boolean
    /** The editor's preview: no timers, the first message only. */
    still?: boolean
  }>(),
  { size: 'Large', narrow: false, still: false },
)

const now = ref(Date.now())
const ticker = props.still ? undefined : setInterval(() => (now.value = Date.now()), 1000)
const order = computed(() => (props.still ? props.messages : open(props.messages, now.value)))

const current = ref<string | null>(null)
const seen = new Set<string>()
const dismissed = ref(new Set<string>())
let rotation: ReturnType<typeof setInterval> | undefined

function rotate() {
  clearInterval(rotation)
  if (!props.still) rotation = setInterval(() => (current.value = next(order.value, current.value)), 15_000)
}

watch(
  () => order.value.map((m) => m.id).join(),
  () => {
    const fresh = order.value.find((m) => !seen.has(m.id))
    for (const m of order.value) seen.add(m.id)
    if (fresh) {
      // A new one at once, for one turn; then it takes its place in the order.
      current.value = fresh.id
      rotate()
    } else if (!order.value.some((m) => m.id === current.value)) {
      current.value = order.value[0]?.id ?? null
    }
  },
  { immediate: true },
)

const shown = computed(() => order.value.find((m) => m.id === current.value) ?? order.value[0] ?? null)
const visible = computed(() => !!shown.value && order.value.some((m) => !dismissed.value.has(m.id)))
const position = computed(() => (shown.value ? order.value.indexOf(shown.value) + 1 : 0))
const left = computed(() => (shown.value ? shown.value.endsAt - now.value : 0))
const drained = computed(() =>
  shown.value ? Math.max(0, Math.min(100, (left.value / (shown.value.seconds * 1000)) * 100)) : 0,
)
const sent = computed(() => {
  const m = shown.value
  if (!m) return ''
  const when = props.time.when(m.sentAt)
  return m.from ? `${m.from} · ${when}` : when
})

/** A screen's own: a preview (still) never closes, whatever Esc closed elsewhere on the page. */
function close() {
  if (!props.still) dismissed.value = new Set(order.value.map((m) => m.id))
}
function key(event: KeyboardEvent) {
  if (event.key === 'Escape' && visible.value && !props.still) close()
}

// --- sizes: 1 % of the box's height is the unit; the title 7 units, the text 4.5 down to 2 ---------------------------------------
const box = ref<HTMLElement | null>(null)
const textBox = ref<HTMLElement | null>(null)
const text = ref<HTMLElement | null>(null)
const unit = ref(10)
const cut = ref(false)
let observer: ResizeObserver | null = null

async function measure() {
  await nextTick()
  if (props.narrow || !box.value) return
  unit.value = Math.max(4, box.value.clientHeight / 100)
  await nextTick()
  if (textBox.value && text.value) cut.value = fitText(textBox.value, text.value, 2, 4.5).overflows
}

onMounted(() => {
  window.addEventListener('keydown', key)
  void document.fonts?.ready.then(() => measure())
  if (typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(() => void measure())
    watch(
      box,
      (el, before) => {
        if (before) observer?.unobserve(before)
        if (el) observer?.observe(el)
      },
      { immediate: true },
    )
  }
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', key)
  clearInterval(ticker)
  clearInterval(rotation)
  observer?.disconnect()
})
watch(
  () => [shown.value?.id, props.size, props.narrow, visible.value],
  () => void measure(),
  { immediate: true },
)

const Rendered = defineComponent({
  props: { markdown: { type: String, required: true } },
  setup: (p) => () => noteNodes(p.markdown, false),
})
</script>

<template>
  <div
    v-if="visible && shown"
    class="board-msg-layer"
    :data-size="narrow ? 'Narrow' : size"
    :data-kind="shown.kind"
    data-test="board-message"
  >
    <div class="board-msg-backdrop" />
    <section
      :key="shown.id"
      ref="box"
      class="board-msg"
      :data-kind="shown.kind"
      role="alertdialog"
      aria-live="assertive"
      :aria-labelledby="`board-msg-${shown.id}`"
      :style="narrow ? undefined : { fontSize: `${unit}px` }"
    >
      <header class="board-msg-band">
        <span class="board-msg-kind">{{ kindNames[shown.kind] }}</span>
        <button type="button" class="board-msg-close" aria-label="Close on this screen" @click="close">✕</button>
      </header>
      <div class="board-msg-body">
        <div class="board-msg-head">
          <MessageIcon :kind="shown.kind" />
          <h2 :id="`board-msg-${shown.id}`" class="board-msg-title">{{ shown.title }}</h2>
        </div>
        <div v-if="shown.text" ref="textBox" class="board-msg-textbox" :data-cut="cut ? 'true' : undefined">
          <div ref="text" class="board-msg-text"><Rendered :markdown="shown.text" /></div>
        </div>
      </div>
      <footer class="board-msg-foot">
        <span class="board-msg-sent">{{ sent }}</span>
        <span v-if="order.length > 1" class="board-msg-count">
          <span class="board-msg-dots" aria-hidden="true"
            ><span v-for="m in order" :key="m.id" :data-on="m.id === shown.id ? 'true' : undefined">●</span></span
          >
          {{ position }} / {{ order.length }}
        </span>
        <span v-if="!still" class="board-msg-left">closes in {{ countdown(left) }}</span>
      </footer>
      <div class="board-msg-drain" aria-hidden="true"><div :style="{ width: `${still ? 100 : drained}%` }" /></div>
    </section>
  </div>
</template>
