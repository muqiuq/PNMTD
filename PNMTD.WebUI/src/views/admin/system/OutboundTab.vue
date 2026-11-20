<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, onScopeDispose, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatTime } from '@/format'
import { formatBytes } from '@/format/bytes'
import OutboundDialog from './OutboundDialog.vue'
import {
  channelIcon,
  channelLabel,
  formatMs,
  laneLabel,
  outcomeSeverity,
  queueLine,
  type OutboundLogName,
} from './outboundFormat'

/**
 * *System health → Outbound* (docs/redmine-queue-outbound-plan.md §7.2). On top, *Running now* from `/live` every 2 s: the
 * operations in flight with a running timer, the Redmine queue per lane and its jobs, and a warning if records were dropped.
 * Below, one of the two logs, newest first, 50 to a page: the notifications (e-mail, Pushover, HTTP) or the Redmine calls.
 * The log, its filters, the search and the open record (`?op=`) are in the URL. Page 1 polls every 3 s, unless *Pause* is on
 * or a record is open; other pages don't; new rows are marked for a moment. A row opens its details (OutboundDialog, §7.3).
 */
type Row = S['OutboundNotificationItem'] | S['RedmineCallItem']

const route = useRoute()
const router = useRouter()
const text = (k: string) => (typeof route.query[k] === 'string' ? (route.query[k] as string) : '')

const log = ref<OutboundLogName>(text('log') === 'redmine' ? 'redmine' : 'notifications')
const channel = ref<S['OutboundChannel'] | null>((text('channel') as S['OutboundChannel']) || null)
const kind = ref<S['RedmineCallKind'] | null>((text('kind') as S['RedmineCallKind']) || null)
const lane = ref<S['RedmineLane'] | null>((text('lane') as S['RedmineLane']) || null)
const outcome = ref<S['OutboundOutcome'] | null>((text('outcome') as S['OutboundOutcome']) || null)
const job = ref<string | null>(text('job') || null)
const op = ref<number | null>(Number(text('op')) || null)
const paused = ref(false)

// A log's own filters go when the other log is shown; the outcome and the search apply to both.
watch(log, (l) => {
  if (l === 'redmine') channel.value = null
  else kind.value = lane.value = job.value = null
})
const filters = computed(() =>
  log.value === 'redmine'
    ? {
        log: log.value,
        kind: kind.value ?? undefined,
        lane: lane.value ?? undefined,
        outcome: outcome.value ?? undefined,
        job: job.value ?? undefined,
      }
    : { log: log.value, channel: channel.value ?? undefined, outcome: outcome.value ?? undefined },
)
const active = computed(
  () => [channel.value, kind.value, lane.value, outcome.value, job.value].filter((x) => x !== null).length,
)

const onFirstPage = ref(true)
const table = useServerTable<Row>({
  key: [...keys.outbound, 'log'],
  fetch: ({ log: which, ...query }) =>
    which === 'redmine' ? api.system.outbound.redmine(query) : api.system.outbound.notifications(query),
  filters,
  pageSize: 50,
  syncUrl: true,
  refetchInterval: computed(() => (paused.value || op.value !== null || !onFirstPage.value ? 0 : 3000)),
})
watch(table.page, (p) => (onFirstPage.value = p === 1), { immediate: true })
const liveText = computed(() =>
  paused.value ? 'Paused.' : onFirstPage.value ? 'Live: new records every 3 s.' : 'Live on the first page only.',
)

/**
 * The whole query in one place, after the table wrote its page and search (flush: 'post'), so the last navigation has
 * everything: two replaces in one tick keep only the second.
 */
watch(
  [log, channel, kind, lane, outcome, job, op, table.params],
  () => {
    const p = table.params.value
    const redmine = log.value === 'redmine'
    const query: Record<string, string> = {}
    for (const [k, v] of Object.entries({
      log: redmine ? 'redmine' : '',
      channel: redmine ? '' : (channel.value ?? ''),
      kind: redmine ? (kind.value ?? '') : '',
      lane: redmine ? (lane.value ?? '') : '',
      outcome: outcome.value ?? '',
      job: redmine ? (job.value ?? '') : '',
      q: p.search ?? '',
      page: p.page > 1 ? String(p.page) : '',
      pageSize: p.pageSize !== 50 ? String(p.pageSize) : '',
      op: op.value ? String(op.value) : '',
    }))
      if (v) query[k] = v
    void router.replace({ query })
  },
  { flush: 'post' },
)

