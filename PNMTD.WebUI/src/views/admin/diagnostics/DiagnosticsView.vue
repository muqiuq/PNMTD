<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import AssignmentsList from './AssignmentsList.vue'
import RunsTab from './RunsTab.vue'
import SetsTab from './SetsTab.vue'

/**
 * Admin → Diagnostics (docs/windows-sensor-plan.md §10.3): sets of ping and DNS checks, their assignments to servers (each a
 * sensor of its own), and the runs.
 */
const route = useRoute()
const session = useSession()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'sets'))
const tabs = [
  { key: 'sets', label: 'Sets', icon: 'pi pi-list-check', to: { name: 'diagnostics', params: { tab: 'sets' } } },
  {
    key: 'assignments',
    label: 'Assignments',
    icon: 'pi pi-link',
    to: { name: 'diagnostics', params: { tab: 'assignments' } },
  },
  { key: 'runs', label: 'Runs', icon: 'pi pi-history', to: { name: 'diagnostics', params: { tab: 'runs' } } },
]
</script>

<template>
  <div class="page">
    <PageHeader title="Diagnostics" />
    <TabNav :items="tabs" :active="tab" label="Diagnostics" />
    <AssignmentsList
      v-if="tab === 'assignments'"
      :can-write="session.can('Commands', 'Run')"
      :is-admin="session.can('Diagnostics', 'Manage')"
    />
    <RunsTab v-else-if="tab === 'runs'" />
    <SetsTab v-else />
  </div>
</template>
