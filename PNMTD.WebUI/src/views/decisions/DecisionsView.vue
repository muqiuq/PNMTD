<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatPercent } from '@/format'
import { useSession } from '@/stores/session'
import RuleEditForm from './RuleEditForm.vue'

/**
 * Monitoring → Decisions (docs/ai-operations-plan.md §5.3): what PNMT asks a person, open ones first. A decision shows
 * its proposal and its evidence: the mail (untrusted text, shown as text), what the guard, the scan and the verification
 * model said, and for a rule what it would match. Nothing durable happens without a choice here.
 */
const session = useSession()
const route = useRoute()
const router = useRouter()
const queryClient = useQueryClient()
const show = ref<'Open' | 'all'>('Open')
const filters = computed(() => ({ state: show.value === 'Open' ? ('Open' as const) : undefined }))
const table = useServerTable<S['DecisionItem']>({
  key: [...keys.decisions, 'list'],
  fetch: (q) => api.decisions.list(q),
  filters,
  refetchInterval: 30_000,
})
const selected = computed(() => (typeof route.params.id === 'string' && route.params.id ? route.params.id : null))
const select = (d: S['DecisionItem']) => router.replace({ name: 'decisions', params: { id: d.id } })
const detail = useQuery({
  queryKey: computed(() => [...keys.decisions, 'detail', selected.value]),
  queryFn: () => api.decisions.get(selected.value!),
  enabled: computed(() => !!selected.value),
})
const d = computed(() => detail.data.value)
const editing = ref(false)
watch(selected, () => (editing.value = false))

const kindLabel: Record<string, string> = {
  'mail.rule': 'Mail rule',
  'mail.suspicious': 'Suspicious mail',
  'mail.outcome': 'Mail outcome',
  'analysis.finding': 'Finding',
}
const stateSeverity = (s: S['DecisionState']) =>
  s === 'Open' ? 'warn' : s === 'Accepted' ? 'success' : s === 'Failed' ? 'danger' : 'secondary'

const { run, busy } = useActions()
async function decide(choice: string, rule?: S['MailInputRuleRequest']) {
  if (!selected.value) return
  const result = await run(() => api.decisions.decide(selected.value!, { choice, rule: rule ?? null }), {
    success: 'Decided',
    failure: 'Not decided',
  })
  if (!result) return
  editing.value = false
  await queryClient.invalidateQueries({ queryKey: [...keys.decisions] })
  await queryClient.invalidateQueries({ queryKey: [...keys.mail] })
}
function choose(option: S['DecisionOption']) {
  if (option.edit) editing.value = true
  else void decide(option.id)
}
</script>

