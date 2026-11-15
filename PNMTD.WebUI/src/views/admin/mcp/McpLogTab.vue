<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'
import type { Schemas as S } from '@/api/client'
import { api, keys } from '@/api/resources'
import AuditChanges from '@/components/AuditChanges.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'

/**
 * The MCP log (docs/mcp-plan.md §10.2): every request, newest first. A row opens what was asked, what came back, Jev's
 * answers, and what it changed: the audit's entries of the same call, field by field. Without MCP's View, one's own.
 */
const route = useRoute()
const session = useSession()
const everyone = computed(() => session.can('Mcp', 'View') && route.query.mine !== '1')
const outcome = ref<S['McpOutcome'] | null>(null)
const kind = ref<S['McpTokenKind'] | null>(null)
const token = computed(() => (typeof route.query.token === 'string' ? route.query.token : undefined))
const filters = computed(() => ({
  outcome: outcome.value ?? undefined,
  kind: kind.value ?? undefined,
  tokenId: token.value,
}))
const table = useServerTable<S['McpLogItem']>({
  key: [...keys.mcp, 'log', everyone],
  fetch: (q) => (everyone.value ? api.mcp.log({ ...q, ...filters.value }) : api.mcp.myLog({ ...q, ...filters.value })),
  filters,
  syncUrl: true,
})
const expanded = ref<Record<string, boolean>>({})
const outcomes: S['McpOutcome'][] = ['Ok', 'ToolError', 'Denied', 'Refused', 'RateLimited', 'Unauthenticated']
const severity = (o: S['McpOutcome']) =>
  o === 'Ok'
    ? 'success'
    : o === 'ToolError'
      ? 'warn'
      : o === 'Unauthenticated' || o === 'RateLimited'
        ? 'secondary'
        : 'danger'
</script>

<template>
  <FilterBar v-model:search="table.search.value" placeholder="Search summaries, users, objects">
    <Select v-model="outcome" :options="outcomes" placeholder="Any outcome" show-clear size="small" />
    <Select v-model="kind" :options="['Read', 'Write']" placeholder="Both tokens" show-clear size="small" />
  </FilterBar>
  <ListTable v-model:expanded="expanded" :table="table" what="MCP requests" fixed>
    <Column expander style="width: 2.5rem" />
    <Column header="Time" style="width: 9rem">
      <template #body="{ data }"><RelativeTime :value="data.at" /></template>
    </Column>
    <Column header="User" style="width: 10rem">
      <template #body="{ data }"
        >{{ data.username || '–' }}
        <span class="muted">{{ data.kind === 'Write' ? 'rw' : data.kind === 'Read' ? 'ro' : '' }}</span></template
      >
    </Column>
    <Column header="Tool" style="width: 12rem">
      <template #body="{ data }"
        ><code>{{ data.tool ?? data.method }}</code></template
      >
    </Column>
    <Column header="Outcome" style="width: 8rem">
      <template #body="{ data }"
        ><Tag :value="data.outcome" :severity="severity(data.outcome)" /><i
          v-if="data.strike"
          class="pi pi-flag strike"
          title="A strike"
      /></template>
    </Column>
    <Column header="Summary">
      <template #body="{ data }"
        ><span class="ellipsis" :title="data.summary">{{ data.summary }}</span></template
      >
    </Column>
    <Column header="Client" style="width: 10rem">
      <template #body="{ data }"
        ><span class="ellipsis muted" :title="data.client ?? ''">{{ data.client ?? '–' }}</span></template
      >
    </Column>
    <template #expansion="{ data }"><McpLogEntry :id="data.id" /></template>
    <template #mobile="{ item }">
      <div>
        <code>{{ item.tool ?? item.method }}</code> <Tag :value="item.outcome" :severity="severity(item.outcome)" />
      </div>
      <div class="muted small"><RelativeTime :value="item.at" /> · {{ item.username }} · {{ item.summary }}</div>
    </template>
  </ListTable>
</template>

<script lang="ts">
import { defineComponent, h } from 'vue'
import ProgressSpinner from 'primevue/progressspinner'

/** One entry, loaded when it opens: the arguments, the result, Jev's answers, the audit's changes. */
const McpLogEntry = defineComponent({
  props: { id: { type: Number, required: true } },
  setup(props) {
    const entry = useQuery({ queryKey: [...keys.mcp, 'entry', props.id], queryFn: () => api.mcp.entry(props.id) })
    const pretty = (text: string | null | undefined) => {
      if (!text) return '—'
      try {
        return JSON.stringify(JSON.parse(text), null, 2)
      } catch {
        return text
      }
    }
    return () => {
      const e = entry.data.value
      if (!e) return h(ProgressSpinner, { style: 'width: 24px; height: 24px' })
      const block = (title: string, text: string) =>
        h('div', { class: 'block' }, [h('div', { class: 'muted small' }, title), h('pre', { class: 'code' }, text)])
      return h('div', { class: 'entry' }, [
        e.error ? block('Error', `${e.item.errorCode ?? ''} ${e.error}`) : null,
        block('Arguments', pretty(e.arguments)),
        block(e.truncated ? 'Result (shortened)' : 'Result', pretty(e.result)),
        e.guard ? block("Jev's answers", pretty(e.guard)) : null,
        h(
          'div',
          { class: 'muted small' },
          `From ${e.item.ip ?? 'unknown'} · ${e.userAgent ?? ''} · ${e.item.durationMs} ms · ${e.correlationId}`,
        ),
        e.changes.length
          ? h('div', { class: 'block' }, [
              h('div', { class: 'muted small' }, 'What changed'),
              ...e.changes.map((c) =>
                h('div', { class: 'change' }, [
                  h('div', `${c.eventType}: ${c.targetName ?? ''} ${c.detail ?? ''}`),
                  c.changes.length ? h(AuditChanges, { changes: c.changes }) : null,
                ]),
              ),
            ])
          : null,
      ])
    }
  },
})
</script>

<style scoped>
.strike {
  margin-left: var(--pnmt-space-1);
  color: var(--p-red-500);
}

:deep(.entry) {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2);
}

:deep(.code) {
  white-space: pre-wrap;
  font-size: 0.8rem;
  margin: 0;
  max-height: 24rem;
  overflow: auto;
}

.ellipsis {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.small {
  font-size: 0.857rem;
}
</style>