// New rows on page 1 are marked for a moment; not the first rows of a log or of other filters.
const fresh = ref(new Set<number>())
let seen: { key: string; newest: number } | null = null
let freshTimer: ReturnType<typeof setTimeout> | undefined
watch(
  () => table.query.data.value,
  (data) => {
    if (!data || table.query.isPlaceholderData.value) return
    const key = JSON.stringify({ ...table.params.value, page: undefined })
    const newest = data.items.reduce((max, i) => Math.max(max, i.id), 0)
    if (seen?.key === key) {
      const before = seen.newest
      const added = data.page === 1 ? data.items.filter((i) => i.id > before).map((i) => i.id) : []
      if (added.length) {
        fresh.value = new Set(added)
        clearTimeout(freshTimer)
        freshTimer = setTimeout(() => (fresh.value = new Set()), 3000)
      }
      seen = { key, newest: Math.max(before, newest) }
    } else {
      seen = { key, newest }
    }
  },
)
onScopeDispose(() => clearTimeout(freshTimer))
const rowClass = (row: Row) => (fresh.value.has(row.id) ? 'fresh' : undefined)
const ids = computed(() => table.items.value.map((i) => i.id))

function showJob(id: string) {
  op.value = null
  log.value = 'redmine'
  kind.value = lane.value = outcome.value = null
  job.value = id
  table.search.value = ''
}

// --- running now ---------------------------------------------------------------------------------------------------
const live = useQuery({
  queryKey: [...keys.outbound, 'live'],
  queryFn: api.system.outbound.live,
  refetchInterval: 2000,
})
const running = computed(() => live.data.value?.running ?? [])
const queue = computed(() => live.data.value?.redmine ?? null)
const dropped = computed(() => {
  const l = live.data.value
  if (!l || l.droppedNotifications + l.droppedRedmine === 0) return null
  return [
    l.droppedNotifications ? `${l.droppedNotifications.toLocaleString()} notifications` : '',
    l.droppedRedmine ? `${l.droppedRedmine.toLocaleString()} Redmine calls` : '',
  ]
    .filter(Boolean)
    .join(' and ')
})
// The running timers tick while something runs.
const now = ref(Date.now())
let ticker: ReturnType<typeof setInterval> | undefined
watch(
  () => running.value.length + (queue.value?.jobs.length ?? 0) > 0,
  (busy) => {
    clearInterval(ticker)
    ticker = undefined
    if (busy) {
      now.value = Date.now()
      ticker = setInterval(() => (now.value = Date.now()), 500)
    }
  },
  { immediate: true },
)
onScopeDispose(() => clearInterval(ticker))
const since = (at: string | null) => (at ? formatMs(now.value - Date.parse(at)) : '')

