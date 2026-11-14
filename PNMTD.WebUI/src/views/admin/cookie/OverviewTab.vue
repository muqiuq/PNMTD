<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatDuration } from '@/format'
import { usd } from '@/views/admin/ai/aiFormat'
import {
  channelLabel,
  channelName,
  channels,
  cookieStateSeverity,
  dutyName,
  outboxStateLabel,
  outboxStateName,
  outboxStateSeverity,
  outboxStates,
  overallLabel,
  overallSeverity,
  sourceLabel,
} from '@/views/cookie/cookieFormat'

/**
 * Admin → Cookie → Overview (docs/cookie-plan.md §9.3, §19.1): Start, Stop and Run now; the status and Cookie's own
 * state, its budget and its last rounds; how the case book keeps up; the duties of the last 7 days; and the outbox:
 * everything that went out, and what a limit held back, which a person can send from here. Every 5 s while Cookie works.
 */
const overview = useQuery({
  queryKey: [...keys.cookie, 'admin', 'overview'],
  queryFn: api.cookie.overview,
  refetchInterval: (q) => (q.state.data?.status.cookie.state === 'Working' ? 5_000 : 30_000),
})
const o = computed(() => overview.data.value)
const working = computed(() => o.value?.status.cookie.state === 'Working')
const rounds = useQuery({
  queryKey: [...keys.cookie, 'admin', 'rounds'],
  queryFn: api.cookie.rounds,
  refetchInterval: computed(() => (working.value ? 5_000 : 30_000)),
})
const share = computed(() =>
  o.value && o.value.budget.limit > 0
    ? Math.min(100, Math.round((o.value.budget.spent / o.value.budget.limit) * 100))
    : null,
)
const took = (r: S['CookieRoundItem']) =>
  r.endedAt ? formatDuration(new Date(r.endedAt).getTime() - new Date(r.startedAt).getTime()) : 'running'

const state = ref<S['CookieOutboxState'] | null>(null)
const channel = ref<S['CookieChannel'] | null>(null)
const filters = computed(() => ({ state: state.value ?? undefined, channel: channel.value ?? undefined }))
const table = useServerTable<S['CookieOutboxItem']>({
  key: [...keys.cookie, 'admin', 'outbox'],
  fetch: (q) => api.cookie.outbox(q),
  filters,
  pageSize: 25,
})
const expanded = ref<Record<string, boolean>>({})
const stateOptions = [
  { label: 'Every state', value: null },
  ...outboxStates.map((s) => ({ label: outboxStateLabel[s], value: s })),
]
const channelOptions = [
  { label: 'Every channel', value: null },
  ...channels.map((c) => ({ label: channelLabel[c], value: c })),
]

const { run, ask, busy } = useActions()
async function send(item: S['CookieOutboxItem']) {
  const sure = await ask(
    'Send it now?',
    `${channelLabel[item.channel]} to ${item.recipient || 'Redmine'}: “${item.subject}”. A limit held it back; sent by you, it goes out whatever the limits say.`,
    'Send',
  )
  if (!sure) return
  await run(() => api.cookie.send(item.id), {
    success: 'Sent',
    failure: 'Not sent',
    invalidate: [[...keys.cookie, 'admin']],
  })
}

/** Start, Stop and Run now (§9.3): the server says why it can't (not enabled in .env, or stopped). */
async function control(what: 'start' | 'stop' | 'run') {
  const labels = {
    start: ['Started', 'Not started'],
    stop: ['Stopped', 'Not stopped'],
    run: ['A round starts now', 'No round'],
  }
  await run(() => api.cookie[what](), {
    success: labels[what][0],
    failure: labels[what][1],
    invalidate: [keys.cookie],
  })
}
</script>

