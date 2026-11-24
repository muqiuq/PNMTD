<script setup lang="ts">
import { computed } from 'vue'
import BoardClock from './BoardClock.vue'
import { colourSymbol } from './names'
import { since, type BoardTime } from './time'
import type { BoardBar, BoardBarView, BoardHeader } from './types'

/**
 * The status bar (docs/boards-plan.md §8.1): its colour with a symbol, the text of what matched, since when, and the header line's title
 * and clock. It flashes for ten seconds when it turns red, where the board allows motion.
 */
const props = defineProps<{
  bar: BoardBar
  view: BoardBarView
  header: BoardHeader
  title: string
  time: BoardTime
  now: number
  flash: boolean
}>()
const sinceText = computed(() =>
  props.view.since && props.view.colour !== 'Green' ? `since ${props.time.when(props.view.since)}` : '',
)
const age = computed(() =>
  props.view.since && props.view.colour !== 'Green' ? since(props.view.since, props.now) : '',
)
</script>

<template>
  <div
    class="board-bar"
    role="status"
    :data-colour="view.colour"
    :data-size="bar.size"
    :data-flash="flash && view.colour === 'Red' ? 'true' : undefined"
  >
    <span class="board-bar-symbol" aria-hidden="true">{{ colourSymbol[view.colour] }}</span>
    <span class="board-bar-text">{{ view.text }}</span>
    <span v-if="sinceText" class="board-bar-since" :title="age">{{ sinceText }}</span>
    <span v-if="header.showTitle" class="board-title">{{ title }}</span>
    <BoardClock
      v-if="header.clock"
      :time="time"
      :seconds="header.seconds"
      :date="header.date"
      :time-format="header.timeFormat"
    />
  </div>
</template>
