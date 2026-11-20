<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useCopy } from '@/composables/useCopy'
import { formatAbsolute } from '@/format'
import { formatBytes } from '@/format/bytes'
import {
  bodyView,
  channelIcon,
  channelLabel,
  downloadName,
  formatMs,
  isMasked,
  laneLabel,
  outcomeSeverity,
  storedNote,
  type OutboundLogName,
} from './outboundFormat'

/**
 * A record of the outbound log in a window (docs/redmine-queue-outbound-plan.md §7.3), a modal dialog, not a drawer: the
 * summary (for Redmine also the lane, the wait for a slot and the job, with *Show the job's calls*); the request and the
 * response, each with its headers and its body (JSON indented, Copy, Download, how much of a cut body is stored); an
 * e-mail's SMTP conversation; the error. Masked secrets say so. ← and → step through the list's page without closing.
 */
const props = defineProps<{
  log: OutboundLogName
  /** The record shown (`?op=`), null when closed. */
  recordId: number | null
  /** The ids of the list's page, in its order, for ← and →. */
  ids: number[]
}>()
const emit = defineEmits<{ close: []; open: [id: number]; job: [jobId: string] }>()
const visible = computed({ get: () => props.recordId !== null, set: (v) => !v && emit('close') })
const detail = useQuery({
  queryKey: computed(() => [...keys.outbound, 'record', props.log, props.recordId]),
  queryFn: () =>
    props.log === 'redmine'
      ? api.system.outbound.redmineCall(props.recordId!)
      : api.system.outbound.notification(props.recordId!),
  enabled: computed(() => props.recordId !== null),
  // A record never changes; the previous one stays until the next is in, so ← and → don't flash.
  staleTime: Infinity,
  placeholderData: keepPreviousData,
})
const d = computed(() => detail.data.value ?? null)
const notification = computed(() => d.value?.notification ?? null)
const call = computed(() => d.value?.redmine ?? null)
const item = computed(() => notification.value ?? call.value)
const header = computed(() => {
  if (notification.value) return `${channelLabel(notification.value.channel)} #${notification.value.id}`
  if (call.value) return `Redmine ${call.value.kind.toLowerCase()} #${call.value.id}`
  return props.log === 'redmine' ? 'Redmine call' : 'Notification'
})

/** The request and the response, shown alike. */
const parts = computed(() => {
  const x = d.value
  const i = item.value
  if (!x || !i) return []
  const answer =
    i.statusCode !== null
      ? `Status ${i.statusCode}`
      : i.outcome === 'Simulated'
        ? 'Not sent (simulated): no answer.'
        : 'No answer.'
  return [
    {
      key: 'request' as const,
      title: 'Request',
      line: `${i.method} ${i.target}`,
      headers: x.requestHeaders,
      body: bodyView(x.requestBody),
      bytes: i.requestBytes,
      note: storedNote(x.requestBody, i.requestBytes, i.truncated),
    },
    {
      key: 'response' as const,
      title: 'Response',
      line: answer,
      headers: x.responseHeaders,
      body: bodyView(x.responseBody),
      bytes: i.responseBytes,
      note: storedNote(x.responseBody, i.responseBytes, i.truncated),
    },
  ]
})
const masked = computed(() => {
  const x = d.value
  if (!x) return false
  return (
    [...x.requestHeaders, ...x.responseHeaders].some((h) => isMasked(h.value)) ||
    isMasked(x.requestBody) ||
    isMasked(x.responseBody) ||
    isMasked(notification.value?.recipient)
  )
})

function showJob() {
  if (call.value?.jobId) emit('job', call.value.jobId)
}

const copy = useCopy()
const maximized = ref(false)
function download(text: string, part: 'request' | 'response' | 'transcript', json: boolean) {
  if (props.recordId === null) return
  const url = URL.createObjectURL(new Blob([text], { type: json ? 'application/json' : 'text/plain' }))
  const a = document.createElement('a')
  a.href = url
  a.download = downloadName(props.log, props.recordId, part, json)
  a.click()
  URL.revokeObjectURL(url)
}

