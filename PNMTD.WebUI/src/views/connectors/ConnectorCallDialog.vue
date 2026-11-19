<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useCopy } from '@/composables/useCopy'
import { useSession } from '@/stores/session'
import { isActive, outcomeLabel, stateSeverity } from './connectorFormat'

/**
 * A call (docs/connectors-plan.md §13.2): its arguments (secure ones by name), reference and note; its steps with their times; the
 * review per question with a link to the AI request; the run with its output; *Cancel…* while queued, *Redact…* once final.
 */
const props = defineProps<{ callId: string | null }>()
const emit = defineEmits<{ close: [] }>()
const session = useSession()
const canManage = computed(() => session.can('Connectors', 'Manage'))
const visible = computed({ get: () => props.callId !== null, set: (v) => !v && emit('close') })
const detail = useQuery({
  queryKey: computed(() => [...keys.connectors, 'call', props.callId]),
  queryFn: () => api.connectors.call(props.callId!),
  enabled: computed(() => props.callId !== null),
  refetchInterval: (q) => (q.state.data && !isActive(q.state.data.call.state) ? false : 2000),
})
const call = computed(() => detail.data.value?.call ?? null)
const { run, ask } = useActions()
const copy = useCopy()
async function cancel() {
  if (
    !(await ask(
      'Cancel the call?',
      'It leaves the queue and never runs; its caller reads "cancelled".',
      'Cancel the call',
    ))
  )
    return
  await run(() => api.connectors.cancelCall(props.callId!), { success: 'Cancelled', invalidate: [keys.connectors] })
}
async function redact() {
  if (
    !(await ask('Redact the call?', "Its arguments and note, and its run's arguments, are removed for good.", 'Redact'))
  )
    return
  await run(() => api.connectors.redactCall(props.callId!), {
    success: 'Redacted',
    invalidate: [keys.connectors, keys.agents],
  })
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Call"
    modal
    maximizable
    dismissable-mask
    :style="{ width: '90vw', maxWidth: '72rem' }"
    :breakpoints="{ '700px': '100vw' }"
  >
    <template v-if="detail.data.value && call">
      <div class="line">
        <Tag :value="outcomeLabel(call)" :severity="stateSeverity(call.state, call.exitCode)" />
        <strong>{{ call.connectorName }}</strong>
        <span class="muted">from {{ call.callerName }} at {{ call.from }}</span>
        <Tag v-if="call.dryRun" value="dry run" severity="secondary" />
        <Tag v-if="call.reviewState === 'unreviewed'" value="unreviewed" severity="warn" />
      </div>
      <Message v-if="call.message" :severity="call.state === 'Done' ? 'info' : 'warn'" :closable="false" size="small">{{
        call.message
      }}</Message>
      <dl class="facts">
        <dt>Ticket</dt>
        <dd class="mono small">{{ call.id }}</dd>
        <dt>Reference</dt>
        <dd>{{ call.reference ?? '–' }}</dd>
        <dt>Server</dt>
        <dd>
          <RouterLink v-if="call.server" :to="{ name: 'sensor', params: { id: call.server.id, tab: 'scripts' } }">{{
            call.serverName
          }}</RouterLink>
          <span v-else>–</span>
        </dd>
        <dt>Script</dt>
        <dd>
          <template v-if="call.scriptName"
            >{{ call.function ? `${call.function} in ` : '' }}{{ call.scriptName }}
            <span class="muted">revision {{ call.scriptRevision }}</span></template
          >
          <span v-else>–</span>
        </dd>
        <dt>Exit code</dt>
        <dd>{{ call.exitCode ?? '–' }}</dd>
        <dt v-if="detail.data.value.idempotencyKey">Idempotency-Key</dt>
        <dd v-if="detail.data.value.idempotencyKey" class="mono small">{{ detail.data.value.idempotencyKey }}</dd>
      </dl>

      <h4>Steps</h4>
      <ol class="steps">
        <li v-for="s in detail.data.value.steps" :key="s.step">
          <strong>{{ s.step }}</strong>
          <span v-if="s.at" class="muted"> <RelativeTime :value="s.at" /></span>
          <span v-if="s.detail"> · {{ s.detail }}</span>
        </li>
      </ol>

      <template v-if="detail.data.value.review">
        <h4>Review</h4>
        <p class="small">
          {{ detail.data.value.review.state }} · threshold {{ detail.data.value.review.threshold }}
          <template v-if="detail.data.value.review.reason"> · {{ detail.data.value.review.reason }}</template>
          <RouterLink
            v-if="detail.data.value.review.aiRequestId"
            :to="{ name: 'ai', query: { request: detail.data.value.review.aiRequestId } }"
          >
            · the AI request</RouterLink
          >
        </p>
        <table class="answers">
          <tr v-for="a in detail.data.value.review.answers" :key="a.key">
            <td>{{ a.label }}</td>
            <td class="bar-cell">
              <span
                class="bar"
                :class="{ refuses: a.refuses }"
                :style="{ width: `${Math.round(a.probability * 100)}%` }"
              />
            </td>
            <td class="mono small">{{ a.probability.toFixed(2) }}</td>
          </tr>
        </table>
      </template>

      <div class="section-head">
        <h4>Arguments</h4>
        <Button
          v-if="canManage && !isActive(call.state) && !detail.data.value.redacted"
          label="Redact…"
          size="small"
          text
          severity="danger"
          @click="redact"
        />
      </div>
      <p v-if="detail.data.value.redacted" class="muted">Redacted.</p>
      <template v-else>
        <pre class="box mono">{{ detail.data.value.arguments ?? 'none' }}</pre>
        <p v-if="detail.data.value.secureNames.length" class="small muted">
          Secret, never stored: {{ detail.data.value.secureNames.join(', ') }}
        </p>
        <p v-if="detail.data.value.note" class="note">{{ detail.data.value.note }}</p>
      </template>

      <template v-if="detail.data.value.run">
        <div class="section-head">
          <h4>Output</h4>
          <Button
            v-if="detail.data.value.run.output"
            label="Copy"
            icon="pi pi-copy"
            size="small"
            text
            @click="copy(detail.data.value.run.output, 'The output')"
          />
        </div>
        <pre class="box output mono">{{
          detail.data.value.run.output ?? (detail.data.value.run.run.finishedAt ? '(none)' : 'Running…')
        }}</pre>
      </template>
    </template>
    <template #footer>
      <Button
        v-if="canManage && call?.state === 'Queued'"
        label="Cancel the call…"
        severity="danger"
        text
        @click="cancel"
      />
      <span v-else-if="call?.state === 'Running'" class="muted small"
        >A running call can't be cancelled: the agents can't stop a run.</span
      >
      <Button label="Close" text severity="secondary" @click="emit('close')" />
    </template>
  </Dialog>
</template>

<style scoped>
.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
  margin-bottom: var(--pnmt-space-2);
}

.facts {
  margin: var(--pnmt-space-3) 0;
}

.steps {
  margin: 0;
  padding-left: var(--pnmt-space-4);
}

.answers {
  border-collapse: collapse;
  width: 100%;
  max-width: 36rem;
}

.answers td {
  padding: 2px var(--pnmt-space-2);
}

.bar-cell {
  width: 60%;
}

.bar {
  display: block;
  height: 0.6rem;
  border-radius: 3px;
  background: var(--p-primary-color);
  min-width: 2px;
}

.bar.refuses {
  background: var(--pnmt-fail);
}

.box {
  white-space: pre-wrap;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  max-height: 30vh;
  overflow: auto;
  font-size: 0.857rem;
  margin: 0;
}

.box.output {
  white-space: pre;
  min-height: 6rem;
  max-height: 50vh;
}

.note {
  white-space: pre-wrap;
}

.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
}

.section-head h4 {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
