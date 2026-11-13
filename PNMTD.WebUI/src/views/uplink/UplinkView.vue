<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import UptimeBar from '@/components/UptimeBar.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatDuration, formatPercent } from '@/format'
import { useSession } from '@/stores/session'

/**
 * The uplink check (docs/ai-operations-plan.md §7.3): its state, its configuration, each endpoint's numbers, the
 * last hours as a bar, the outages and every check. Every role reads it; an Admin checks now.
 */
const session = useSession()
const isAdmin = computed(() => session.can('Admin'))
const queryClient = useQueryClient()
const overview = useQuery({ queryKey: [...keys.uplink], queryFn: api.uplink.get, refetchInterval: 15_000 })
const o = computed(() => overview.data.value)
const outages = useQuery({
  queryKey: [...keys.uplink, 'outages'],
  queryFn: () => api.uplink.outages(7),
  refetchInterval: 60_000,
})

const show = ref<'all' | 'failures'>('all')
const filters = computed(() => ({ failuresOnly: show.value === 'failures' ? true : undefined }))
const checks = useServerTable<S['UplinkCheckItem']>({
  key: [...keys.uplink, 'checks'],
  fetch: (q) => api.uplink.checks(q),
  filters,
  refetchInterval: 15_000,
})

const outcomeLabel: Record<NonNullable<S['UplinkOutcome']>, string> = {
  Ok: 'OK',
  HttpStatus: 'HTTP error',
  Timeout: 'Timeout',
  ConnectError: 'No connection',
  Dns: 'DNS',
  Tls: 'TLS',
  BadAnswer: 'Bad answer',
  BadSignature: 'Wrong signature',
}
const label = (outcome: S['UplinkOutcome'] | undefined) => (outcome ? outcomeLabel[outcome] : '')

/** The week's hours as uptime fractions, oldest first; null for an hour without checks. */
const hours = computed(() => (o.value?.week ?? []).map((b) => (b.ok + b.failed ? b.ok / (b.ok + b.failed) : null)))
/** The day's median latency per half hour, as an SVG polyline. */
const latency = computed(() => {
  const points = (o.value?.day ?? []).map((b, i) => ({ i, ms: b.medianLatencyMs ?? null }))
  const values = points.filter((p) => p.ms !== null).map((p) => p.ms as number)
  if (values.length < 2) return null
  const max = Math.max(...values)
  const width = 480
  const height = 48
  const step = width / Math.max(1, points.length - 1)
  const line = points
    .filter((p) => p.ms !== null)
    .map((p) => `${(p.i * step).toFixed(1)},${(height - ((p.ms as number) / max) * (height - 4) - 2).toFixed(1)}`)
    .join(' ')
  return { line, width, height, max }
})

