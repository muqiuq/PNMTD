<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import BoardCanvas from '@/boards/BoardCanvas.vue'
import { boardNow } from '@/boards/time'
import type { BoardElementData, BoardView } from '@/boards/types'
import { tokenFromPath } from './client'
import { showColour, useKiosk } from './kiosk'
import { useBoardPolling } from './polling'

/**
 * The board app (docs/boards-plan.md §4.3, §6.4): one board, from the token in the page's address, drawn to the window. A refusal fills
 * the screen with its reason and the address PNMT sees; a new version of PNMT reloads the page after a random 0–60 s, so that ten
 * screens don't reload at once.
 */
declare const __BOARD_VERSION__: string

const token = tokenFromPath(location.pathname)
const height = ref(window.innerHeight)
const width = ref(window.innerWidth)
const resize = () => {
  height.value = window.innerHeight
  width.value = window.innerWidth
}
onMounted(() => window.addEventListener('resize', resize))
onBeforeUnmount(() => window.removeEventListener('resize', resize))
useKiosk()

let reloading = false
function onVersion(version: string) {
  if (!reloading && version && version !== __BOARD_VERSION__ && __BOARD_VERSION__ !== 'dev') {
    reloading = true
    setTimeout(() => location.reload(), Math.random() * 60_000)
  }
}

const polling = token ? useBoardPolling(token, onVersion) : null
const view = computed<BoardView | null>(() => {
  const v = polling?.view.value ?? null
  const lounge = polling?.lounge.value
  if (!v || !lounge) return v
  return { ...v, elements: v.elements.map((e: BoardElementData) => (e.id === lounge.id ? lounge : e)) }
})
const error = computed(() => polling?.error.value ?? null)
const staleSince = computed(() => polling?.staleSince(boardNow.value) ?? null)
const refused = computed(() => (error.value?.refused || (error.value && !view.value) ? error.value : null))

watch(
  () => [view.value?.bar.colour, view.value?.name, staleSince.value] as const,
  ([colour, name, stale]) => {
    if (colour && name) showColour(stale ? 'Grey' : colour, name)
  },
)

const messages: Record<string, string> = {
  board_not_found: 'There is no board at this address. Its URL may have been renewed, or the board deleted.',
  board_disabled: 'This board is switched off.',
}
</script>

<template>
  <main v-if="!token" class="board-message">
    <h1>A board's address</h1>
    <p>
      This is where PNMT shows its boards. Open a board with its full URL, which an admin copies from the board's Access
      tab.
    </p>
  </main>
  <main v-else-if="refused" class="board-message" role="alert">
    <h1>
      {{
        refused.code === 'board_network'
          ? 'Not from this network'
          : refused.code === 'network'
            ? 'PNMT can’t be reached'
            : 'No board here'
      }}
    </h1>
    <p>{{ messages[refused.code] ?? refused.message }}</p>
    <p v-if="refused.address" class="address">PNMT sees this screen as {{ refused.address }}.</p>
    <p class="again">Trying again every minute.</p>
  </main>
  <BoardCanvas
    v-else-if="view"
    :layout="view.layout"
    :view="view"
    :height="height"
    :width="width"
    :name="view.name"
    :stale-since="staleSince"
  />
  <main v-else class="board-message"><p>Loading the board…</p></main>
</template>
