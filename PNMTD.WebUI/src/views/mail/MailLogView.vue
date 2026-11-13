<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import Drawer from 'primevue/drawer'
import ProgressSpinner from 'primevue/progressspinner'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'

/** The mails the mailbox check fetched (§5.6), newest first, with the rule that took each; not for Viewers. */
const processed = ref<string>('')
const choices = [
  { label: 'All', value: '' },
  { label: 'Processed', value: 'true' },
  { label: 'Not processed', value: 'false' },
]
const filters = computed(() => ({ processed: processed.value === '' ? undefined : processed.value === 'true' }))
const table = useServerTable<S['MailLogItem']>({
  key: [...keys.mail, 'log'],
  fetch: (q) => api.mail.log(q),
  filters,
  syncUrl: true,
})

const selected = ref<string | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const entry = useQuery({
  queryKey: computed(() => [...keys.mail, 'entry', selected.value]),
  queryFn: () => api.mail.logEntry(selected.value!),
  enabled: computed(() => !!selected.value),
})
</script>

<template>
  <div class="page">
    <PageHeader title="Mail log" />
    <FilterBar v-model:search="table.search.value" placeholder="Search sender or subject" :active="processed ? 1 : 0">
      <SelectButton
        v-model="processed"
        :options="choices"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Processed"
      />
    </FilterBar>
    <ListTable :table="table" what="mails" selectable @select="(m) => (selected = m.id)">
      <Column header="Received" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.created" /></template>
      </Column>
      <Column header="From" style="width: 14rem">
        <template #body="{ data }"
          ><span class="ellipsis cell">{{ data.from }}</span></template
        >
      </Column>
      <Column header="Subject">
        <template #body="{ data }"
          ><span class="ellipsis cell wide">{{ data.subject }}</span></template
        >
      </Column>
      <Column header="Processed by" style="width: 14rem">
        <template #body="{ data }">
          <Tag v-if="data.disposition === 'Deleted'" value="deleted" severity="secondary" />
          <Tag v-else-if="data.disposition === 'Ignored'" value="ignored" severity="secondary" />
          <Tag v-else-if="data.disposition === 'AiEvent'" value="AI" severity="info" />
          <span v-if="data.processedBy"> {{ data.processedBy.name }}</span>
          <Tag v-else-if="data.processed && data.disposition === 'None'" value="no rule" severity="secondary" />
          <Tag v-else-if="!data.processed" value="waiting" severity="warn" />
        </template>
      </Column>
      <Column header="AI" style="width: 9rem">
        <template #body="{ data }">
          <RouterLink v-if="data.decisionId" :to="{ name: 'decisions', params: { id: data.decisionId } }" @click.stop
            ><Tag
              :value="data.aiState === 'Blocked' ? 'blocked' : 'decision'"
              :severity="data.aiState === 'Blocked' ? 'danger' : 'warn'"
          /></RouterLink>
          <Tag v-else-if="data.aiState === 'Queued'" value="queued" severity="info" />
          <Tag v-else-if="data.aiState === 'Failed'" value="failed" severity="danger" />
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="mobile-line">
          <span class="ellipsis strong">{{ item.subject }}</span>
          <RelativeTime :value="item.created" class="muted right" />
        </div>
        <div class="muted ellipsis">{{ item.from }}</div>
      </template>
    </ListTable>

    <Drawer
      v-model:visible="open"
      position="right"
      header="Mail"
      class="mail-drawer"
      :style="{ width: 'min(40rem, 100vw)' }"
    >
      <ProgressSpinner v-if="entry.isLoading.value" style="width: 32px; height: 32px" />
      <template v-else-if="entry.data.value">
        <dl class="facts">
          <dt>Received</dt>
          <dd>{{ formatAbsolute(entry.data.value.created) }}</dd>
          <dt>From</dt>
          <dd>{{ entry.data.value.from }}</dd>
          <dt>To</dt>
          <dd>{{ entry.data.value.to }}</dd>
          <dt>Subject</dt>
          <dd>{{ entry.data.value.subject }}</dd>
          <dt>Rule</dt>
          <dd>
            <RouterLink
              v-if="entry.data.value.processedBy"
              :to="{ name: 'mail-input', params: { id: entry.data.value.processedBy.id } }"
              >{{ entry.data.value.processedBy.name }}</RouterLink
            >
            <span v-else class="muted">none</span>
          </dd>
        </dl>
        <h3>Processing</h3>
        <pre class="box">{{ entry.data.value.processLog || '–' }}</pre>
        <h3>Content</h3>
        <pre class="box">{{ entry.data.value.content }}</pre>
      </template>
    </Drawer>
  </div>
</template>

<style scoped>
.cell {
  display: block;
  max-width: 14rem;
}

.cell.wide {
  max-width: 36rem;
}

.strong {
  font-weight: 500;
}

.mobile-line {
  display: flex;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.right {
  margin-left: auto;
  white-space: nowrap;
}

h3 {
  margin: var(--pnmt-space-4) 0 var(--pnmt-space-2);
}

.box {
  margin: 0;
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  max-height: 50vh;
  overflow: auto;
}
</style>
