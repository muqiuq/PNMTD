<script setup lang="ts" generic="T">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

/**
 * A list that, fit to window, shows the rows that fit and says how many more there are (docs/boards-plan.md §6.3); in a page that
 * scrolls, all of them. <c>more</c> adds what the server left out already. <c>keep="last"</c> keeps the newest rows at the end, as a
 * chat does, and puts "+ 4 more" above them.
 */
const props = withDefaults(
  defineProps<{
    items: T[]
    fit: boolean
    more?: number
    keyOf: (item: T) => string | number
    keep?: 'first' | 'last'
  }>(),
  { more: 0, keep: 'first' },
)

const list = ref<HTMLElement | null>(null)
const shown = ref(props.items.length)
let observer: ResizeObserver | null = null
let measuring = false

/** The rows that fit in the list's height, counted from the end it keeps, with room for the "more" line where one is needed. */
async function measure() {
  if (measuring) return
  measuring = true
  try {
    shown.value = props.items.length
    if (!props.fit || !list.value) return
    await nextTick()
    const box = list.value
    const fontSize = parseFloat(getComputedStyle(box).fontSize) || 16
    const gap = parseFloat(getComputedStyle(box).rowGap) || 0
    const rows = [...box.querySelectorAll<HTMLElement>(':scope > .board-fit-row')]
    const ordered = props.keep === 'last' ? [...rows].reverse() : rows
    const fits = (room: number) => {
      let used = 0
      let count = 0
      for (const row of ordered) {
        used += row.offsetHeight + (count > 0 ? gap : 0)
        if (used > room) break
        count++
      }
      return count
    }
    let count = fits(box.clientHeight)
    if (count < rows.length || props.more > 0) count = fits(box.clientHeight - fontSize * 1.4)
    shown.value = Math.max(0, count)
  } finally {
    measuring = false
  }
}

const visible = (index: number) =>
  props.keep === 'last' ? index >= props.items.length - shown.value : index < shown.value
const hidden = computed(() => props.items.length - Math.min(shown.value, props.items.length) + props.more)

onMounted(() => {
  void measure()
  // Web fonts change the rows' heights once they are in.
  void document.fonts?.ready.then(() => measure())
  if (typeof ResizeObserver !== 'undefined' && list.value) {
    observer = new ResizeObserver(() => void measure())
    observer.observe(list.value)
  }
})
onBeforeUnmount(() => observer?.disconnect())
watch(
  () => [props.items, props.fit, props.more],
  () => void measure(),
)
</script>

<template>
  <div v-if="keep === 'last' && hidden > 0" class="board-more">+ {{ hidden }} more</div>
  <ul ref="list" class="board-list" :class="{ 'board-list-last': keep === 'last' }">
    <li v-for="(item, i) in items" v-show="visible(i)" :key="keyOf(item)" class="board-row board-fit-row">
      <slot :item="item" :index="i" />
    </li>
  </ul>
  <div v-if="keep === 'first' && hidden > 0" class="board-more">+ {{ hidden }} more</div>
</template>
