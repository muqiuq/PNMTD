<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { formatAbsolute, formatTime } from '@/format'
import { pretty } from '@/views/admin/ai/aiFormat'
import {
  byRound,
  foldedKind,
  lastId,
  mergeTrace,
  traceKindLabel,
  traceKindSeverity,
  traceKinds,
  type TraceItem,
} from './trace'
import { usePoll } from './usePoll'

/**
 * Cookie's trace (docs/cookie-plan.md §9.2), like Copilot's: each round with its steps, the thinking and the tools'
 * results folded. The full trace is for Cookie's Manage and for whoever has every right Cookie's group has; everyone else
 * sees the notes and replies on the cases they may see. New steps are added as they come: every 2 s while Cookie works,
 * every 15 s otherwise. The filters are in the query (?case=, ?round=, ?kind=).
 */
const route = useRoute()
const router = useRouter()
const text = (v: unknown) => (typeof v === 'string' && v ? v : undefined)
const caseId = computed(() => text(route.query.case))
const roundId = computed(() => (text(route.query.round) ? Number(route.query.round) : undefined))
const kind = computed(() => text(route.query.kind))
function filter(name: 'case' | 'round' | 'kind', value: string | number | null | undefined) {
  const query = { ...route.query }
  if (value === null || value === undefined || value === '') delete query[name]
  else query[name] = String(value)
  void router.replace({ query })
}

const status = useQuery({
  queryKey: [...keys.cookie, 'status'],
  queryFn: api.cookie.status,
  refetchInterval: (q) => (q.state.data?.cookie.state === 'Working' ? 2_000 : 15_000),
})
const working = computed(() => status.data.value?.cookie.state === 'Working')

const items = ref<TraceItem[]>([])
const full = ref(true)
const loaded = ref(false)
const error = ref<unknown>(null)
let generation = 0
async function fetchTrace(reset: boolean) {
  const mine = generation
  try {
    const answer = await api.cookie.trace({
      caseId: caseId.value,
      roundId: roundId.value,
      kind: kind.value,
      afterId: reset ? undefined : lastId(items.value),
      limit: 200,
    })
    if (mine !== generation) return
    items.value = mergeTrace(reset ? [] : items.value, answer.items)
    full.value = answer.full
    error.value = null
    loaded.value = true
  } catch (e) {
    if (mine === generation) error.value = e
  }
}
const poll = usePoll(
  () => fetchTrace(false),
  () => (working.value ? 2_000 : 15_000),
)
watch(
  [caseId, roundId, kind],
  async () => {
    generation++
    items.value = []
    loaded.value = false
    await fetchTrace(true)
    poll.schedule()
  },
  { immediate: true },
)

