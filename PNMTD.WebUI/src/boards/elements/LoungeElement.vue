<script setup lang="ts">
import { computed } from 'vue'
import { segments } from '@/shared/loungeText'
import FitList from '../FitList.vue'
import type { BoardTime } from '../time'
import type { BoardElementData, BoardLoungeLine } from '../types'

/**
 * The lounge's chat, read-only (§7.6): the newest lines, comments under their line, chips that name nothing, and who is typing. A board
 * never writes, comments or links anything.
 */
const props = defineProps<{ data: BoardElementData; fit: boolean; time: BoardTime }>()
const cookie = computed(() => props.data.lounge?.cookieName ?? 'Cookie')

function who(line: BoardLoungeLine): string {
  if (line.from === 'cookie') return cookie.value
  if (line.from === 'seraph') return 'Seraph'
  if (line.from === 'system') return ''
  return line.author ?? 'Someone'
}

/** Black or white on a person's colour, whichever reads. */
function textOn(colour: string | null | undefined): string {
  const m = /^#?([0-9a-f]{6})$/i.exec(colour ?? '')
  if (!m) return '#fff'
  const n = parseInt(m[1]!, 16)
  const [r, g, b] = [(n >> 16) & 255, (n >> 8) & 255, n & 255]
  return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? '#000' : '#fff'
}

/** Oldest first, as a chat reads; fit to window, the newest that fit stay at the bottom. */
const lines = computed(() => props.data.lounge?.lines ?? [])
const typing = computed(() => {
  const s = props.data.lounge?.typing
  return s ? `${s === 'cookie' ? cookie.value : s === 'seraph' ? 'Seraph' : s} is typing…` : null
})
</script>

<template>
  <p v-if="!lines.length" class="board-muted">The lounge is quiet.</p>
  <div v-else class="lounge">
    <FitList :items="lines" :fit="fit" :key-of="(l) => l.id" keep="last">
      <template #default="{ item }">
        <span
          v-if="item.from === 'person' && item.authorCode"
          class="code"
          :style="{ background: item.authorColor ?? undefined, color: textOn(item.authorColor) }"
          >{{ item.authorCode }}</span
        >
        <span class="board-grow text" :class="{ comment: item.kind === 'comment', important: item.important }">
          <strong v-if="who(item)" class="who">{{ who(item) }}</strong>
          <span class="board-muted">{{ time.when(item.at) }}</span><br />
          <template v-for="(s, i) in segments(item.text)" :key="i">
            <span v-if="s.kind === 'text'">{{ s.text }}</span>
            <span v-else class="board-chip">{{ s.text }}</span>
          </template>
        </span>
      </template>
    </FitList>
    <p v-if="typing" class="board-muted typing">{{ typing }}</p>
  </div>
</template>

<style scoped>
.lounge {
  display: flex;
  flex-direction: column;
  flex: 1 1 auto;
  min-height: 0;
}

.text {
  white-space: normal !important;
  display: -webkit-box;
  -webkit-line-clamp: 4;
  -webkit-box-orient: vertical;
}

.comment {
  padding-left: 1.2em;
  font-size: 0.92em;
}

.important {
  font-weight: 600;
}

.who {
  margin-right: 0.4em;
}

.code {
  flex: 0 0 auto;
  min-width: 1.8em;
  padding: 0 0.25em;
  border-radius: 0.3em;
  background: var(--board-unknown);
  color: #fff;
  font-size: 0.8em;
  font-weight: 700;
  text-align: center;
}

.typing {
  margin: 0.2em 0 0;
  font-style: italic;
}
</style>