// ← and →: the previous and the next record of the list's page (newest first), the window stays open.
const index = computed(() => (props.recordId === null ? -1 : props.ids.indexOf(props.recordId)))
const previous = computed(() => (index.value > 0 ? props.ids[index.value - 1]! : null))
const next = computed(() =>
  index.value >= 0 && index.value < props.ids.length - 1 ? props.ids[index.value + 1]! : null,
)
function onKey(event: KeyboardEvent) {
  if (props.recordId === null || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return
  const target = event.target as HTMLElement | null
  if (target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))) return
  const to = event.key === 'ArrowLeft' ? previous.value : event.key === 'ArrowRight' ? next.value : null
  if (to === null) return
  event.preventDefault()
  emit('open', to)
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="header"
    modal
    maximizable
    dismissable-mask
    :style="{ width: 'min(1100px, 95vw)' }"
    @maximize="maximized = true"
    @unmaximize="maximized = false"
  >
    <ErrorState
      v-if="detail.isError.value && !d"
      :error="detail.error.value"
      what="the record"
      @retry="detail.refetch()"
    />
    <div v-else-if="!d || !item" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <div v-else class="record" :class="{ stale: detail.isPlaceholderData.value, maximized }">
      <div class="line">
        <Tag :value="item.outcome" :severity="outcomeSeverity(item.outcome)" />
        <span v-if="item.statusCode !== null" class="mono">{{ item.statusCode }}</span>
        <strong class="ellipsis" :title="item.summary">{{ item.summary }}</strong>
      </div>

      <section aria-labelledby="outbound-summary">
        <h4 id="outbound-summary">Summary</h4>
        <dl class="facts">
          <dt>Time</dt>
          <dd>
            {{ formatAbsolute(item.startedAt) }} <span class="muted">· <RelativeTime :value="item.startedAt" /></span>
          </dd>
          <dt>Duration</dt>
          <dd>{{ formatMs(item.durationMs) }}</dd>
          <template v-if="notification">
            <dt>Channel</dt>
            <dd>
              <i :class="channelIcon(notification.channel)" aria-hidden="true" />
              {{ channelLabel(notification.channel) }}
            </dd>
          </template>
          <template v-if="call">
            <dt>Kind</dt>
            <dd>{{ call.kind }} · {{ laneLabel(call.lane) }} lane</dd>
            <dt>Waited</dt>
            <dd>{{ formatMs(call.queuedMs) }} for a free slot</dd>
          </template>
          <dt>Outcome</dt>
          <dd>
            {{ item.outcome }}<template v-if="item.statusCode !== null"> · status {{ item.statusCode }}</template>
          </dd>
          <dt>Target</dt>
          <dd class="mono wrap">{{ item.target }}</dd>
          <template v-if="notification?.recipient">
            <dt>Recipient</dt>
            <dd class="mono wrap">{{ notification.recipient }}</dd>
          </template>
          <dt>Purpose</dt>
          <dd>{{ item.purpose }}</dd>
          <dt>Asked by</dt>
          <dd>{{ item.askedBy }}</dd>
          <template v-if="call?.jobId">
            <dt>Job</dt>
            <dd class="job">
              <span class="mono small">{{ call.jobId }}</span>
              <Button label="Show the job's calls" icon="pi pi-filter" text size="small" @click="showJob" />
            </dd>
          </template>
        </dl>
      </section>

      <p v-if="masked" class="muted small masked-note">
        <i class="pi pi-lock" aria-hidden="true" /> Values shown as •••abcd are masked on purpose: keys, tokens and
        passwords are never stored, only their last 4 characters.
      </p>

      <section v-for="part in parts" :key="part.key" :aria-labelledby="`outbound-${part.key}`">
        <h4 :id="`outbound-${part.key}`">{{ part.title }}</h4>
        <p class="mono wrap small target">{{ part.line }}</p>
        <table v-if="part.headers.length" class="headers" :aria-label="`${part.title} headers`">
          <tbody>
            <tr v-for="(h, k) in part.headers" :key="k">
              <th scope="row" class="mono">{{ h.name }}</th>
              <td class="mono" :class="{ masked: isMasked(h.value) }">
                {{ h.value }}
                <i v-if="isMasked(h.value)" class="pi pi-lock" title="Masked on purpose" aria-hidden="true" />
              </td>
            </tr>
          </tbody>
        </table>
        <p v-else class="muted small">No headers.</p>
        <div class="section-head">
          <span class="muted small">
            Body · {{ formatBytes(part.bytes) }}<template v-if="part.body.json"> · JSON</template>
            <template v-if="part.note"> · {{ part.note }}</template>
          </span>
          <span v-if="part.body.text" class="buttons">
            <Button
              label="Copy"
              icon="pi pi-copy"
              size="small"
              text
              @click="copy(part.body.text, `The ${part.key} body`)"
            />
            <Button
              label="Download"
              icon="pi pi-download"
              size="small"
              text
              @click="download(part.body.text, part.key, part.body.json)"
            />
          </span>
        </div>
        <pre v-if="part.body.text" class="box mono">{{ part.body.text }}</pre>
        <p v-else class="muted small">No body.</p>
      </section>

      <section v-if="d.transcript" aria-labelledby="outbound-transcript">
        <div class="section-head">
          <h4 id="outbound-transcript">Transcript</h4>
          <span class="buttons">
            <Button
              label="Copy"
              icon="pi pi-copy"
              size="small"
              text
              @click="copy(d?.transcript ?? '', 'The transcript')"
            />
            <Button
              label="Download"
              icon="pi pi-download"
              size="small"
              text
              @click="download(d?.transcript ?? '', 'transcript', false)"
            />
          </span>
        </div>
        <pre class="box mono">{{ d.transcript }}</pre>
      </section>

      <section v-if="item.error" aria-labelledby="outbound-error">
        <h4 id="outbound-error">Error</h4>
        <Message severity="error" :closable="false" size="small">{{ item.error }}</Message>
      </section>
    </div>
    <template #footer>
      <span class="muted small keys">← → step through the list</span>
      <Button
        label="Previous"
        icon="pi pi-angle-left"
        text
        size="small"
        :disabled="previous === null"
        aria-keyshortcuts="ArrowLeft"
        @click="previous !== null && emit('open', previous)"
      />
      <Button
        label="Next"
        icon="pi pi-angle-right"
        icon-pos="right"
        text
        size="small"
        :disabled="next === null"
        aria-keyshortcuts="ArrowRight"
        @click="next !== null && emit('open', next)"
      />
      <Button label="Close" text severity="secondary" @click="emit('close')" />
    </template>
  </Dialog>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.record {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.record.stale {
  opacity: 0.6;
}

.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  min-width: 0;
}

h4 {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
}

.wrap {
  overflow-wrap: anywhere;
}

.target {
  margin: 0 0 var(--pnmt-space-2);
}

.job {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.masked-note {
  margin: var(--pnmt-space-2) 0 0;
}

.headers {
  border-collapse: collapse;
  width: 100%;
  font-size: 0.857rem;
}

.headers th,
.headers td {
  text-align: left;
  vertical-align: top;
  padding: 2px var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  overflow-wrap: anywhere;
}

.headers th {
  width: 16rem;
  font-weight: 500;
  color: var(--pnmt-muted);
}

.headers td.masked {
  color: var(--pnmt-warn);
}

.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-2);
}

.section-head h4 {
  margin: var(--pnmt-space-3) 0 0;
}

.buttons {
  display: inline-flex;
  gap: var(--pnmt-space-1);
}

.box {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  max-height: 45vh;
  overflow: auto;
  font-size: 0.857rem;
  margin: 0;
}

.record.maximized .box {
  max-height: none;
}

.keys {
  margin-right: auto;
}

.small {
  font-size: 0.857rem;
}
</style>