const groups = computed(() => byRound(items.value))
const opened = ref<Set<number>>(new Set())
function toggle(id: number) {
  const next = new Set(opened.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  opened.value = next
}

// The cases for the filter and for the entries' links: the open ones, and the one filtered by.
const cases = useQuery({
  queryKey: [...keys.cookie, 'cases', 'for-trace'],
  queryFn: () => api.cookie.cases({ pageSize: 200 }),
  staleTime: 30_000,
})
const filtered = useQuery({
  queryKey: computed(() => [...keys.cookie, 'case', caseId.value]),
  queryFn: () => api.cookie.case(caseId.value ?? ''),
  enabled: computed(() => !!caseId.value && !(cases.data.value?.items ?? []).some((c) => c.id === caseId.value)),
})
const titles = computed(() => {
  const map = new Map((cases.data.value?.items ?? []).map((c) => [c.id, c.title]))
  const f = filtered.data.value?.case
  if (f) map.set(f.id, f.title)
  return map
})
const caseOptions = computed(() => [
  { label: 'Every case', value: null as string | null },
  ...[...titles.value.entries()].map(([value, label]) => ({ label, value: value as string | null })),
])
const roundOptions = computed(() => {
  const ids = new Set(items.value.map((t) => t.roundId).filter((r): r is number => r !== null))
  if (roundId.value !== undefined) ids.add(roundId.value)
  return [
    { label: 'Every round', value: null as number | null },
    ...[...ids].sort((a, b) => b - a).map((id) => ({ label: `Round #${id}`, value: id as number | null })),
  ]
})
const kindOptions = [
  { label: 'Every kind', value: null as string | null },
  ...traceKinds.map((k) => ({ label: traceKindLabel(k), value: k as string | null })),
]
const caseLink = (id: string) => ({ name: 'cookie', params: { tab: 'cases' }, query: { case: id } })
const roundLink = (id: number) => ({ name: 'cookie', params: { tab: 'trace' }, query: { round: id } })
const body = (t: TraceItem) => (t.kind === 'tool' ? pretty(t.body) : t.body)
</script>

<template>
  <div class="trace">
    <div class="filters">
      <Select
        :model-value="roundId ?? null"
        :options="roundOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Round"
        @update:model-value="(v: number | null) => filter('round', v)"
      />
      <Select
        :model-value="caseId ?? null"
        :options="caseOptions"
        option-label="label"
        option-value="value"
        filter
        size="small"
        aria-label="Case"
        class="case-filter"
        @update:model-value="(v: string | null) => filter('case', v)"
      />
      <Select
        :model-value="kind ?? null"
        :options="kindOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Kind"
        @update:model-value="(v: string | null) => filter('kind', v)"
      />
      <span class="live muted small">
        <i :class="working ? 'pi pi-spin pi-spinner' : 'pi pi-circle'" aria-hidden="true" />
        {{ working ? 'Cookie is working: new steps every 2 s' : 'New steps every 15 s' }}
      </span>
    </div>
    <Message v-if="loaded && !full" severity="info" :closable="false" size="small">
      You see Cookie's notes and replies on the cases you may see. The full trace, with Cookie's thinking and what its
      tools returned, needs Cookie: Manage, or every right Cookie's group has.
    </Message>
    <ErrorState v-if="error && !loaded" :error="error" what="the trace" @retry="poll.restart()" />
    <div v-else-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <EmptyState
      v-else-if="!items.length"
      icon="pi pi-list"
      title="Nothing in the trace yet."
      text="Cookie writes here while it works: each round's triage, its notes, its replies."
      class="panel"
    />
    <section
      v-for="g in groups"
      :key="String(g.roundId)"
      class="panel round"
      :aria-label="g.roundId === null ? 'Outside a round' : `Round ${g.roundId}`"
    >
      <div class="panel-header">
        <template v-if="g.roundId !== null">
          <RouterLink :to="roundLink(g.roundId)">Round #{{ g.roundId }}</RouterLink>
        </template>
        <span v-else>Outside a round</span>
        <span class="muted normal small">{{ formatAbsolute(g.items[0].at) }}</span>
        <span class="muted normal small end">{{ g.items.length }} step(s)</span>
      </div>
      <ol class="steps">
        <li v-for="t in g.items" :key="t.id" class="step">
          <div class="step-head">
            <span class="time muted small">{{ formatTime(t.at) }}</span>
            <Tag :value="traceKindLabel(t.kind)" :severity="traceKindSeverity(t.kind)" />
            <span class="title">{{ t.title }}</span>
            <span class="links small">
              <RouterLink v-if="t.caseId" :to="caseLink(t.caseId)">{{
                t.caseTitle ?? titles.get(t.caseId) ?? 'The case'
              }}</RouterLink>
              <Button
                v-if="foldedKind(t.kind) && t.body"
                :label="opened.has(t.id) ? 'Hide' : 'Show'"
                :icon="opened.has(t.id) ? 'pi pi-angle-up' : 'pi pi-angle-down'"
                size="small"
                severity="secondary"
                text
                :aria-expanded="opened.has(t.id)"
                @click="toggle(t.id)"
              />
            </span>
          </div>
          <pre
            v-if="t.body && (!foldedKind(t.kind) || opened.has(t.id))"
            class="body"
            :class="{ mono: t.kind === 'tool' }"
            >{{ body(t) }}</pre>
          <div v-if="t.areas.length" class="muted small">From {{ t.areas.join(', ') }}</div>
        </li>
      </ol>
    </section>
  </div>
</template>

<style scoped>
.trace {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.case-filter {
  min-width: 14rem;
}

.live {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  margin-left: auto;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.normal {
  font-weight: 400;
}

.end {
  margin-left: auto;
}

.steps {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.step {
  border-left: 2px solid var(--pnmt-border);
  padding-left: var(--pnmt-space-2);
}

.step-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.title {
  font-weight: 500;
  min-width: 0;
  overflow-wrap: anywhere;
}

.links {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-left: auto;
}

.body {
  margin: var(--pnmt-space-1) 0 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  max-height: 40vh;
  overflow: auto;
}

.body.mono {
  font-family: var(--pnmt-mono);
  font-size: 0.8rem;
}

.small {
  font-size: 0.857rem;
}
</style>
