<script setup lang="ts">
import Column from 'primevue/column'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { importanceSeverity } from '@/views/notifications/importance'
import CaseDetail from './CaseDetail.vue'
import { caseKinds, dutyName } from './cookieFormat'

/**
 * The case book (docs/cookie-plan.md §4.3, §19.1): a problem per host, a ticket per watched Redmine ticket, open ones
 * first, the most important on top. The server leaves out the cases outside the reader's rights. A case opens beside
 * the list, and its id stays in the query (?case=). A stopped case is marked (§4.3).
 */
const route = useRoute()
const router = useRouter()
const show = ref<'open' | 'closed' | 'all'>('open')
const kind = ref<S['CookieCaseKind'] | null>(null)
const filters = computed(() => ({
  open: show.value === 'all' ? undefined : show.value === 'open',
  kind: kind.value ?? undefined,
}))
const table = useServerTable<S['CookieCaseItem']>({
  key: [...keys.cookie, 'cases'],
  fetch: (q) => api.cookie.cases(q),
  filters,
})
const selected = computed(() => (typeof route.query.case === 'string' && route.query.case ? route.query.case : null))
const select = (c: S['CookieCaseItem']) => router.replace({ query: { ...route.query, case: c.id } })

const shows = [
  { label: 'Open', value: 'open' },
  { label: 'Closed', value: 'closed' },
  { label: 'All', value: 'all' },
]
// Every kind is no filter (null), which the select shows as its placeholder.
const kindOptions = [
  { label: 'Every kind', value: null as S['CookieCaseKind'] | null },
  ...caseKinds.map((k) => ({ label: k, value: k as S['CookieCaseKind'] | null })),
]
/** A stopped case (§4.3): Cookie leaves it alone; who stopped it and why. */
const stoppedTitle = (c: S['CookieCaseItem']) =>
  `Stopped by ${c.stopped?.by ?? 'someone'}${c.stopped?.comment ? `: ${c.stopped.comment}` : ''}`
</script>

<template>
  <div class="layout">
    <div class="list">
      <div class="filters">
        <SelectButton
          v-model="show"
          :options="shows"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-label="Open or closed"
        />
        <Select
          v-model="kind"
          :options="kindOptions"
          option-label="label"
          option-value="value"
          placeholder="Every kind"
          size="small"
          aria-label="Kind of case"
        />
      </div>
      <ListTable
        :table="table"
        what="cases"
        selectable
        fixed
        :empty-text="show === 'open' ? 'No open cases: nothing needs anyone.' : 'No cases.'"
        @select="select"
      >
        <Column header="Case">
          <template #body="{ data }">
            <div :class="{ strong: data.id === selected }" class="ellipsis" :title="data.title">{{ data.title }}</div>
            <div class="line">
              <Tag :value="data.kind" severity="secondary" />
              <Tag :value="data.importance" :severity="importanceSeverity(data.importance)" />
              <Tag v-if="data.stopped" value="stopped" severity="warn" :title="stoppedTitle(data)" />
              <Tag v-if="!data.open" value="closed" severity="secondary" />
            </div>
          </template>
        </Column>
        <Column header="Opened" style="width: 6.5rem">
          <template #body="{ data }"><RelativeTime :value="data.openedAt" /></template>
        </Column>
        <Column header="Last change" style="width: 6.5rem">
          <template #body="{ data }"><RelativeTime :value="data.changedAt" /></template>
        </Column>
        <Column header="Next deadline" style="width: 10rem">
          <template #body="{ data }">
            <template v-if="data.next?.dueAt">
              <div class="ellipsis small">{{ dutyName(data.next?.kind) }}</div>
              <RelativeTime :value="data.next?.dueAt" class="small" />
            </template>
            <span v-else class="muted">–</span>
          </template>
        </Column>
        <template #mobile="{ item }">
          <div class="ellipsis">{{ item.title }}</div>
          <div class="muted small">
            {{ item.kind }} · {{ item.importance }}<template v-if="item.stopped"> · stopped</template
            ><template v-if="item.next?.dueAt">
              · {{ dutyName(item.next?.kind) }} <RelativeTime :value="item.next?.dueAt"
            /></template>
          </div>
        </template>
      </ListTable>
    </div>
    <section class="panel detail" aria-label="The case">
      <EmptyState v-if="!selected" icon="pi pi-folder-open" title="Choose a case." />
      <CaseDetail v-else :id="selected" />
    </section>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 1099px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }
}

.list {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  margin-top: var(--pnmt-space-1);
}

.strong {
  font-weight: 600;
}

.small {
  font-size: 0.857rem;
}
</style>
