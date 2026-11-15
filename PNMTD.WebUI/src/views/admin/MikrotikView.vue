<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import NetworkTab from './mikrotik/NetworkTab.vue'
import BackupsTab from './mikrotik/BackupsTab.vue'
import OverviewTab from './mikrotik/OverviewTab.vue'
import RoutersTab from './mikrotik/RoutersTab.vue'
import JobsTab from './mikrotik/JobsTab.vue'
import TunnelsTab from './mikrotik/TunnelsTab.vue'

/**
 * MikroTik & WireGuard (docs/mikrotik-plan.md §11.2), Admin only: one page that monitors and configures
 * the routers and the tunnel network. Tabs (?tab=…, so they can be linked): the overview, the network's
 * settings, every router, and their backups.
 */
const route = useRoute()
const tab = computed(() => (typeof route.query.tab === 'string' ? route.query.tab : 'overview'))
const tabs = computed(() => {
  const to = (t: string) => ({ name: 'mikrotik', query: t === 'overview' ? {} : { tab: t } })
  return [
    { key: 'overview', label: 'Overview', icon: 'pi pi-chart-bar', to: to('overview') },
    { key: 'routers', label: 'Routers', icon: 'pi pi-server', to: to('routers') },
    { key: 'network', label: 'Network', icon: 'pi pi-sitemap', to: to('network') },
    { key: 'tunnels', label: 'Custom tunnels', icon: 'pi pi-lock', to: to('tunnels') },
    { key: 'jobs', label: 'Jobs', icon: 'pi pi-upload', to: to('jobs') },
    { key: 'backups', label: 'Backups', icon: 'pi pi-history', to: to('backups') },
  ]
})
</script>

<template>
  <div class="page">
    <PageHeader title="MikroTik & WireGuard" />
    <TabNav :items="tabs" :active="tab" label="MikroTik sections" />
    <NetworkTab v-if="tab === 'network'" />
    <RoutersTab v-else-if="tab === 'routers'" />
    <TunnelsTab v-else-if="tab === 'tunnels'" />
    <JobsTab v-else-if="tab === 'jobs'" />
    <BackupsTab v-else-if="tab === 'backups'" />
    <OverviewTab v-else />
  </div>
</template>
