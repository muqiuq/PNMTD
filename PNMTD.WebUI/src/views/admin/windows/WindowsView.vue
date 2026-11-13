<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import AgentLogTab from './AgentLogTab.vue'
import ServersTab from './ServersTab.vue'
import TokensTab from './TokensTab.vue'

/**
 * Admin → Windows servers (docs/windows-sensor-plan.md §10.1): the fleet, the join tokens, the agent's downloads and
 * every agent's log. Admin only.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'servers'))
const tabs = [
  { key: 'servers', label: 'Servers', icon: 'pi pi-microsoft', to: { name: 'windows', params: { tab: 'servers' } } },
  { key: 'tokens', label: 'Join tokens', icon: 'pi pi-key', to: { name: 'windows', params: { tab: 'tokens' } } },
  { key: 'log', label: 'Agent log', icon: 'pi pi-history', to: { name: 'windows', params: { tab: 'log' } } },
]
</script>

<template>
  <div class="page">
    <PageHeader title="Windows servers" />
    <TabNav :items="tabs" :active="tab" label="Windows servers" />
    <TokensTab v-if="tab === 'tokens'" />
    <AgentLogTab v-else-if="tab === 'log'" />
    <ServersTab v-else />
  </div>
</template>
