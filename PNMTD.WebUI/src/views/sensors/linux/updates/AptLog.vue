<script setup lang="ts">
import Button from 'primevue/button'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { api, type S } from '@/api/resources'
import { formatBytes } from '@/format/bytes'
import { cleanLog, isShortenedMarker, searchLines } from './osUpdatesFormat'

/**
 * A run's full log (docs/linux-os-updates-plan.md §10.1 2 and 5): monospace, as text (never HTML: apt's output is untrusted,
 * §7.2), with a search, *Download*, and PNMTD's marker where a long log was shortened. While the run lasts it follows the log:
 * asks for what came after the last offset every 2 s and stays at the end unless the person scrolled up.
 */
const props = withDefaults(defineProps<{ sensorId: string; run: S['OsUpdateRunItem']; height?: string }>(), {
  height: '24rem',
})
const emit = defineEmits<{ finished: [] }>()

const text = ref('')
const next = ref(0)
const part = ref<S['OsUpdateLogPart'] | null>(null)
const error = ref<string | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined
let generation = 0
const box = ref<HTMLElement | null>(null)
/** Whether the view follows the end: until the person scrolls up. */
const stick = ref(true)

/** Runs outside PNMT come from history.log: their packages are the summary, there is no output (§4.5). */
const noLog = computed(() => props.run.trigger === 'External' && props.run.logBytes === 0)
const finished = computed(() => part.value?.finished ?? !!props.run.finishedAt)

async function load(gen: number) {
  try {
    const p = await api.linux.osUpdates.log(props.sensorId, props.run.id, next.value)
    if (gen !== generation) return
    if (p.text) text.value += cleanLog(p.text)
    next.value = p.nextOffset
    part.value = p
    error.value = null
    if (p.finished) {
      emit('finished')
      return
    }
  } catch (e) {
    if (gen !== generation) return
    error.value = e instanceof Error && e.message ? e.message : 'The log could not be read.'
    // A finished run's log is read once; a running one is asked again.
    if (props.run.finishedAt) return
  }
  timer = setTimeout(() => void load(gen), 2000)
}

function start() {
  generation++
  clearTimeout(timer)
  text.value = ''
  next.value = 0
  part.value = null
  error.value = null
  stick.value = true
  if (!noLog.value) void load(generation)
}

function retry() {
  generation++
  clearTimeout(timer)
  void load(generation)
}

watch(() => props.run.id, start, { immediate: true })
onBeforeUnmount(() => {
  generation++
  clearTimeout(timer)
})

// --- following the end ---------------------------------------------------------------------------------------------------
function onScroll() {
  const el = box.value
  if (el) stick.value = el.scrollTop + el.clientHeight >= el.scrollHeight - 16
}
watch(text, async () => {
  if (!stick.value) return
  await nextTick()
  if (box.value) box.value.scrollTop = box.value.scrollHeight
})

// --- the text, its marker, and the search ------------------------------------------------------------------------------------
/** The text in parts, PNMTD's marker lines apart so they can be marked. */
const parts = computed(() => {
  if (!text.value.includes('left out: PNMT keeps')) return [{ text: text.value, marker: false }]
  const result: { text: string; marker: boolean }[] = []
  let plain: string[] = []
  for (const line of text.value.split('\n')) {
    if (isShortenedMarker(line)) {
      if (plain.length) result.push({ text: `${plain.join('\n')}\n`, marker: false })
      result.push({ text: `${line}\n`, marker: true })
      plain = []
    } else {
      plain.push(line)
    }
  }
  if (plain.length) result.push({ text: plain.join('\n'), marker: false })
  return result
})
const search = ref('')
const found = computed(() => searchLines(text.value, search.value))
const downloadUrl = computed(() => api.linux.osUpdates.logUrl(props.sensorId, props.run.id))
</script>

<template>
  <div class="apt-log">
    <p v-if="noLog" class="muted small none">
      No full log: this run happened outside PNMT, and history.log names only its packages.
    </p>
    <template v-else>
      <div class="toolbar">
        <IconField>
          <InputIcon class="pi pi-search" />
          <InputText v-model="search" size="small" placeholder="Search the log" aria-label="Search the log" />
        </IconField>
        <span v-if="search.trim()" class="muted small"
          >{{ found.length >= 2000 ? 'the first 2000' : found.length }}
          {{ found.length === 1 ? 'line matches' : 'lines match' }}</span
        >
        <span class="spacer" />
        <span v-if="!finished" class="muted small live"><i class="pi pi-circle-fill" aria-hidden="true" /> live</span>
        <span v-if="part" class="muted small">{{ formatBytes(part.totalBytes) }}</span>
        <Button
          as="a"
          :href="downloadUrl"
          label="Download"
          icon="pi pi-download"
          size="small"
          text
          aria-label="Download the log of this run"
        />
      </div>
      <p v-if="part?.truncated || run.logTruncated" class="small warn note">
        Shortened: PNMT keeps the start and the end of a long log; the marker shows where the middle was left out.
      </p>
      <p v-if="part?.incomplete || run.logIncomplete" class="small warn note">
        Incomplete: parts of this log never reached PNMT.
      </p>
      <p v-if="error" class="small bad note">
        {{ error }} <Button v-if="run.finishedAt" label="Retry" size="small" link @click="retry" />
      </p>
      <pre
        v-if="search.trim()"
        class="log"
        :style="{ maxHeight: height }"
      ><template v-for="l in found" :key="l.n"><span class="n">{{ l.n }}</span>{{ l.text }}{{ '\n' }}</template><span v-if="!found.length" class="muted">No line matches.</span></pre>
      <pre
        v-else
        ref="box"
        class="log"
        :style="{ maxHeight: height }"
        tabindex="0"
        aria-label="The log"
        @scroll="onScroll"
      ><template v-for="(p, i) in parts" :key="i"><span v-if="p.marker" class="marker">{{ p.text }}</span><template v-else>{{ p.text }}</template></template><span v-if="!text" class="muted">{{ finished ? '(empty)' : 'No output yet.' }}</span></pre>
    </template>
  </div>
</template>

<style scoped>
.apt-log {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.spacer {
  flex: 1;
}

.log {
  margin: 0;
  padding: var(--pnmt-space-2);
  overflow: auto;
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.n {
  display: inline-block;
  min-width: 3.5rem;
  margin-right: var(--pnmt-space-2);
  color: var(--pnmt-muted);
  user-select: none;
}

.marker {
  color: var(--pnmt-warn);
  font-weight: 600;
}

.live {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.live i {
  font-size: 0.5rem;
  color: var(--pnmt-ok);
}

.note,
.none {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
}

.bad {
  color: var(--pnmt-fail);
}
</style>
