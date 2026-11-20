<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { api, keys } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import ConnectionsTab from './ConnectionsTab.vue'
import InterfacesTab from './InterfacesTab.vue'
import MapTab from './MapTab.vue'
import ScansTab from './ScansTab.vue'
import ScanStrip from './ScanStrip.vue'
import SuggestionsTab from './SuggestionsTab.vue'

/**
 * The network map (docs/network-map-plan.md §8.1): how the routers, hosts and servers are connected. Tabs in ?tab=: the map,
 * the connections, the suggestions, the scans, and the network interfaces (Details). While a scan runs, a strip on every tab
 * says what it is doing.
 */
const route = useRoute()
const session = useSession()
const tab = computed(() => (typeof route.query.tab === 'string' ? route.query.tab : 'map'))
const summary = useQuery({
  queryKey: [...keys.network, 'summary'],
  queryFn: () => api.network.summary(),
  refetchInterval: 30_000,
})
const status = useQuery({
  queryKey: [...keys.network, 'scan-status'],
  queryFn: () => api.network.scanStatus(),
  refetchInterval: (q) => (q.state.data?.running || q.state.data?.queued.length ? 3000 : 30_000),
})
const tabs = computed(() => {
  const to = (t: string) => ({ name: 'network-map', query: t === 'map' ? {} : { tab: t } })
  const s = summary.data.value
  const open = s ? Object.values(s.openSuggestions).reduce((a, b) => a + b, 0) : 0
  return [
    { key: 'map', label: 'Map', icon: 'pi pi-map', to: to('map') },
    {
      key: 'connections',
      label: 'Connections',
      icon: 'pi pi-arrows-h',
      to: to('connections'),
      badge: s?.unconfirmed || null,
    },
    { key: 'suggestions', label: 'Suggestions', icon: 'pi pi-lightbulb', to: to('suggestions'), badge: open || null },
    {
      key: 'scans',
      label: 'Scans',
      icon: 'pi pi-sync',
      to: to('scans'),
      badge: status.data.value?.running ? '●' : null,
    },
    ...(session.can('NetworkMap', 'Details')
      ? [{ key: 'interfaces', label: 'Network interfaces', icon: 'pi pi-list', to: to('interfaces') }]
      : []),
  ]
})
</script>

<template>
  <div class="page">
    <PageHeader title="Network map" />
    <TabNav :items="tabs" :active="tab" label="Network map sections" />
    <ScanStrip v-if="tab !== 'scans'" :status="status.data.value" />
    <ConnectionsTab v-if="tab === 'connections'" />
    <SuggestionsTab v-else-if="tab === 'suggestions'" />
    <ScansTab v-else-if="tab === 'scans'" :status="status.data.value" />
    <InterfacesTab v-else-if="tab === 'interfaces' && session.can('NetworkMap', 'Details')" />
    <MapTab v-else />
  </div>
</template>