<template>
  <div class="page">
    <PageHeader title="Decisions" />
    <div class="layout">
      <div class="list">
        <FilterBar v-model:search="table.search.value" placeholder="Search" :active="show === 'Open' ? 0 : 1">
          <SelectButton
            v-model="show"
            :options="[
              { label: 'Open', value: 'Open' },
              { label: 'All', value: 'all' },
            ]"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-label="Which decisions"
          />
        </FilterBar>
        <ListTable :table="table" what="decisions" selectable empty-text="Nothing to decide." @select="select">
          <Column header="Decision">
            <template #body="{ data }">
              <div class="line">
                <Tag :value="kindLabel[data.kind] ?? data.kind" severity="secondary" />
                <Tag v-if="data.state !== 'Open'" :value="data.state" :severity="stateSeverity(data.state)" />
                <Tag v-if="data.disputed" value="disputed" severity="warn" />
                <span v-if="data.occurrences > 1" class="muted small">×{{ data.occurrences }}</span>
              </div>
              <div :class="{ strong: data.id === selected }" class="ellipsis">{{ data.title }}</div>
              <RelativeTime :value="data.lastSeenAt" class="muted small" />
            </template>
          </Column>
          <template #mobile="{ item }">
            <div class="ellipsis">{{ item.title }}</div>
            <div class="muted small">{{ kindLabel[item.kind] ?? item.kind }} · {{ item.state }}</div>
          </template>
        </ListTable>
      </div>

      <section class="panel detail" aria-labelledby="decision-title">
        <EmptyState v-if="!selected" icon="pi pi-inbox" title="Choose a decision." />
        <template v-else-if="d">
          <div id="decision-title" class="panel-header">
            <Tag :value="kindLabel[d.item.kind] ?? d.item.kind" severity="secondary" />
            <Tag :value="d.item.state" :severity="stateSeverity(d.item.state)" />
            <Tag v-if="d.item.disputed" value="disputed" severity="warn" />
          </div>
          <div class="panel-body body">
            <h2 class="title">{{ d.item.title }}</h2>
            <p>{{ d.item.summary }}</p>
            <p class="muted small">
              First {{ formatAbsolute(d.item.createdAt) }}, last {{ formatAbsolute(d.item.lastSeenAt) }}
              <template v-if="d.item.occurrences > 1"> · {{ d.item.occurrences }} times</template>
              <template v-if="d.item.decidedAt">
                · decided {{ formatAbsolute(d.item.decidedAt) }} by {{ d.item.decidedByName ?? '?' }}:
                {{ d.item.choice }}</template
              >
            </p>
            <Message v-if="d.item.disputed" severity="warn" :closable="false" size="small">
              The verification model disagreed: {{ d.ai?.issues.join(' · ') || 'no reason given' }}
            </Message>
            <Message v-if="d.error" severity="error" :closable="false" size="small">{{ d.error }}</Message>

            <template v-if="d.mailRule">
              <h3 class="sub">The proposed rule</h3>
              <dl class="facts">
                <dt>Does</dt>
                <dd>
                  {{ d.mailRule.action }} · {{ d.mailRule.kind }} · confidence
                  {{ formatPercent(d.mailRule.confidence, 0) }}
                </dd>
                <dt>Sender</dt>
                <dd class="mono">{{ d.mailRule.fromTest }}</dd>
                <dt>Subject</dt>
                <dd class="mono">{{ d.mailRule.subjectTest ?? '–' }}</dd>
                <dt>OK test</dt>
                <dd class="mono">{{ d.mailRule.okTest ?? '–' }}</dd>
                <dt>FAIL test</dt>
                <dd class="mono">{{ d.mailRule.failTest ?? '–' }}</dd>
                <dt>Otherwise</dt>
                <dd>{{ d.mailRule.defaultOutcome === 'fail' ? 'failure' : 'OK' }}</dd>
                <dt>Sensor</dt>
                <dd>
                  <RouterLink v-if="d.mailRule.sensorId" :to="{ name: 'sensor', params: { id: d.mailRule.sensorId } }"
                    >{{ d.mailRule.hostName }} · {{ d.mailRule.sensorName }}</RouterLink
                  >
                  <span v-else-if="d.mailRule.newSensorName">a new sensor, “{{ d.mailRule.newSensorName }}”</span>
                  <span v-else class="muted">none</span>
                </dd>
                <template v-if="d.mailRule.existingRuleName">
                  <dt>Improves</dt>
                  <dd>the rule {{ d.mailRule.existingRuleName }}</dd>
                </template>
                <dt>Would match</dt>
                <dd>
                  {{ d.mailRule.preview.matches }} of the last {{ d.mailRule.preview.checked }} mails,
                  {{ d.mailRule.preview.otherSenders }} other sender(s)
                  <Tag v-if="d.mailRule.preview.tooBroad" value="too broad" severity="danger" />
                  <ul v-if="d.mailRule.preview.examples.length" class="examples">
                    <li v-for="e in d.mailRule.preview.examples" :key="e" class="muted small">{{ e }}</li>
                  </ul>
                </dd>
              </dl>
              <p class="muted small">{{ d.mailRule.reasoning }}</p>
            </template>

            <template v-if="d.finding">
              <h3 class="sub">The finding</h3>
              <p>{{ d.finding.explanation }}</p>
              <p><strong>Recommended:</strong> {{ d.finding.recommendation }}</p>
              <p class="muted small">
                {{ d.finding.kind }} · {{ d.finding.severity }} · confidence
                {{ formatPercent(d.finding.confidence, 0) }}
                <template v-if="d.finding.verified"> · confirmed by the verification model</template>
              </p>
              <p v-if="d.finding.sensors.length">
                Sensors:
                <template v-for="(s, i) in d.finding.sensors" :key="s.id"
                  ><template v-if="i">, </template
                  ><RouterLink :to="{ name: 'sensor', params: { id: s.id } }">{{ s.name }}</RouterLink></template
                >
              </p>
            </template>

            <template v-if="d.mail">
              <h3 class="sub">The mail</h3>
              <dl class="facts">
                <dt>From</dt>
                <dd>{{ d.mail.from }}</dd>
                <dt>Subject</dt>
                <dd>{{ d.mail.subject }}</dd>
              </dl>
              <pre class="mail mono">{{ d.mail.body }}</pre>
            </template>

            <template v-if="d.ai">
              <h3 class="sub">What the checks said</h3>
              <ul class="checks small">
                <li v-if="d.ai.blockedBy">Stopped by the {{ d.ai.blockedBy }}: no model read it.</li>
                <li v-if="d.ai.injection !== null && d.ai.injection !== undefined">
                  The guard's injection probability: {{ d.ai.injection.toFixed(2) }}
                </li>
                <li v-if="d.ai.flags.length">The scan: {{ d.ai.flags.join(', ') }}</li>
                <li v-if="d.ai.verdict">The verification model: {{ d.ai.verdict }}</li>
                <li v-if="session.can('Admin') && d.ai.requestIds.length">
                  <RouterLink :to="{ name: 'ai', params: { tab: 'requests' } }"
                    >{{ d.ai.requestIds.length }} AI request(s)</RouterLink
                  >
                </li>
              </ul>
            </template>

            <RuleEditForm
              v-if="editing && d.mailRule"
              :proposal="d.mailRule"
              :busy="busy"
              @create="(rule) => decide('edit', rule)"
              @cancel="editing = false"
            />
            <div v-else-if="d.options.length" class="options">
              <Button
                v-for="o in d.options"
                :key="o.id"
                :label="o.label"
                :severity="o.id === 'reject' || o.id === 'dismiss' ? 'secondary' : undefined"
                :outlined="o.id !== 'create' && o.id !== 'update-rule' && o.id !== 'ok' && o.id !== 'acknowledge'"
                :disabled="!o.enabled || busy"
                :title="o.description"
                size="small"
                @click="choose(o)"
              />
            </div>
            <p v-if="d.options.some((o) => !o.enabled)" class="muted small">
              {{
                d.options
                  .filter((o) => !o.enabled)
                  .map((o) => `${o.label}: ${o.description}`)
                  .join(' · ')
              }}
            </p>
          </div>
        </template>
      </section>
    </div>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(18rem, 2fr) 3fr;
  gap: var(--pnmt-space-3);
  align-items: start;
}
@media (max-width: 900px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}
.panel-header {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}
.title {
  font-size: 1.1rem;
  margin: 0;
}
.sub {
  font-size: 0.93rem;
  margin: var(--pnmt-space-2) 0 0;
}
.mail {
  white-space: pre-wrap;
  word-break: break-word;
  background: var(--pnmt-surface-2, transparent);
  padding: var(--pnmt-space-2);
  margin: 0;
  max-height: 30vh;
  overflow: auto;
  font-size: 0.8rem;
}
.examples,
.checks {
  margin: var(--pnmt-space-1) 0 0;
  padding-left: 1.2rem;
}
.options {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-2);
}
.small {
  font-size: 0.857rem;
}
.strong {
  font-weight: 600;
}
</style>
