<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import Drawer from 'primevue/drawer'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { outcomeSeverity, pretty, usd } from './aiFormat'

/** Every request to a model (§3.6), newest first, refreshed every 5 seconds; a row opens its raw bodies. */
const role = ref<S['AiRole'] | ''>('')
const outcome = ref<S['AiOutcome'] | ''>('')
const filters = computed(() => ({ role: role.value || undefined, outcome: outcome.value || undefined }))
const table = useServerTable<S['AiRequestItem']>({
  key: [...keys.ai, 'requests'],
  fetch: (q) => api.ai.requests(q),
  filters,
  refetchInterval: 5_000,
})
const roles = [
  { label: 'Every model', value: '' },
  { label: 'Main', value: 'Main' },
  { label: 'Verification', value: 'Verify' },
  { label: 'Guard (Jev)', value: 'Guard' },
]
const outcomes = [
  { label: 'Every outcome', value: '' },
  ...(['Ok', 'HttpError', 'Timeout', 'ConnectError', 'InvalidOutput', 'BlockedByGuard'] as const).map((o) => ({
    label: o,
    value: o,
  })),
]

const selected = ref<number | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const detail = useQuery({
  queryKey: computed(() => [...keys.ai, 'request', selected.value]),
  queryFn: () => api.ai.request(selected.value!),
  enabled: computed(() => selected.value !== null),
})
</script>

<template>
  <div>
    <FilterBar :active="(role ? 1 : 0) + (outcome ? 1 : 0)">
      <Select
        v-model="role"
        :options="roles"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Model"
      />
      <Select
        v-model="outcome"
        :options="outcomes"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Outcome"
      />
    </FilterBar>
    <ListTable
      :table="table"
      what="requests"
      selectable
      empty-text="No AI request yet."
      @select="(r) => (selected = Number(r.id))"
    >
      <Column header="When" style="width: 8rem">
        <template #body="{ data }"><RelativeTime :value="data.at" /></template>
      </Column>
      <Column header="Purpose" style="width: 10rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.purpose }}</span></template
        >
      </Column>
      <Column header="Model">
        <template #body="{ data }"
          ><span class="ellipsis"
            >{{ data.role }} · <span class="mono">{{ data.modelServed ?? data.modelAsked }}</span></span
          ></template
        >
      </Column>
      <Column header="Outcome" style="width: 9rem">
        <template #body="{ data }"><Tag :value="data.outcome" :severity="outcomeSeverity(data.outcome)" /></template>
      </Column>
      <Column header="Time" style="width: 6rem">
        <template #body="{ data }">{{ data.durationMs }} ms</template>
      </Column>
      <Column header="Tokens" style="width: 7rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.promptTokens ?? '–' }} / {{ data.completionTokens ?? '–' }}</span></template
        >
      </Column>
      <Column header="Cost" style="width: 7rem">
        <template #body="{ data }"
          ><span class="mono">{{ usd(data.cost) }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="item.outcome" :severity="outcomeSeverity(item.outcome)" />
          <span class="mono">{{ item.purpose }}</span>
          <RelativeTime :value="item.at" class="muted right" />
        </div>
        <div class="muted small">
          {{ item.role }} · {{ item.modelServed ?? item.modelAsked }} · {{ usd(item.cost) }}
        </div>
      </template>
    </ListTable>

    <Drawer v-model:visible="open" position="right" header="AI request" :style="{ width: 'min(52rem, 100vw)' }">
      <ProgressSpinner v-if="detail.isLoading.value" style="width: 32px; height: 32px" />
      <template v-else-if="detail.data.value">
        <dl class="facts">
          <dt>When</dt>
          <dd>{{ formatAbsolute(detail.data.value.item.at) }}</dd>
          <dt>Purpose</dt>
          <dd class="mono">{{ detail.data.value.item.purpose }} ({{ detail.data.value.promptVersion ?? '–' }})</dd>
          <dt>Model</dt>
          <dd>
            {{ detail.data.value.item.role }} · asked
            <span class="mono">{{ detail.data.value.item.modelAsked }}</span><template
              v-if="detail.data.value.item.modelServed"
            >
              · answered <span class="mono">{{ detail.data.value.item.modelServed }}</span></template
            ><template v-if="detail.data.value.item.provider"> via {{ detail.data.value.item.provider }}</template>
          </dd>
          <dt>Outcome</dt>
          <dd>
            <Tag :value="detail.data.value.item.outcome" :severity="outcomeSeverity(detail.data.value.item.outcome)" />
            <span v-if="detail.data.value.item.httpStatus"> HTTP {{ detail.data.value.item.httpStatus }}</span>
            <span v-if="detail.data.value.item.error" class="error"> {{ detail.data.value.item.error }}</span>
          </dd>
          <dt>Time and cost</dt>
          <dd>
            {{ detail.data.value.item.durationMs }} ms · {{ detail.data.value.item.promptTokens ?? '–' }} in,
            {{ detail.data.value.item.completionTokens ?? '–' }} out,
            {{ detail.data.value.reasoningTokens ?? 0 }} reasoning, {{ detail.data.value.cachedTokens ?? 0 }} cached ·
            {{ usd(detail.data.value.item.cost) }}
          </dd>
          <template v-if="detail.data.value.item.workId">
            <dt>Work</dt>
            <dd class="mono small">
              {{ detail.data.value.item.workId }} · attempt {{ detail.data.value.item.attempt }}
              <template v-if="detail.data.value.sameWork.length">
                · {{ detail.data.value.sameWork.length }} other request(s)</template
              >
            </dd>
          </template>
        </dl>
        <p v-if="detail.data.value.truncated" class="muted small">A body was longer than 1 MiB and is cut.</p>
        <h3 class="title">Sent</h3>
        <pre class="body mono">{{ pretty(detail.data.value.request) }}</pre>
        <h3 class="title">Answered</h3>
        <pre class="body mono">{{ pretty(detail.data.value.response) || '(no answer)' }}</pre>
      </template>
    </Drawer>
  </div>
</template>

<style scoped>
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
.title {
  font-size: 0.93rem;
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-1);
}
.body {
  white-space: pre-wrap;
  word-break: break-word;
  background: var(--pnmt-surface-2, transparent);
  padding: var(--pnmt-space-2);
  margin: 0;
  max-height: 40vh;
  overflow: auto;
  font-size: 0.8rem;
}
.error {
  color: var(--pnmt-danger, red);
}
</style>
