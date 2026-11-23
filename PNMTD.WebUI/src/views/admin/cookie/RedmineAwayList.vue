<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { awayStateLabel, awayStateSeverity, awayVerdict } from './cookieAdmin'

/**
 * Comments while away (docs/cookie-redmine-tab-plan.md §9): what the away watch found, newest first, 50 a page. Each row shows the
 * ticket, who is away and until when, who commented and what, Cookie's verdict (or none), and whom it told or what became of it.
 * The comment's words go after 30 days; the verdict stays.
 */
const page = ref(1)
const away = useQuery({
  queryKey: computed(() => [...keys.cookie, 'admin', 'redmine', 'away', page.value]),
  queryFn: () => api.cookie.redmineAdmin.away(page.value),
})
const data = computed(() => away.data.value)
const pages = computed(() => (data.value ? Math.max(1, Math.ceil(data.value.totalCount / data.value.pageSize)) : 1))
const until = (row: S['CookieAwayCommentItem']) => (row.awayUntil ? `until ${row.awayUntil}` : 'for a while')
</script>

<template>
  <section class="panel" aria-labelledby="redmine-away-list-title">
    <div id="redmine-away-list-title" class="panel-header">
      Comments while away
      <span v-if="data && pages > 1" class="end pager">
        <Button icon="pi pi-angle-left" size="small" text :disabled="page <= 1" aria-label="Newer" @click="page--" />
        <span class="muted small">{{ page }} / {{ pages }}</span>
        <Button
          icon="pi pi-angle-right"
          size="small"
          text
          :disabled="page >= pages"
          aria-label="Older"
          @click="page++"
        />
      </span>
    </div>
    <div class="panel-body">
      <ErrorState v-if="away.isError.value" :error="away.error.value" what="the comments" @retry="away.refetch()" />
      <DataTable
        v-else
        :value="data?.items ?? []"
        :loading="away.isPending.value"
        size="small"
        class="table"
        data-key="id"
      >
        <template #empty>No comment while someone was away yet.</template>
        <Column header="Ticket">
          <template #body="{ data: row }">
            <RouterLink :to="{ name: 'redmine-issue', params: { id: row.issueId } }">{{ row.title }}</RouterLink>
            <div class="muted small">{{ row.assignee ?? 'The assignee' }} away {{ until(row) }}</div>
          </template>
        </Column>
        <Column header="Comment">
          <template #body="{ data: row }">
            {{ row.authorName ?? 'Someone' }}<span v-if="row.isPrivate" class="muted">, private</span>,
            <RelativeTime :value="row.commentAt" />
            <div v-if="row.text" class="muted small comment">{{ row.text }}</div>
          </template>
        </Column>
        <Column header="Verdict" style="width: 14rem">
          <template #body="{ data: row }">
            {{ awayVerdict(row) }}
            <div v-if="row.reason" class="muted small">{{ row.reason }}</div>
          </template>
        </Column>
        <Column header="What came of it" style="width: 16rem">
          <template #body="{ data: row }">
            <Tag :value="awayStateLabel[row.state as S['AwayCommentState']]" :severity="awayStateSeverity(row.state)" />
            <div v-if="row.detail" class="muted small">{{ row.detail }}</div>
          </template>
        </Column>
      </DataTable>
    </div>
  </section>
</template>

<style scoped>
.end {
  margin-left: auto;
}

.pager {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.comment {
  white-space: pre-line;
  max-height: 6em;
  overflow: hidden;
}

.small {
  font-size: 0.857rem;
}
</style>
