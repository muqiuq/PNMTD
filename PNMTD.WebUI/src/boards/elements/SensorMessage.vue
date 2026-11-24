<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { fitText } from '../fitText'
import { since } from '../time'
import type { BoardElement, BoardElementData, BoardTextSize } from '../types'

/**
 * One sensor's last message (docs/board-messages-plan.md §9.3), as plain text with its line breaks: in a size, or as large as the
 * element allows (Fit). The state's colour as a band or the background, and how old the message is.
 */
const props = defineProps<{ element: BoardElement; data: BoardElementData; fit: boolean; now: number }>()
const s = computed(() => props.element.settings)
const m = computed(() => props.data.sensorMessage)

const scale: Record<Exclude<BoardTextSize, 'Fit'>, number> = { Small: 1, Medium: 1.5, Large: 2.5, Huge: 4 }
const size = computed(() => s.value.textSize ?? 'Medium')
/** Without a height to fill (a page that scrolls, an element of its own height), Fit is as wide as the column allows, at most 6×. */
const widthOnly = computed(() => !props.fit || props.element.height == null)

const tone = { Ok: 'ok', Failing: 'fail', NoData: 'none', Off: 'none', Missing: 'none' } as const
const text = computed(() => {
  const x = m.value
  if (!x || x.state === 'Missing') return 'This sensor no longer exists'
  if (x.message) return x.message
  return x.state === 'NoData' ? 'No data yet' : 'No message'
})
const placeholder = computed(() => !m.value?.message)
/** Fit grows a message, never a placeholder such as "No data yet": that stays at the medium size. */
const isFit = computed(() => size.value === 'Fit' && !placeholder.value)
const age = computed(() => (s.value.age && m.value?.at ? `${since(m.value.at, props.now)} ago` : ''))

const box = ref<HTMLElement | null>(null)
const body = ref<HTMLElement | null>(null)
const fitted = ref(1)
let observer: ResizeObserver | null = null

async function measure() {
  if (!isFit.value) return
  await nextTick()
  if (!box.value || !body.value) return
  fitted.value = fitText(box.value, body.value, 0.8, widthOnly.value ? 6 : 20, widthOnly.value).size
}

onMounted(() => {
  void measure()
  void document.fonts?.ready.then(() => measure())
  if (typeof ResizeObserver !== 'undefined' && box.value) {
    observer = new ResizeObserver(() => void measure())
    observer.observe(box.value)
  }
})
onBeforeUnmount(() => observer?.disconnect())
watch(
  () => [text.value, size.value, widthOnly.value, s.value.monospace, s.value.center],
  () => void measure(),
)

const style = computed(() =>
  isFit.value
    ? { fontSize: `${fitted.value}em` }
    : {
        fontSize: `${scale[size.value === 'Fit' ? 'Medium' : size.value]}em`,
        WebkitLineClamp: String(s.value.rows ?? 6),
      },
)
</script>

<template>
  <div
    class="sensor-message"
    :data-tone="tone[m?.state ?? 'Missing']"
    :data-colour="s.stateColour ?? 'Bar'"
    :data-fit="isFit ? (widthOnly ? 'width' : 'box') : undefined"
    :data-center="s.center ? 'true' : undefined"
  >
    <div ref="box" class="box">
      <div
        ref="body"
        class="text"
        :class="{ mono: s.monospace, 'board-muted': placeholder, clamp: !isFit }"
        :style="style"
        data-test="sensor-message"
        v-text="text"
      />
    </div>
    <span v-if="age" class="board-muted age">{{ age }}</span>
  </div>
</template>

<style scoped>
.sensor-message {
  flex: 1 1 auto;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 0.25em;
  border-radius: var(--board-radius);
}

.sensor-message[data-colour='Bar'] {
  border-left: 0.35em solid var(--board-unknown);
  padding-left: 0.5em;
}

.sensor-message[data-colour='Bar'][data-tone='ok'] {
  border-left-color: var(--board-ok);
}

.sensor-message[data-colour='Bar'][data-tone='fail'] {
  border-left-color: var(--board-fail);
}

.sensor-message[data-colour='Background'] {
  padding: 0.4em 0.6em;
  background: var(--board-unknown);
  color: var(--board-unknown-on);
}

.sensor-message[data-colour='Background'][data-tone='ok'] {
  background: var(--board-ok);
  color: var(--board-ok-on);
}

.sensor-message[data-colour='Background'][data-tone='fail'] {
  background: var(--board-fail);
  color: var(--board-fail-on);
}

.sensor-message[data-colour='Background'] .board-muted {
  color: inherit;
  opacity: 0.8;
}

.box {
  min-height: 0;
  min-width: 0;
  overflow: hidden;
}

.sensor-message[data-fit='box'] .box {
  flex: 1 1 0;
  display: flex;
  flex-direction: column;
  justify-content: center;
}

.text {
  white-space: pre-wrap;
  overflow-wrap: normal;
  line-height: 1.2;
}

.text.clamp {
  display: -webkit-box;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}

.text.mono {
  font-family: var(--board-mono);
}

.sensor-message[data-center='true'] .text {
  text-align: center;
}

.age {
  font-size: 0.8em;
  align-self: flex-end;
}
</style>