const { run, busy } = useActions()
async function checkNow() {
  const check = await run(() => api.uplink.check(), { failure: 'Not checked' })
  if (check) {
    await queryClient.invalidateQueries({ queryKey: [...keys.uplink] })
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Uplink">
      <template #actions>
        <Button
          v-if="isAdmin && o?.configured"
          label="Check now"
          icon="pi pi-refresh"
          size="small"
          :loading="busy"
          @click="checkNow"
        />
      </template>
    </PageHeader>

    <section v-if="o && !o.configured" class="panel">
      <div class="panel-header">Not configured</div>
      <div class="panel-body">
        <p>
          The uplink check asks two or three endpoints of your own, in turn, every 2 minutes, whether PNMTD reaches the
          internet. While it fails, the checks that need the internet pause, so that an outage of PNMTD's own line does
          not turn every sensor red. Add to <span class="mono">.env</span>:
        </p>
        <pre class="mono code">
UplinkCheck__SharedKey=…
UplinkCheck__Url1=https://…
UplinkCheck__Url2=https://…</pre>
      </div>
    </section>

    <template v-else-if="o">
      <section class="panel" aria-labelledby="state-title">
        <div id="state-title" class="panel-header">
          State
          <StatusBadge :status="o.up ? 'Ok' : 'Failing'" :label="o.up ? 'Up' : 'Down'" />
        </div>
        <div class="panel-body">
          <dl class="facts">
            <template v-if="!o.up && o.downSince">
              <dt>Down since</dt>
              <dd>{{ formatAbsolute(o.downSince) }} (<RelativeTime :value="o.downSince" />)</dd>
            </template>
            <dt>Failures in a row</dt>
            <dd>{{ o.failuresInARow }} of {{ o.failuresForDown }}</dd>
            <dt>Last check</dt>
            <dd><RelativeTime :value="o.lastCheck" /></dd>
            <dt>Last success</dt>
            <dd><RelativeTime :value="o.lastSuccess" /></dd>
            <dt>Next check</dt>
            <dd><RelativeTime :value="o.nextCheck" /></dd>
            <dt>Settings</dt>
            <dd>
              Every {{ formatDuration(o.intervalSeconds * 1000) }}, {{ o.timeoutSeconds }} s timeout, down after
              {{ o.failuresForDown }} failures in a row; the shared key is {{ o.sharedKeySet ? 'set' : 'not set' }}.
            </dd>
            <dt>While it is down</dt>
            <dd>
              {{ o.pausedTasks.join(', ') }} pause, and wait {{ o.recoveryWaitMinutes }} more minutes after it is back.
            </dd>
            <template v-if="o.sensorId">
              <dt>Sensor</dt>
              <dd>
                <RouterLink :to="{ name: 'sensor', params: { id: o.sensorId } }">Uplink</RouterLink>
                <span class="muted"> · its events, and notification rules for an outage</span>
              </dd>
            </template>
          </dl>
        </div>
      </section>

      <section class="panel" aria-labelledby="timeline-title">
        <div id="timeline-title" class="panel-header">The last 7 days, by hour</div>
        <div class="panel-body">
          <UptimeBar :values="hours" :minimum="48" />
          <div v-if="latency" class="latency">
            <span class="muted small">Median latency, the last 24 hours (up to {{ latency.max }} ms)</span>
            <svg
              :viewBox="`0 0 ${latency.width} ${latency.height}`"
              preserveAspectRatio="none"
              role="img"
              aria-label="Median latency"
            >
              <polyline :points="latency.line" fill="none" stroke="currentColor" stroke-width="1.5" />
            </svg>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="endpoints-title">
        <div id="endpoints-title" class="panel-header">Endpoints</div>
        <DataTable :value="o.endpoints" size="small" data-key="number">
          <Column header="#" style="width: 3rem">
            <template #body="{ data }">{{ data.number }}</template>
          </Column>
          <Column header="Address">
            <template #body="{ data }"
              ><span class="mono ellipsis">{{ data.address }}</span></template
            >
          </Column>
          <Column header="Last" style="width: 11rem">
            <template #body="{ data }">
              <Tag
                v-if="data.lastOutcome"
                :value="label(data.lastOutcome)"
                :severity="data.lastOutcome === 'Ok' ? 'success' : 'danger'"
              />
              <RelativeTime :value="data.lastAt" class="muted small" />
            </template>
          </Column>
          <Column header="24 h" style="width: 6rem">
            <template #body="{ data }">{{ formatPercent(data.successRate24h) }}</template>
          </Column>
          <Column header="7 days" style="width: 6rem">
            <template #body="{ data }">{{ formatPercent(data.successRate7d) }}</template>
          </Column>
          <Column header="Latency (median, p95)" style="width: 11rem">
            <template #body="{ data }"
              ><span v-if="data.medianLatency24h !== null" class="mono"
                >{{ data.medianLatency24h }} / {{ data.p95Latency24h }} ms</span
              ><span v-else class="muted">–</span></template
            >
          </Column>
        </DataTable>
      </section>

      <section class="panel" aria-labelledby="outages-title">
        <div id="outages-title" class="panel-header">Outages, the last 7 days</div>
        <div class="panel-body">
          <p v-if="!(outages.data.value ?? []).length" class="muted">None.</p>
          <ul v-else class="outages">
            <li v-for="x in outages.data.value" :key="x.start">
              <strong>{{ formatAbsolute(x.start) }}</strong>
              <span>{{ x.end ? `for ${formatDuration(x.durationSeconds * 1000)}` : 'ongoing' }}</span>
              <span class="muted small">endpoint {{ x.endpoints.join(', ') }} · {{ x.firstError ?? '' }}</span>
            </li>
          </ul>
        </div>
      </section>

      <section class="panel" aria-labelledby="checks-title">
        <div id="checks-title" class="panel-header">Checks</div>
        <FilterBar :active="show === 'failures' ? 1 : 0">
          <SelectButton
            v-model="show"
            :options="[
              { label: 'All', value: 'all' },
              { label: 'Failures', value: 'failures' },
            ]"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-label="Which checks"
          />
        </FilterBar>
        <ListTable :table="checks" what="checks">
          <Column header="When" style="width: 9rem">
            <template #body="{ data }"><RelativeTime :value="data.at" /></template>
          </Column>
          <Column header="Endpoint" style="width: 12rem">
            <template #body="{ data }"
              >{{ data.endpoint }} · <span class="mono">{{ data.host }}</span></template
            >
          </Column>
          <Column header="Outcome" style="width: 10rem">
            <template #body="{ data }">
              <Tag :value="label(data.outcome)" :severity="data.outcome === 'Ok' ? 'success' : 'danger'" />
            </template>
          </Column>
          <Column header="Latency" style="width: 6rem">
            <template #body="{ data }">{{ data.latencyMs !== null ? `${data.latencyMs} ms` : '–' }}</template>
          </Column>
          <Column header="Detail">
            <template #body="{ data }"
              ><span class="muted ellipsis">{{ data.error ?? '' }}</span></template
            >
          </Column>
          <template #mobile="{ item }">
            <div class="line">
              <Tag :value="label(item.outcome)" :severity="item.outcome === 'Ok' ? 'success' : 'danger'" />
              <span class="mono">{{ item.host }}</span>
              <RelativeTime :value="item.at" class="muted right" />
            </div>
            <div v-if="item.error" class="muted ellipsis">{{ item.error }}</div>
          </template>
        </ListTable>
      </section>
    </template>
  </div>
</template>

<style scoped>
.page > .panel + .panel {
  margin-top: var(--pnmt-space-3);
}
.panel-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.code {
  background: var(--pnmt-surface-2, transparent);
  padding: var(--pnmt-space-2);
  margin: 0;
  white-space: pre-wrap;
}
.latency {
  margin-top: var(--pnmt-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}
.latency svg {
  width: 100%;
  height: 48px;
  color: var(--pnmt-accent, currentColor);
}
.outages {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}
.outages li {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.right {
  margin-left: auto;
}
.small {
  font-size: 0.857rem;
}
</style>
