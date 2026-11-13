<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import Tag from 'primevue/tag'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import { usd } from './aiFormat'

/**
 * The AI's configuration as PNMTD reads it from .env (never the key), the budget of this period, the spending of the
 * last 30 days by purpose and model, and the queue (§3.2, §3.7, §16). Changing any of it is a change to .env.
 */
const status = useQuery({ queryKey: [...keys.ai, 'status'], queryFn: api.ai.status, refetchInterval: 15_000 })
const s = computed(() => status.data.value)
const percent = computed(() => (s.value?.budget.share == null ? null : Math.round(s.value.budget.share * 100)))

// The unmatched mails of the last days (§6.2): counted first, then queued, one request each.
const days = ref(7)
const counted = ref<S['MailBackfillResult'] | null>(null)
const queued = ref<S['MailBackfillResult'] | null>(null)
const { run, busy } = useActions()
async function count() {
  queued.value = null
  counted.value =
    (await run(() => api.ai.backfill({ days: days.value, dryRun: true }), { failure: 'Not counted' })) ?? null
}
async function analyse() {
  queued.value =
    (await run(() => api.ai.backfill({ days: days.value, dryRun: false }), { failure: 'Not queued' })) ?? null
  counted.value = null
}
</script>

<template>
  <div v-if="s" class="settings">
    <Message v-for="w in s.warnings" :key="w" severity="warn" :closable="false" size="small">{{ w }}</Message>
    <div class="two-columns">
      <section class="panel" aria-labelledby="config-title">
        <div id="config-title" class="panel-header">
          Configuration
          <Tag
            :value="s.enabled && s.keySet ? 'on' : 'off'"
            :severity="s.enabled && s.keySet ? 'success' : 'secondary'"
          />
        </div>
        <dl class="panel-body facts">
          <dt>Key</dt>
          <dd>{{ s.keySet ? 'set' : 'not set (Ai__ApiKey)' }}</dd>
          <dt>Provider</dt>
          <dd class="mono">{{ s.baseUrl }}</dd>
          <dt>Main model</dt>
          <dd class="mono">{{ s.mainModel }}</dd>
          <dt>Verification model</dt>
          <dd class="mono">{{ s.verifyModel }}</dd>
          <dt>Guard</dt>
          <dd>
            <span class="mono">{{ s.guardModel }}</span> at <span class="mono">{{ s.guardUrl }}</span>; blocks from a
            probability of {{ s.guardThreshold
            }}<template v-if="s.guardRequired">, required for untrusted text</template>
          </dd>
          <dt>Data collection</dt>
          <dd>{{ s.dataCollection }}</dd>
          <dt>Request log</dt>
          <dd>the newest {{ s.logEntries.toLocaleString() }}</dd>
        </dl>
      </section>
      <section class="panel" aria-labelledby="budget-title">
        <div id="budget-title" class="panel-header">Budget</div>
        <div class="panel-body body">
          <template v-if="s.budget.usd > 0">
            <p>
              {{ usd(s.budget.spent) }} of {{ usd(s.budget.usd) }} this {{ s.budget.period }} ({{
                s.budget.periodKey
              }}), until {{ formatAbsolute(s.budget.periodEndsAt) }}.
            </p>
            <ProgressBar :value="Math.min(100, percent ?? 0)" :show-value="true" />
            <p class="muted small">
              Warnings at 80 % and 90 % (the AI budget sensor); at 100 % AI work waits for the next
              {{ s.budget.period }}.
            </p>
          </template>
          <p v-else>No limit (Ai__Budget__Usd=0).</p>
          <dl class="facts">
            <dt>Today</dt>
            <dd>{{ usd(s.spending.today) }} · {{ s.spending.requestsToday }} request(s)</dd>
            <dt>Last 30 days</dt>
            <dd>{{ usd(s.spending.last30Days) }} · {{ s.spending.requests30Days }} request(s)</dd>
            <dt>Queue</dt>
            <dd>
              {{ s.queue.queued }} queued, {{ s.queue.running }} running, {{ s.queue.waitingRetry }} waiting to retry,
              {{ s.queue.waitingBudget }} waiting for the budget; {{ s.queue.failed24Hours }} failed in 24 hours
            </dd>
          </dl>
        </div>
      </section>
    </div>
    <section class="panel" aria-labelledby="backfill-title">
      <div id="backfill-title" class="panel-header">Unmatched mails</div>
      <div class="panel-body body">
        <p class="muted small">
          New mails no rule handles go to the AI after a few minutes. Mails from before AI was on can be analysed too:
          one request each, except those like a mail that already waits for a decision.
        </p>
        <div class="backfill">
          <label for="backfill-days">Of the last</label>
          <InputNumber
            v-model="days"
            input-id="backfill-days"
            :min="1"
            :max="365"
            suffix=" days"
            :use-grouping="false"
          />
          <Button label="Count" size="small" severity="secondary" :loading="busy" @click="count" />
          <Button
            v-if="counted && counted.mails"
            :label="`Analyse ${counted.mails} mail(s)`"
            size="small"
            :loading="busy"
            @click="analyse"
          />
        </div>
        <p v-if="counted">{{ counted.mails }} unmatched mail(s) the AI has not seen.</p>
        <p v-if="queued">{{ queued.queued }} queued, {{ queued.attached }} attached to a waiting decision.</p>
      </div>
    </section>
    <div class="two-columns">
      <section class="panel" aria-labelledby="purpose-title">
        <div id="purpose-title" class="panel-header">Spending by purpose, 30 days</div>
        <DataTable :value="s.spending.byPurpose" size="small" data-key="name">
          <Column field="name" header="Purpose" />
          <Column field="requests" header="Requests" style="width: 7rem" />
          <Column header="Cost" style="width: 8rem">
            <template #body="{ data }">{{ usd(data.cost) }}</template>
          </Column>
        </DataTable>
      </section>
      <section class="panel" aria-labelledby="model-title">
        <div id="model-title" class="panel-header">Spending by model, 30 days</div>
        <DataTable :value="s.spending.byModel" size="small" data-key="name">
          <Column header="Model">
            <template #body="{ data }"
              ><span class="mono">{{ data.name }}</span></template
            >
          </Column>
          <Column field="requests" header="Requests" style="width: 7rem" />
          <Column header="Cost" style="width: 8rem">
            <template #body="{ data }">{{ usd(data.cost) }}</template>
          </Column>
        </DataTable>
      </section>
    </div>
  </div>
</template>

<style scoped>
.settings {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
.panel-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}
.small {
  font-size: 0.857rem;
}
.backfill {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}
</style>
