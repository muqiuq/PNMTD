<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import NetworkTab from './mikrotik/NetworkTab.vue'
import OverviewTab from './mikrotik/OverviewTab.vue'
import RoutersTab from './mikrotik/RoutersTab.vue'

/**
 * MikroTik & WireGuard (docs/mikrotik-plan.md §11.2), Admin only: one page that monitors and configures
 * the routers and the tunnel network. Tabs (?tab=…, so they can be linked): the overview, the network's
 * settings, and every router.
 */
const route = useRoute()
const tab = computed(() => (typeof route.query.tab === 'string' ? route.query.tab : 'overview'))
const tabs = computed(() => {
  const to = (t: string) => ({ name: 'mikrotik', query: t === 'overview' ? {} : { tab: t } })
  return [
    { key: 'overview', label: 'Overview', icon: 'pi pi-chart-bar', to: to('overview') },
    { key: 'network', label: 'Network', icon: 'pi pi-sitemap', to: to('network') },
    { key: 'routers', label: 'Routers', icon: 'pi pi-server', to: to('routers') },
  ]
})
</script>

<template>
  <div class="page">
    <PageHeader title="MikroTik & WireGuard" />
    <TabNav :items="tabs" :active="tab" label="MikroTik sections" />
    <NetworkTab v-if="tab === 'network'" />
    <RoutersTab v-else-if="tab === 'routers'" />
    <OverviewTab v-else />
  </div>
</template>