const logs = [
  { label: 'Notifications', value: 'notifications' },
  { label: 'Redmine', value: 'redmine' },
]
const channels = [
  { label: 'E-mail', value: 'Email' },
  { label: 'Pushover', value: 'Pushover' },
  { label: 'HTTP', value: 'Http' },
]
const kinds: S['RedmineCallKind'][] = ['Read', 'Write']
const lanes = (['Button', 'Write', 'Interactive', 'Background'] as S['RedmineLane'][]).map((l) => ({
  label: laneLabel(l),
  value: l,
}))
const outcomes: S['OutboundOutcome'][] = ['Ok', 'Failed', 'Simulated', 'Cancelled']
const size = (r: Row) => `Sent ${formatBytes(r.requestBytes)}, received ${formatBytes(r.responseBytes)}`
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="running-title">
      <div id="running-title" class="panel-header">
        Running now
        <span v-if="queue" class="muted queue">{{ queueLine(queue) }}</span>
      </div>
      <div class="panel-body live">
        <p v-if="!running.length" class="muted none">Nothing in flight.</p>
        <ul v-else class="lines">
          <li v-for="(r, i) in running" :key="`${i}-${r.startedAt}-${r.target}`">
            <i :class="channelIcon(r.channel)" aria-hidden="true" />
            <strong>{{ channelLabel(r.channel) }}</strong>
            <span class="mono ellipsis" :title="r.target">{{ r.method }} {{ r.target }}</span>
            <span class="muted ellipsis">{{ r.purpose }} · {{ r.askedBy }}</span>
            <span class="mono timer">{{ since(r.startedAt) }}</span>
          </li>
        </ul>
        <template v-if="queue">
          <p class="muted small none">
            {{ queue.slotsInUse }} of {{ queue.slots }} slots in use (Redmine__Parallelism)
            <template v-if="queue.backgroundPausedUntil">
              · background paused until {{ formatTime(queue.backgroundPausedUntil) }} (Redmine asked to wait)</template
            >
          </p>
          <ul v-if="queue.jobs.length" class="lines jobs" aria-label="Redmine jobs">
            <li v-for="j in queue.jobs" :key="j.id">
              <Tag :value="laneLabel(j.lane)" severity="secondary" class="small-tag" />
              <span class="ellipsis">{{ j.purpose }}</span>
              <span v-if="j.key" class="mono muted ellipsis">{{ j.key }}</span>
              <span class="muted ellipsis">{{ j.askedBy }}</span>
              <span class="muted small timer">
                <template v-if="j.started">
                  running {{ since(j.startedAt) }} · {{ j.callsDone }} calls done, {{ j.callsRunning }} in flight
                </template>
                <template v-else>queued {{ since(j.queuedAt) }}</template>
              </span>
            </li>
          </ul>
        </template>
        <Message v-if="dropped" severity="warn" :closable="false" size="small">
          Not recorded since the start, because the log's queue was full: {{ dropped }}.
        </Message>
      </div>
    </section>

    <div class="toolbar">
      <SelectButton
        v-model="log"
        :options="logs"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Log"
      />
      <span class="muted small">{{ liveText }}</span>
    </div>
    <FilterBar
      v-model:search="table.search.value"
      :placeholder="log === 'redmine' ? 'Path, summary, purpose or who' : 'Recipient, subject, purpose or who'"
      :active="active"
    >
      <Select
        v-if="log === 'notifications'"
        v-model="channel"
        :options="channels"
        option-label="label"
        option-value="value"
        placeholder="Any channel"
        show-clear
        size="small"
        aria-label="Channel"
      />
      <template v-else>
        <Select
          v-model="kind"
          :options="kinds"
          placeholder="Reads and writes"
          show-clear
          size="small"
          aria-label="Kind"
        />
        <Select
          v-model="lane"
          :options="lanes"
          option-label="label"
          option-value="value"
          placeholder="Any lane"
          show-clear
          size="small"
          aria-label="Lane"
        />
      </template>
      <Select
        v-model="outcome"
        :options="outcomes"
        placeholder="Any outcome"
        show-clear
        size="small"
        aria-label="Outcome"
      />
      <Button
        v-if="log === 'redmine' && job"
        :label="`Job ${job.slice(0, 8)}`"
        icon="pi pi-times"
        icon-pos="right"
        size="small"
        severity="secondary"
        outlined
        :aria-label="`Job ${job.slice(0, 8)}: show every job's calls`"
        @click="job = null"
      />
      <template #end>
        <label class="switch"><ToggleSwitch v-model="paused" /> Pause</label>
      </template>
    </FilterBar>

    <ListTable
      :table="table"
      :what="log === 'redmine' ? 'Redmine calls' : 'notifications'"
      :empty-text="log === 'redmine' ? 'No Redmine call recorded yet.' : 'No notification recorded yet.'"
      :row-class="rowClass"
      selectable
      fixed
      @select="(r) => (op = r.id)"
    >
      <Column header="Time" style="width: 7rem">
        <template #body="{ data }"><RelativeTime :value="data.startedAt" /></template>
      </Column>
      <Column v-if="log === 'notifications'" header="Channel" style="width: 7rem">
        <template #body="{ data }">
          <span class="nowrap"
            ><i :class="channelIcon(data.channel)" aria-hidden="true" /> {{ channelLabel(data.channel) }}</span
          >
        </template>
      </Column>
      <Column v-else header="Kind" style="width: 8rem">
        <template #body="{ data }">
          <span class="nowrap"
            >{{ data.kind }} <span class="mono muted small">{{ data.method }}</span></span
          >
          <div class="muted small">{{ laneLabel(data.lane) }}</div>
        </template>
      </Column>
      <Column :header="log === 'redmine' ? 'Target' : 'Recipient'">
        <template #body="{ data }">
          <span class="mono small ellipsis cell" :title="data.recipient ?? data.target">{{
            data.recipient ?? data.target
          }}</span>
        </template>
      </Column>
      <Column header="Summary">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.summary">{{ data.summary }}</span></template
        >
      </Column>
      <Column header="Purpose" style="width: 13rem">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.purpose">{{ data.purpose }}</span></template
        >
      </Column>
      <Column header="Asked by" style="width: 9rem">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.askedBy">{{ data.askedBy }}</span></template
        >
      </Column>
      <Column header="Outcome" style="width: 8rem">
        <template #body="{ data }">
          <Tag :value="data.outcome" :severity="outcomeSeverity(data.outcome)" />
          <span v-if="data.statusCode !== null" class="mono small"> {{ data.statusCode }}</span>
        </template>
      </Column>
      <Column header="Duration" style="width: 6.5rem">
        <template #body="{ data }">
          <span class="small">{{ formatMs(data.durationMs) }}</span>
          <div v-if="log === 'redmine' && data.queuedMs" class="muted small">waited {{ formatMs(data.queuedMs) }}</div>
        </template>
      </Column>
      <Column header="Size" style="width: 7.5rem">
        <template #body="{ data }">
          <span class="small nowrap" :title="size(data)"
            >↑ {{ formatBytes(data.requestBytes) }}<br />↓ {{ formatBytes(data.responseBytes) }}</span
          >
          <Tag v-if="data.truncated" value="cut" severity="warn" class="small-tag" />
        </template>
      </Column>
      <Column style="width: 5.5rem">
        <template #body="{ data }">
          <Button label="Details" text size="small" @click.stop="op = data.id" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="small mobile-line">
          <Tag :value="item.outcome" :severity="outcomeSeverity(item.outcome)" />
          <span v-if="'channel' in item"
            ><i :class="channelIcon(item.channel)" aria-hidden="true" /> {{ channelLabel(item.channel) }}</span
          >
          <span v-else>{{ item.kind }} · {{ laneLabel(item.lane) }}</span>
          <RelativeTime :value="item.startedAt" class="muted" />
        </div>
        <div class="ellipsis">{{ item.summary }}</div>
      </template>
    </ListTable>
    <p class="muted small">
      The newest records of each log are kept (Outbound__KeepNotifications, Outbound__KeepRedmine); keys and tokens are
      masked before they are stored.
    </p>
    <OutboundDialog :log="log" :record-id="op" :ids="ids" @close="op = null" @open="(id) => (op = id)" @job="showJob" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.queue {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.857rem;
}

.live {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.none {
  margin: 0;
}

.lines {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.lines li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.lines .timer {
  margin-left: auto;
  white-space: nowrap;
}

.jobs {
  font-size: 0.857rem;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  flex-wrap: wrap;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  white-space: nowrap;
}

.cell {
  display: block;
}

.mobile-line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small-tag {
  font-size: 0.75rem;
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}

.tab :deep(tr.fresh),
.tab :deep(li.fresh) {
  animation: fresh 3s ease-out;
}

@keyframes fresh {
  from {
    background: color-mix(in srgb, var(--p-primary-color) 22%, transparent);
  }
  to {
    background: transparent;
  }
}
</style>