<template>
  <ErrorState
    v-if="overview.isError.value && !o"
    :error="overview.error.value"
    what="Cookie"
    @retry="overview.refetch()"
  />
  <div v-else-if="o" class="overview">
    <Message v-if="o.status.cookie.enabled && !o.hasGroup" severity="warn" :closable="false">
      Choose Cookie's group on the
      <RouterLink :to="{ name: 'cookie-admin', params: { tab: 'rights' } }">Rights</RouterLink> tab: Cookie pauses
      without one.
    </Message>
    <div class="two-columns">
      <section class="panel" aria-labelledby="cookie-status-title">
        <div class="panel-header">
          <span id="cookie-status-title">Status</span>
          <span class="end controls">
            <Button
              v-if="!o.status.cookie.running"
              label="Start"
              icon="pi pi-play"
              size="small"
              :loading="busy"
              @click="control('start')"
            />
            <template v-else>
              <Button
                label="Run now"
                icon="pi pi-refresh"
                size="small"
                severity="secondary"
                outlined
                :disabled="busy || working"
                @click="control('run')"
              />
              <Button
                label="Stop"
                icon="pi pi-stop"
                size="small"
                severity="secondary"
                :disabled="busy"
                @click="control('stop')"
              />
            </template>
          </span>
        </div>
        <dl class="panel-body facts">
          <dt>Overall</dt>
          <dd>
            <div class="line">
              <Tag :value="overallLabel[o.status.overall.state]" :severity="overallSeverity(o.status.overall.state)" />
              <span class="muted small"
                >{{ sourceLabel(o.status.overall.source)
                }}<template v-if="o.status.overall.at"> · <RelativeTime :value="o.status.overall.at" /></template
              ></span>
            </div>
            <div v-if="o.status.overall.reason">{{ o.status.overall.reason }}</div>
            <ul v-if="o.status.overall.cases.length" class="cases">
              <li v-for="c in o.status.overall.cases" :key="c.id">
                <RouterLink :to="{ name: 'cookie', query: { case: c.id } }">{{ c.title }}</RouterLink>
              </li>
            </ul>
          </dd>
          <template v-if="o.status.overall.source === 'cookie'">
            <dt>PNMT computes</dt>
            <dd>
              <Tag :value="overallLabel[o.codeState]" :severity="overallSeverity(o.codeState)" />
              {{ o.codeReason }}
            </dd>
          </template>
          <dt>Cookie</dt>
          <dd>
            <template v-if="o.status.cookie.enabled">
              <Tag :value="o.status.cookie.state" :severity="cookieStateSeverity(o.status.cookie.state)" />
              {{ o.status.cookie.reason ?? '' }}
            </template>
            <span v-else>Not enabled: {{ o.status.cookie.reason ?? 'Cookie__Enabled is not set.' }}</span>
          </dd>
          <dt>Last round</dt>
          <dd><RelativeTime :value="o.status.cookie.lastRoundAt" /></dd>
          <template v-if="o.status.cookie.nextRoundAt">
            <dt>Next round</dt>
            <dd><RelativeTime :value="o.status.cookie.nextRoundAt" /></dd>
          </template>
          <dt>Budget</dt>
          <dd>
            <template v-if="o.budget.limit > 0">
              {{ usd(o.budget.spent) }} of {{ usd(o.budget.limit) }} this month
              <ProgressBar :value="share ?? 0" :show-value="false" class="budget" />
            </template>
            <template v-else>{{ usd(o.budget.spent) }} this month, no limit</template>
          </dd>
          <dt>AI</dt>
          <dd>{{ o.aiUsable ? 'usable' : 'not usable: Admin → AI says why' }}</dd>
        </dl>
        <p class="panel-body muted small stopped-note">
          Stopped, Cookie writes nothing; the case book, the deadlines and the duties keep running. Stopping and
          starting are in the audit log.
        </p>
      </section>

      <section class="panel" aria-labelledby="cookie-book-title">
        <div id="cookie-book-title" class="panel-header">The case book</div>
        <div class="panel-body body">
          <Message v-if="o.caseBookError" severity="error" :closable="false" size="small">{{
            o.caseBookError
          }}</Message>
          <dl class="facts">
            <dt>Sensors and Zabbix</dt>
            <dd>checked <RelativeTime :value="o.caseBookAt" /></dd>
            <dt>Tickets</dt>
            <dd>checked <RelativeTime :value="o.ticketsAt" /></dd>
            <dt>Open</dt>
            <dd>{{ o.openProblems }} problem(s), {{ o.openTickets }} ticket(s)</dd>
            <dt>Duties due now</dt>
            <dd>{{ o.dutiesDue }}</dd>
            <dt>Held back today</dt>
            <dd>
              {{ o.heldBackToday }}
              <span v-if="o.heldBackToday" class="muted small">: a limit was hit; see the outbox below</span>
            </dd>
            <dt>Redmine</dt>
            <dd>
              <template v-if="!o.redmineConfigured">not configured</template>
              <template v-else-if="o.redmineWritable">configured, writable</template>
              <template v-else>configured, read only</template>
            </dd>
          </dl>
          <Message v-if="o.redmineConfigured && !o.redmineWritable" severity="warn" :closable="false" size="small">
            Tickets and notes need Redmine__WriteApiKey.
          </Message>
        </div>
      </section>
    </div>

    <section class="panel" aria-labelledby="cookie-rounds-title">
      <div class="panel-header">
        <span id="cookie-rounds-title">Last rounds</span>
        <RouterLink :to="{ name: 'cookie', params: { tab: 'trace' } }" class="end">The trace</RouterLink>
      </div>
      <ErrorState
        v-if="rounds.isError.value && !rounds.data.value"
        :error="rounds.error.value"
        what="the rounds"
        @retry="rounds.refetch()"
      />
      <DataTable v-else :value="rounds.data.value ?? []" size="small" data-key="id" :loading="!rounds.data.value">
        <template #empty><span class="muted">No round yet.</span></template>
        <Column header="Round" style="width: 6rem">
          <template #body="{ data }">
            <RouterLink :to="{ name: 'cookie', params: { tab: 'trace' }, query: { round: data.id } }"
              >#{{ data.id }}</RouterLink
            >
          </template>
        </Column>
        <Column header="Started" style="width: 8rem">
          <template #body="{ data }"><RelativeTime :value="data.startedAt" /></template>
        </Column>
        <Column header="Took" style="width: 6rem">
          <template #body="{ data }">{{ took(data) }}</template>
        </Column>
        <Column header="Why">
          <template #body="{ data }"
            ><span class="ellipsis cell" :title="data.trigger">{{ data.trigger }}</span></template
          >
        </Column>
        <Column header="Outcome">
          <template #body="{ data }">
            <span v-if="data.error" class="error-text" :title="data.error">{{ data.error }}</span>
            <span v-else class="ellipsis cell" :title="data.outcome">{{ data.outcome }}</span>
          </template>
        </Column>
        <Column header="Cases" field="cases" style="width: 5rem" />
        <Column header="Calls" style="width: 7rem">
          <template #body="{ data }">{{ data.calls }} · {{ data.toolCalls }} tools</template>
        </Column>
        <Column header="Cost" style="width: 6rem">
          <template #body="{ data }">{{ usd(data.cost) }}</template>
        </Column>
      </DataTable>
    </section>

    <section class="panel" aria-labelledby="cookie-duties-title">
      <div class="panel-header">
        <span id="cookie-duties-title">Duties</span> <span class="muted normal">(the last 7 days)</span>
        <RouterLink :to="{ name: 'cookie-admin', params: { tab: 'actions' } }" class="end">Switch on or off</RouterLink>
      </div>
      <DataTable :value="o.duties" size="small">
        <Column header="Duty">
          <template #body="{ data }">{{ dutyName(data.kind) }}</template>
        </Column>
        <Column header="" style="width: 5rem">
          <template #body="{ data }">
            <Tag :value="data.on ? 'on' : 'off'" :severity="data.on ? 'success' : 'secondary'" />
          </template>
        </Column>
        <Column field="due" header="Due" style="width: 6rem" />
        <Column field="done" header="Done" style="width: 6rem" />
        <Column field="heldBack" header="Held back" style="width: 6rem" />
        <Column field="off" header="Off" style="width: 6rem" />
        <Column field="failed" header="Failed" style="width: 6rem" />
      </DataTable>
    </section>

    <section class="outbox" aria-labelledby="cookie-outbox-title">
      <div class="outbox-head">
        <h2 id="cookie-outbox-title">Outbox</h2>
        <Select
          v-model="state"
          :options="stateOptions"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="State"
        />
        <Select
          v-model="channel"
          :options="channelOptions"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Channel"
        />
      </div>
      <p class="muted small hint">
        Every message, note and ticket, by PNMT's duties or by Cookie. What a limit held back can be sent from here.
      </p>
      <ListTable v-model:expanded="expanded" :table="table" what="messages" empty-text="Nothing went out yet." fixed>
        <Column expander style="width: 2.5rem" />
        <Column header="When" style="width: 7rem">
          <template #body="{ data }"><RelativeTime :value="data.createdAt" /></template>
        </Column>
        <Column header="Channel" style="width: 7rem">
          <template #body="{ data }">{{ channelName(data.channel) }}</template>
        </Column>
        <Column header="To" style="width: 10rem">
          <template #body="{ data }"
            ><span class="ellipsis cell">{{ data.recipient || '–' }}</span></template
          >
        </Column>
        <Column header="Subject">
          <template #body="{ data }"
            ><span class="ellipsis cell" :title="data.subject">{{ data.subject }}</span></template
          >
        </Column>
        <Column header="State" style="width: 12rem">
          <template #body="{ data }">
            <div class="line">
              <Tag :value="outboxStateName(data.state)" :severity="outboxStateSeverity(data.state)" />
              <Button
                v-if="data.state === 'HeldBack'"
                label="Send"
                icon="pi pi-send"
                size="small"
                severity="secondary"
                outlined
                :disabled="busy"
                @click.stop="send(data)"
              />
            </div>
          </template>
        </Column>
        <template #expansion="{ data }">
          <div class="details">
            <dl class="facts">
              <template v-if="data.detail">
                <dt>Detail</dt>
                <dd>{{ data.detail }}</dd>
              </template>
              <dt>By</dt>
              <dd>{{ data.source === 'cookie' ? 'Cookie' : 'PNMT' }}</dd>
              <template v-if="data.issueId">
                <dt>Ticket</dt>
                <dd>#{{ data.issueId }}</dd>
              </template>
              <template v-if="data.caseId">
                <dt>Case</dt>
                <dd>
                  <RouterLink :to="{ name: 'cookie', query: { case: data.caseId } }">Open the case</RouterLink>
                </dd>
              </template>
              <template v-if="data.sentAt">
                <dt>Sent</dt>
                <dd>
                  {{ formatAbsolute(data.sentAt) }}<template v-if="data.sentByName"> by {{ data.sentByName }}</template>
                </dd>
              </template>
            </dl>
            <pre class="text">{{ data.body }}</pre>
          </div>
        </template>
        <template #mobile="{ item }">
          <div class="line">
            <Tag :value="outboxStateName(item.state)" :severity="outboxStateSeverity(item.state)" />
            <span class="ellipsis">{{ item.subject }}</span>
          </div>
          <div class="muted small">
            {{ channelName(item.channel) }} · {{ item.recipient || '–' }} · <RelativeTime :value="item.createdAt" />
          </div>
          <Button
            v-if="item.state === 'HeldBack'"
            label="Send"
            icon="pi pi-send"
            size="small"
            severity="secondary"
            outlined
            :disabled="busy"
            @click.stop="send(item)"
          />
        </template>
      </ListTable>
    </section>
  </div>
</template>

<style scoped>
.overview {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.cases {
  margin: var(--pnmt-space-1) 0 0;
  padding-left: 1.2rem;
}

.normal {
  font-weight: 400;
}

.controls {
  display: flex;
  gap: var(--pnmt-space-2);
}

.budget {
  height: 6px;
  margin-top: var(--pnmt-space-1);
  max-width: 16rem;
}

.stopped-note {
  margin: 0;
  border-top: 1px solid var(--pnmt-border);
}

.error-text {
  color: var(--pnmt-fail);
}

.end {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
}

.outbox {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.outbox-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.outbox-head h2 {
  margin-right: var(--pnmt-space-2);
}

.hint {
  margin: 0;
}

.cell {
  display: block;
}

.details {
  display: grid;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) 0;
}

.text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: var(--pnmt-mono);
  font-size: 0.8rem;
  max-height: 30vh;
  overflow: auto;
}

.small {
  font-size: 0.857rem;
}
</style>
