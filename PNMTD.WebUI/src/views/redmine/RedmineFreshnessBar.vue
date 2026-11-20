<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { api, type RedmineRefresh, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'

/**
 * The bar (docs/redmine-queue-outbound-plan.md §5.1–§5.3), in a Redmine page's header: when the page's data was read from
 * Redmine (the exact time in the tooltip), whether a refresh runs (this button's, the scheduler's or another user's) and
 * whether the last one failed (the message in the tooltip), with ⟳ to refresh it now. The button waits for the new data,
 * then refetches `queryKey`; the page polls while `refreshing` (§5.4).
 */
const props = defineProps<{
  freshness: S['RedmineFreshness'] | null | undefined
  /** What the page shows: the overview of a project, a ticket, or the search's query. */
  refresh: RedmineRefresh
  /** The page's query, refetched once the refresh is done. */
  queryKey: readonly unknown[]
}>()
const queryClient = useQueryClient()
const notify = useNotify()
const pending = ref(false)
const busy = computed(() => pending.value || !!props.freshness?.refreshing)
const failure = computed(() => {
  const f = props.freshness
  return f?.failedAt ? `${f.error ?? 'Redmine did not answer.'} (${formatAbsolute(f.failedAt)})` : null
})

async function run() {
  if (busy.value) return
  pending.value = true
  try {
    await api.redmine.refresh(props.refresh)
  } catch (e) {
    notify.error(e, 'Not refreshed')
  } finally {
    // The new snapshot, or the failure: either way the page shows what the server has now.
    await queryClient.invalidateQueries({ queryKey: [...props.queryKey] })
    pending.value = false
  }
}
</script>

<template>
  <div class="freshness">
    <span v-if="busy" class="text muted">Refreshing…</span>
    <span v-else class="text" :class="failure ? 'failed' : 'muted'">
      <i
        v-if="failure"
        v-tooltip.bottom="failure"
        class="pi pi-exclamation-triangle"
        role="img"
        :aria-label="failure"
      />
      <template v-if="freshness?.refreshedAt">Refreshed <RelativeTime :value="freshness.refreshedAt" /></template>
      <template v-else>Not read yet</template>
      <span v-if="failure" v-tooltip.bottom="failure"> · refresh failed</span>
    </span>
    <Button
      v-tooltip.bottom="'Refresh from Redmine'"
      :icon="busy ? 'pi pi-refresh pi-spin' : 'pi pi-refresh'"
      text
      rounded
      size="small"
      severity="secondary"
      :disabled="busy"
      aria-label="Refresh from Redmine"
      @click="run"
    />
  </div>
</template>

<style scoped>
.freshness {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  white-space: nowrap;
  font-size: 0.857rem;
}

.text {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.failed {
  color: var(--pnmt-warn);
}
</style>
