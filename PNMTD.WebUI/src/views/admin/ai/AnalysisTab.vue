<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Drawer from 'primevue/drawer'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatPercent } from '@/format'
import { findingSeverity, situationSeverity, triggerLabel } from './aiFormat'

/**
 * The situation analysis (docs/ai-operations-plan.md §9): the runs, newest first, each with its status and findings. A
 * run starts every hour when something changed, and early on a storm, when the uplink came back or the gateway went
 * down. New findings go to the AI analysis sensors; warnings and critical ones become decisions.
 */
const status = useQuery({ queryKey: [...keys.ai, 'status'], queryFn: api.ai.status })
const table = useServerTable<S['AiAnalysisRunItem']>({
  key: [...keys.ai, 'analysis'],
  fetch: (q) => api.ai.analysis(q),
  refetchInterval: 10_000,
})
const selected = ref<string | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const detail = useQuery({
  queryKey: computed(() => [...keys.ai, 'analysis', selected.value]),
  queryFn: () => api.ai.analysisRun(selected.value!),
  enabled: computed(() => selected.value !== null),
})
const { done } = useActions()
const run = () => done(() => api.ai.runAnalysis(), { success: 'Analysis queued', invalidate: [keys.ai] })
const stateSeverity = (s: S['AnalysisRunState']) =>
  s === 'Done' ? 'success' : s === 'Failed' ? 'danger' : s === 'Queued' ? 'info' : 'secondary'
</script>

<template>
  <div class="tab">
    <div class="bar">
      <span v-if="status.data.value" class="muted small">
        <template v-if="status.data.value.analysisEnabled && status.data.value.enabled">
          Every {{ status.data.value.analysisIntervalMinutes }} minutes when something changed
          (Ai__Analysis__IntervalMinutes), and early on a storm, the uplink's return or the gateway's loss.
        </template>
        <template v-else>The analysis is off: Ai__Enabled, Ai__ApiKey and Ai__Analysis__Enabled.</template>
      </span>
      <span class="spacer" />
      <Button
        label="Analyse now"
        icon="pi pi-play"
        size="small"
        :disabled="!status.data.value?.analysisEnabled || !status.data.value?.enabled"
        @click="run"
      />
    </div>
    <ListTable
      :table="table"
      what="analysis runs"
      selectable
      empty-text="No analysis yet."
      @select="(r) => (selected = String(r.id))"
    >
      <Column header="When" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.at" /></template>
      </Column>
      <Column header="Status" style="width: 8rem">
        <template #body="{ data }">
          <Tag v-if="data.status" :value="data.status" :severity="situationSeverity(data.status)" />
          <Tag v-else :value="data.state" :severity="stateSeverity(data.state)" />
        </template>
      </Column>
      <Column header="Summary">
        <template #body="{ data }">
          <span class="ellipsis">{{ data.summary ?? data.error ?? '' }}</span>
        </template>
      </Column>
      <Column header="Findings" style="width: 9rem">
        <template #body="{ data }">
          <span class="findings">
            <Tag v-if="data.critical" :value="`${data.critical} critical`" severity="danger" />
            <Tag v-if="data.warnings" :value="`${data.warnings} warn`" severity="warn" />
            <Tag v-if="data.infos" :value="`${data.infos} info`" severity="info" />
          </span>
        </template>
      </Column>
      <Column header="Why" style="width: 12rem">
        <template #body="{ data }"
          ><span class="small">{{ triggerLabel(data.trigger) }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Tag v-if="item.status" :value="item.status" :severity="situationSeverity(item.status)" />
          <Tag v-else :value="item.state" :severity="stateSeverity(item.state)" />
          <RelativeTime :value="item.at" />
        </div>
        <div class="small">{{ item.summary ?? item.error ?? '' }}</div>
      </template>
    </ListTable>

    <Drawer v-model:visible="open" position="right" header="Analysis" class="analysis-drawer">
      <template v-if="detail.data.value">
        <p class="line">
          <Tag
            v-if="detail.data.value.run.status"
            :value="detail.data.value.run.status"
            :severity="situationSeverity(detail.data.value.run.status)"
          />
          <Tag v-else :value="detail.data.value.run.state" :severity="stateSeverity(detail.data.value.run.state)" />
          <RelativeTime :value="detail.data.value.run.at" />
          <span class="muted small">· {{ triggerLabel(detail.data.value.run.trigger) }}</span>
        </p>
        <p v-if="detail.data.value.run.summary">{{ detail.data.value.run.summary }}</p>
        <Message v-if="detail.data.value.run.error" severity="error" :closable="false" size="small">{{
          detail.data.value.run.error
        }}</Message>
        <Message v-if="detail.data.value.run.guardFlagged" severity="warn" :closable="false" size="small">
          The guard flagged the sensors' messages: the analysis ran with every message withheld.
        </Message>
        <p class="muted small">
          {{ detail.data.value.snapshotChars.toLocaleString() }} characters sent, anonymised ·
          {{ detail.data.value.run.withheld }} messages withheld · {{ detail.data.value.requestIds.length }} requests
          (Requests tab)
        </p>
        <p v-if="!detail.data.value.findings.length" class="muted">No findings.</p>
        <section v-for="f in detail.data.value.findings" :key="f.id" class="finding" :class="{ muted: f.muted }">
          <div class="line">
            <Tag :value="f.severity" :severity="findingSeverity(f.severity)" />
            <strong>{{ f.title }}</strong>
          </div>
          <div class="small muted">
            {{ f.kind }} · confidence {{ formatPercent(f.confidence, 0) }}
            <template v-if="f.verified"> · confirmed by the verification model</template>
            <template v-if="f.disputed"> · rated critical, not confirmed</template>
            <template v-if="f.muted"> · muted</template>
            <template v-else-if="f.new"> · new, sent</template>
            <template v-else> · already reported</template>
          </div>
          <p>{{ f.explanation }}</p>
          <p><strong>Recommended:</strong> {{ f.recommendation }}</p>
          <ul v-if="f.evidence.length" class="small evidence">
            <li v-for="e in f.evidence" :key="e">{{ e }}</li>
          </ul>
          <p v-if="f.sensors.length || f.hosts.length" class="small">
            <template v-for="s in f.sensors" :key="s.id">
              <RouterLink :to="{ name: 'sensor', params: { id: s.id } }">{{ s.name }}</RouterLink>{{ ' ' }}
            </template>
            <template v-for="h in f.hosts" :key="h.id">
              <RouterLink :to="{ name: 'host', params: { id: h.id } }">{{ h.name }}</RouterLink>{{ ' ' }}
            </template>
          </p>
          <RouterLink v-if="f.decisionId" :to="{ name: 'decisions', params: { id: f.decisionId } }" class="small"
            >The decision</RouterLink
          >
        </section>
      </template>
    </Drawer>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.bar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.findings {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.finding {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
  margin-top: var(--pnmt-space-2);
}

.finding p {
  margin: var(--pnmt-space-1) 0;
}

.evidence {
  margin: var(--pnmt-space-1) 0;
  padding-left: var(--pnmt-space-4);
}
</style>

<style>
.analysis-drawer {
  width: min(40rem, 100vw);
}
</style>
