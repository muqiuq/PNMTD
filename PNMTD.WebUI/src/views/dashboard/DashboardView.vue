<script setup lang="ts">
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useDashboard } from '@/composables/useDashboard'
import { useMedia } from '@/composables/useMedia'
import { useSession } from '@/stores/session'
import CookieCard from './CookieCard.vue'
import HostsPanel from './HostsPanel.vue'
import ProblemsPanel from './ProblemsPanel.vue'
import RecentChanges from './RecentChanges.vue'
import SituationCard from './SituationCard.vue'
import SummaryStrip from './SummaryStrip.vue'

/**
 * "Is anything wrong, and what exactly?" at a glance (§5.5): the summary strip, the problems and the
 * recent changes on the left, every host on the right in a panel that scrolls by itself. Stale data
 * stays visible while a refetch fails. Cookie's card heads the left side, for everyone (docs/cookie-plan.md §9.1); with the
 * AI on, its latest situation analysis follows it.
 */
const dashboard = useDashboard()
const phone = useMedia('(max-width: 599px)')
const session = useSession()
</script>

<template>
  <div class="page dashboard">
    <h1 class="sr-only">Dashboard</h1>
    <Message v-if="dashboard.data.value && dashboard.isError.value" severity="warn" :closable="false" class="lost">
      Connection lost, retrying. The data below is from the last successful update.
    </Message>
    <template v-if="!session.can('Monitoring', 'View')">
      <CookieCard class="no-access" />
      <EmptyState
        icon="pi pi-lock"
        title="You don't have access to anything yet."
        text="Ask an admin to add you to a group. Your account is under Account and Security."
        class="no-access"
      />
    </template>
    <template v-else-if="dashboard.data.value">
      <SummaryStrip :summary="dashboard.data.value.summary" class="summary" />
      <div class="left">
        <CookieCard />
        <SituationCard />
        <ProblemsPanel
          :problems="dashboard.data.value.problems"
          :sensor-count="dashboard.data.value.summary.sensors.ok"
        />
        <RecentChanges :changes="dashboard.data.value.recentChanges" :collapsible="phone" />
      </div>
      <HostsPanel :hosts="dashboard.data.value.hosts" class="right" />
    </template>
    <ErrorState
      v-else-if="dashboard.isError.value"
      :error="dashboard.error.value"
      what="the dashboard"
      @retry="dashboard.refetch()"
    />
    <div v-else class="loading"><ProgressSpinner style="width: 40px; height: 40px" /></div>
  </div>
</template>

<style scoped>
.no-access {
  grid-column: 1 / -1;
}

.dashboard {
  display: grid;
  grid-template-columns: minmax(0, 7fr) minmax(0, 5fr);
  grid-template-rows: auto auto 1fr;
  align-items: start;
}

.lost,
.summary {
  grid-column: 1 / -1;
}

.left {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

/* The page does not scroll for the hosts: their panel does (§5.5). */
.right {
  position: sticky;
  top: calc(var(--pnmt-header-height) + var(--pnmt-space-3));
  height: calc(100vh - var(--pnmt-header-height) - 108px);
  min-height: 320px;
}

.loading {
  grid-column: 1 / -1;
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

@media (max-width: 1199px) {
  .dashboard {
    grid-template-columns: minmax(0, 1fr);
  }

  .right {
    position: static;
    height: auto;
    max-height: 70vh;
  }
}
</style>
