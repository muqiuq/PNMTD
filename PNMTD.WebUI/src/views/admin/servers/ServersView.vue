<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import AgentLogTab from './AgentLogTab.vue'
import DownloadTab from './DownloadTab.vue'
import RunAsTab from './RunAsTab.vue'
import ScriptsTab from './ScriptsTab.vue'
import ServersTab from './ServersTab.vue'
import TokensTab from './TokensTab.vue'

/**
 * Admin → Servers (docs/linux-sensor-plan.md §11.1; Admin → Windows servers before L1, docs/windows-sensor-plan.md §10.1):
 * the fleet of both platforms, the join tokens, the scripts, Windows' run-as accounts, the agent's downloads and every
 * agent's log. Admin only.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'servers'))
const tabs = [
  { key: 'servers', label: 'Servers', icon: 'pi pi-server', to: { name: 'servers', params: { tab: 'servers' } } },
  { key: 'tokens', label: 'Join tokens', icon: 'pi pi-key', to: { name: 'servers', params: { tab: 'tokens' } } },
  { key: 'scripts', label: 'Scripts', icon: 'pi pi-code', to: { name: 'servers', params: { tab: 'scripts' } } },
  { key: 'run-as', label: 'Run-as accounts', icon: 'pi pi-user', to: { name: 'servers', params: { tab: 'run-as' } } },
  { key: 'download', label: 'Download', icon: 'pi pi-download', to: { name: 'servers', params: { tab: 'download' } } },
  { key: 'log', label: 'Agent log', icon: 'pi pi-history', to: { name: 'servers', params: { tab: 'log' } } },
]
</script>

<template>
  <div class="page">
    <PageHeader title="Servers" />
    <TabNav :items="tabs" :active="tab" label="Servers" />
    <TokensTab v-if="tab === 'tokens'" />
    <ScriptsTab v-else-if="tab === 'scripts'" />
    <RunAsTab v-else-if="tab === 'run-as'" />
    <DownloadTab v-else-if="tab === 'download'" />
    <AgentLogTab v-else-if="tab === 'log'" />
    <ServersTab v-else />
  </div>
</template>
