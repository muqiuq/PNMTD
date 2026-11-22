<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { kbAiWrittenIcon, kbApi, kbKeys, kbNumber, kbStatusLabel, kbStatusSeverity } from '@/api/kb'
import RelativeTime from '@/components/RelativeTime.vue'
import { usePreferences } from '@/stores/preferences'
import { changeLine, moreLine } from './recentChanges'

/**
 * The recent changes on top of the knowledge base (docs/knowledge-base-extensions-plan.md §6): the pages changed in the
 * period, each once with its newest change, newest first, at most 10. It folds, and the browser remembers that. The lines
 * open that revision's changes in the history.
 */
const preferences = usePreferences()
const folded = computed({
  get: () => preferences.kbRecentFolded,
  set: (value: boolean) => (preferences.kbRecentFolded = value),
})
const query = useQuery({ queryKey: kbKeys.changes, queryFn: kbApi.changes, refetchInterval: 60_000 })
const items = computed(() => query.data.value?.items ?? [])
const days = computed(() => query.data.value?.days ?? 14)
</script>

<template>
  <section class="panel recent" aria-labelledby="kb-recent-title">
    <button type="button" class="panel-header fold" :aria-expanded="!folded" @click="folded = !folded">
      <i class="pi" :class="folded ? 'pi-chevron-right' : 'pi-chevron-down'" aria-hidden="true" />
      <span id="kb-recent-title">Recent changes</span>
      <span class="muted">· last {{ days }} days</span>
      <span v-if="folded && items.length" class="muted count">{{ items.length }}</span>
    </button>
    <template v-if="!folded && query.data.value">
      <p v-if="!items.length" class="muted none">No changes in the last {{ days }} days.</p>
      <ul v-else class="rows" aria-label="Recent changes">
        <li v-for="c in items" :key="c.pageId">
          <div class="line">
            <span class="number mono">{{ kbNumber(c.number) }}</span>
            <RouterLink :to="{ name: 'kb-page', params: { number: c.number } }" class="title">{{ c.title }}</RouterLink>
            <Tag
              v-if="c.status !== 'Published'"
              :value="kbStatusLabel[c.status]"
              :severity="kbStatusSeverity(c.status)"
              class="status"
            />
            <i v-if="c.sensitive" class="pi pi-lock muted" title="Sensitive: never shown to AI or MCP" />
            <i v-if="c.writtenWithAi" :class="kbAiWrittenIcon" class="muted" title="Written mostly with AI" />
            <span class="meta muted">
              {{ changeLine(c) }} · <RelativeTime :value="c.at" />
              <template v-if="c.added || c.removed">
                ·
                <RouterLink
                  :to="{ name: 'kb-history', params: { number: c.number }, query: { to: c.revision } }"
                  class="mono"
                  :title="`The changes of revision ${c.revision}`"
                  >+{{ c.added }} −{{ c.removed }}</RouterLink
                >
              </template>
            </span>
          </div>
          <div v-if="c.note || moreLine(c)" class="sub muted">
            <span v-if="c.note" class="note">“{{ c.note }}”</span>
            <span v-if="moreLine(c)" class="more">{{ moreLine(c) }}</span>
          </div>
        </li>
      </ul>
      <div class="footer">
        <RouterLink :to="{ name: 'kb', query: { sort: 'updated' } }">All changes ›</RouterLink>
      </div>
    </template>
  </section>
</template>

<style scoped>
.fold {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  border: none;
  background: none;
  font: inherit;
  color: inherit;
  text-align: left;
  cursor: pointer;
}

.fold .pi {
  font-size: 0.75rem;
}

.count {
  margin-left: auto;
}

.none {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
  min-width: 0;
}

.number {
  color: var(--pnmt-muted);
  white-space: nowrap;
}

.title {
  font-weight: 600;
  min-width: 0;
}

.status {
  font-size: 0.75rem;
}

.meta {
  margin-left: auto;
  font-size: 0.857rem;
}

.sub {
  display: flex;
  gap: var(--pnmt-space-3);
  justify-content: space-between;
  font-size: 0.857rem;
  flex-wrap: wrap;
}

.note {
  font-style: italic;
  overflow-wrap: anywhere;
}

.more {
  margin-left: auto;
}

.footer {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  text-align: right;
  font-size: 0.929rem;
}
</style>
