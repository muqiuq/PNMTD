<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, watch } from 'vue'
import BoardClock from './BoardClock.vue'
import BoardElementView from './BoardElementView.vue'
import { imageLoaderKey, motionKey, type ImageLoader } from './inject'
import { fontSize } from './layout'
import MessageBox from './MessageBox.vue'
import type { ShownMessage } from './messages'
import StatusBar from './StatusBar.vue'
import { boardNow, boardTime, lookTime } from './time'
import type { BoardBarView, BoardElementData, BoardLayout, BoardView } from './types'
import './board.css'
import './themes/themes.css'

/**
 * A board (docs/boards-plan.md §6): the status bar or the header line, the columns with their elements, and the banner when its data is
 * old. Drawn alike by the board app and the admin's preview, whose layout may be a draft ahead of the data. The theme dresses only this
 * element and what is in it (§9.3).
 */
const props = withDefaults(
  defineProps<{
    layout: BoardLayout
    view: BoardView | null
    /** The window's height (the board app) or the preview's screen: fit to window scales the text with it. */
    height: number
    /** Its width: below 700 px (a phone), a board that fits the window is a page that scrolls instead. */
    width?: number
    name: string
    /** Since when there was no good answer: the board greys and says so (§6.4). */
    staleSince?: string | null
    selectable?: boolean
    selected?: string | null
    /** How its image elements fetch their files (docs/board-messages-plan.md §9.4). */
    images?: ImageLoader | null
    /** Its open messages, drawn over the columns (docs/board-messages-plan.md §5). */
    messages?: ShownMessage[]
    /** The editor's sample: drawn as it is, without timers. */
    stillMessages?: boolean
  }>(),
  {
    staleSince: null,
    selectable: false,
    selected: null,
    width: 1920,
    images: null,
    messages: () => [],
    stillMessages: false,
  },
)
const emit = defineEmits<{ select: [id: string] }>()

const time = computed(() =>
  boardTime(props.view?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone, lookTime(props.layout.look)),
)
const fit = computed(() => props.layout.look.fit && props.width >= 700)

/** Motion: the board's setting, unless the screen asks for less (prefers-reduced-motion). */
const reduced = ref(false)
const query =
  typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null
const onQuery = () => (reduced.value = !!query?.matches)
onQuery()
query?.addEventListener?.('change', onQuery)
onBeforeUnmount(() => query?.removeEventListener?.('change', onQuery))
const motion = computed(() => props.layout.look.motion && !reduced.value)
provide(motionKey, motion)
provide(imageLoaderKey, props.images)
const size = computed(() => `${fontSize(fit.value, props.height, props.layout.look.textScale)}px`)
const columns = computed(() => props.layout.columns.map((c) => `${c.width}fr`).join(' '))
const data = computed(() => new Map<string, BoardElementData>((props.view?.elements ?? []).map((e) => [e.id, e])))
const title = computed(() => props.layout.header.title ?? props.name)

/** Never green on old data: grey, saying why (§6.4). */
const bar = computed<BoardBarView>(() => {
  if (props.staleSince) {
    return { colour: 'Grey', text: 'Status unknown: no connection to PNMT', since: null, parts: [] }
  }
  return props.view?.bar ?? { colour: 'Grey', text: 'Loading…', since: null, parts: [] }
})
const showHeader = computed(
  () => !props.layout.bar.show && (props.layout.header.showTitle || props.layout.header.clock),
)

/** Flash once on turning red, not on every poll. */
const flash = ref(false)
watch(
  () => bar.value.colour,
  (colour, before) => {
    flash.value = colour === 'Red' && before !== undefined && before !== 'Red' && props.layout.bar.flash
  },
  { immediate: true },
)
</script>

<template>
  <div
    class="board"
    :data-board-theme="layout.look.theme"
    :data-fit="fit ? 'true' : 'false'"
    :data-motion="layout.look.motion ? 'true' : 'false'"
    :data-stale="staleSince ? 'true' : undefined"
    :data-colour="bar.colour"
    :style="{ fontSize: size }"
  >
    <StatusBar
      v-if="layout.bar.show && layout.bar.position === 'Top'"
      :bar="layout.bar"
      :view="bar"
      :header="layout.header"
      :title="title"
      :time="time"
      :now="boardNow"
      :flash="flash"
    />
    <div v-else-if="showHeader" class="board-header">
      <span v-if="layout.header.showTitle" class="board-title">{{ title }}</span>
      <BoardClock
        v-if="layout.header.clock"
        :time="time"
        :seconds="layout.header.seconds"
        :date="layout.header.date"
        :time-format="layout.header.timeFormat"
      />
    </div>
    <div class="board-main">
      <div class="board-columns" :style="{ gridTemplateColumns: columns }">
        <div v-for="(column, i) in layout.columns" :key="i" class="board-column">
          <BoardElementView
            v-for="element in column.elements"
            :key="element.id"
            :element="element"
            :data="data.get(element.id) ?? null"
            :fit="fit"
            :time="time"
            :now="boardNow"
            :selectable="selectable"
            :selected="selected === element.id"
            @click="selectable && emit('select', element.id)"
          />
        </div>
      </div>
      <MessageBox
        v-if="messages.length"
        :messages="messages"
        :size="layout.look.messageSize ?? 'Large'"
        :time="time"
        :narrow="width < 700"
        :still="stillMessages"
      />
    </div>
    <StatusBar
      v-if="layout.bar.show && layout.bar.position === 'Bottom'"
      :bar="layout.bar"
      :view="bar"
      :header="layout.header"
      :title="title"
      :time="time"
      :now="boardNow"
      :flash="flash"
    />
    <div v-if="staleSince" class="board-stale" role="alert">
      No connection to PNMT since {{ time.when(staleSince) }}. What is shown may be out of date.
    </div>
  </div>
</template>
