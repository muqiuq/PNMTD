<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Select from 'primevue/select'
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import { decisionKindLabel, decisionKinds, decisionOutcomeLabel, decisionOutcomes } from './acting'
import DecisionLog from './DecisionLog.vue'

/**
 * Cookie's decisions (docs/cookie-plan.md §11, §21.5): the decision log the reader may see, with Right and Wrong. The
 * filters are in the query (?kind=, ?outcome=), so a view can be linked.
 */
const route = useRoute()
const router = useRouter()
const text = (v: unknown) => (typeof v === 'string' && v ? v : null)
const kind = computed(() => text(route.query.kind))
const outcome = computed(() => text(route.query.outcome))
function filter(name: 'kind' | 'outcome', value: string | null | undefined) {
  const query = { ...route.query }
  if (!value) delete query[name]
  else query[name] = value
  void router.replace({ query })
}

const status = useQuery({
  queryKey: [...keys.cookie, 'status'],
  queryFn: api.cookie.status,
  refetchInterval: (q) => (q.state.data?.cookie.state === 'Working' ? 2_000 : 15_000),
})
const working = computed(() => status.data.value?.cookie.state === 'Working')

// "Every …" is no filter (null), which the select shows as its placeholder.
const kindOptions = [
  { label: 'Every kind', value: null as string | null },
  ...decisionKinds.map((k) => ({ label: decisionKindLabel(k), value: k as string | null })),
]
const outcomeOptions = [
  { label: 'Every outcome', value: null as string | null },
  ...decisionOutcomes.map((o) => ({ label: decisionOutcomeLabel(o), value: o as string | null })),
]
</script>

<template>
  <div class="decisions">
    <div class="filters">
      <Select
        :model-value="kind"
        :options="kindOptions"
        option-label="label"
        option-value="value"
        placeholder="Every kind"
        size="small"
        aria-label="Kind"
        @update:model-value="(v: string | null) => filter('kind', v)"
      />
      <Select
        :model-value="outcome"
        :options="outcomeOptions"
        option-label="label"
        option-value="value"
        placeholder="Every outcome"
        size="small"
        aria-label="Outcome"
        @update:model-value="(v: string | null) => filter('outcome', v)"
      />
      <span class="live muted small">
        <i :class="working ? 'pi pi-spin pi-spinner' : 'pi pi-circle'" aria-hidden="true" />
        {{ working ? 'Cookie is working: new entries every 2 s' : 'New entries every 15 s' }}
      </span>
    </div>
    <p class="muted small intro">
      What Cookie proposed and what came of it: code checks each proposal (its level, the confidence, the targets, mute
      and suppress, duplicates), Seraph reviews what leaves PNMT or reaches people, and a person decides what waits in
      the decisions inbox. Mark an entry Right or Wrong: the ratings become Cookie's lessons.
    </p>
    <DecisionLog :kind="kind" :outcome="outcome" />
  </div>
</template>

<style scoped>
.decisions {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 1100px;
}

.filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.live {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  margin-left: auto;
}

.intro {
  margin: 0;
  max-width: 90ch;
}

.small {
  font-size: 0.857rem;
}
</style>
