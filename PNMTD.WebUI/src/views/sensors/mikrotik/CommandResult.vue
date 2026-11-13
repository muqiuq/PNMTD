<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute, formatDuration } from '@/format'
import { useSession } from '@/stores/session'
import DiffView from './DiffView.vue'
import JobLog from './JobLog.vue'

/**
 * A command on one router (docs/mikrotik-plan.md §8.3): the outcome, the output with Copy and Download, what it
 * changed as the diff of the backups before and after, and the log. Cancel (one's own, or as an Admin), Redact
 * (Admin) and Run again.
 */
const props = defineProps<{ jobId: string; isAdmin: boolean; showText?: boolean }>()
const emit = defineEmits<{ again: [text: string] }>()
const session = useSession()
const { run, done, ask, notify } = useActions()

const running = (c?: S['MikrotikCommandItem']) => c?.state === 'Queued' || c?.state === 'Running'
const q = useQuery({
  queryKey: computed(() => [...keys.mikrotik, 'commands', 'detail', props.jobId]),
  queryFn: () => api.mikrotik.command(props.jobId),
  refetchInterval: (query) => (running(query.state.data?.command) ? 1500 : false),
})
const detail = computed(() => q.data.value)
const c = computed(() => detail.value?.command)
const duration = computed(() =>
  c.value?.startedAt && c.value.finishedAt
    ? formatDuration(new Date(c.value.finishedAt).getTime() - new Date(c.value.startedAt).getTime())
    : null,
)
const changed = computed(
  () => !!c.value?.backupBeforeId && !!c.value.backupAfterId && c.value.backupBeforeId !== c.value.backupAfterId,
)
const canCancel = computed(
  () =>
    !!c.value &&
    (c.value.state === 'Queued' ||
      (c.value.state === 'Running' && ['Check', 'Export', 'Run'].includes(c.value.step))) &&
    (props.isAdmin || c.value.requestedBy === session.user?.username),
)
const diff = ref<S['MikrotikBackupDiff'] | null>(null)
const showLog = ref(false)

async function toggleDiff() {
  if (diff.value) {
    diff.value = null
    return
  }
  const x = c.value
  if (!x?.sensorId || !x.backupAfterId) return
  diff.value =
    (await run(() => api.mikrotik.diff(x.sensorId!, x.backupAfterId!, x.backupBeforeId ?? undefined), {
      failure: 'No diff',
    })) ?? null
}

async function copy() {
  try {
    await navigator.clipboard.writeText(detail.value?.output ?? '')
    notify.success('Copied', 'The output')
  } catch {
    notify.error(new Error('The browser did not allow copying.'), 'Not copied')
  }
}

function download() {
  const x = c.value
  if (!x) return
  const url = URL.createObjectURL(new Blob([detail.value?.output ?? ''], { type: 'text/plain' }))
  const a = document.createElement('a')
  a.href = url
  a.download = `${x.routerName.replace(/[^\w.-]+/g, '-')}-${x.createdAt.slice(0, 19).replace(/[:T]/g, '-')}.txt`
  a.click()
  URL.revokeObjectURL(url)
}

async function cancel() {
  if (
    await ask(
      'Cancel the command?',
      'A running command loses its session; whatever the router did stays done.',
      'Cancel command',
    )
  ) {
    await done(() => api.mikrotik.cancelCommand(props.jobId), { success: 'Cancelled', invalidate: [keys.mikrotik] })
  }
}

async function redact() {
  if (
    await ask(
      'Redact this command?',
      'Its text, output and error are replaced for good by who redacted them and when. The backups before and after stay.',
      'Redact',
    )
  ) {
    await done(() => api.mikrotik.redactCommand(props.jobId), { success: 'Redacted', invalidate: [keys.mikrotik] })
  }
}

const severity = (s: S['MikrotikJobState']) =>
  s === 'Succeeded'
    ? 'success'
    : s === 'Failed'
      ? 'danger'
      : s === 'TimedOut'
        ? 'warn'
        : s === 'Running' || s === 'Queued'
          ? 'info'
          : 'secondary'
const stateLabel = (s: S['MikrotikJobState']) => (s === 'TimedOut' ? 'Timed out' : s)
</script>

<template>
  <div v-if="c && detail" class="result">
    <div class="head">
      <Tag :value="stateLabel(c.state)" :severity="severity(c.state)" />
      <span class="muted">
        {{ c.routerName }} · as {{ c.runAs }} · by {{ c.requestedBy }} · <RelativeTime :value="c.createdAt" />
        <template v-if="duration"> · {{ duration }}</template>
        <template v-if="c.exitStatus !== null && c.exitStatus !== undefined">
          · exit status {{ c.exitStatus }}</template
        >
        <template v-if="c.state === 'Running'"> · {{ c.step }}</template>
      </span>
      <span class="spacer" />
      <Button
        v-if="c.command"
        label="Run again"
        icon="pi pi-replay"
        size="small"
        text
        @click="emit('again', c.command)"
      />
      <Button v-if="canCancel" label="Cancel" size="small" text severity="danger" @click="cancel" />
      <Button
        v-if="isAdmin && !running(c) && !c.redactedAt"
        label="Redact"
        icon="pi pi-eye-slash"
        size="small"
        text
        severity="secondary"
        @click="redact"
      />
    </div>
    <p v-if="c.redactedAt" class="muted note">Redacted by {{ c.redactedBy }} on {{ formatAbsolute(c.redactedAt) }}.</p>
    <pre v-if="showText && c.command" class="text" aria-label="Command">{{ c.command }}</pre>
    <p v-if="c.error && !c.redactedAt" class="bad note">{{ c.error }}</p>
    <template v-if="detail.output !== null && detail.output !== undefined">
      <pre class="output" aria-label="Output">{{ detail.output || '(no output)' }}</pre>
      <div class="bar">
        <small v-if="c.outputTruncated" class="muted">The output was cut: only its beginning is kept.</small>
        <span class="spacer" />
        <Button label="Copy" icon="pi pi-copy" size="small" text :disabled="!detail.output" @click="copy" />
        <Button
          label="Download .txt"
          icon="pi pi-download"
          size="small"
          text
          :disabled="!detail.output"
          @click="download"
        />
      </div>
    </template>
    <div v-if="detail.added !== null && detail.added !== undefined" class="bar">
      <span v-if="changed" class="mono">
        Configuration changed: <span class="added">+{{ detail.added }}</span>
        <span class="removed">−{{ detail.removed }}</span> lines
      </span>
      <span v-else class="muted">The command changed no configuration.</span>
      <span class="spacer" />
      <Button
        v-if="changed && c.sensorId"
        :label="diff ? 'Hide the change' : 'Show the change'"
        size="small"
        text
        @click="toggleDiff"
      />
    </div>
    <DiffView v-if="diff" :diff="diff" />
    <div>
      <Button
        :label="showLog ? 'Hide the log' : 'Log'"
        size="small"
        text
        severity="secondary"
        @click="showLog = !showLog"
      />
      <JobLog v-if="showLog" :log="detail.log" class="log" />
    </div>
  </div>
</template>

<style scoped>
.result {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.head,
.bar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.spacer {
  flex: 1;
}

.note {
  margin: 0;
}

.text,
.output {
  margin: 0;
  padding: var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.output {
  max-height: 28rem;
  overflow: auto;
  white-space: pre;
}

.mono {
  font-family: var(--pnmt-mono);
}

.added {
  color: var(--pnmt-ok);
}

.removed {
  color: var(--pnmt-fail);
}

.bad {
  color: var(--pnmt-fail);
}

.log {
  margin-top: var(--pnmt-space-2);
}
</style>
