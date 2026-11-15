<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import McpDocsTab from './McpDocsTab.vue'
import McpLogTab from './McpLogTab.vue'
import McpTokensTab from './McpTokensTab.vue'

/**
 * Admin → MCP (docs/mcp-plan.md §10): the documentation, generated from the tool catalog, for every signed-in user; the
 * log of everyone with MCP's View (one's own without it); every user's tokens with MCP's View, turning one off with Manage.
 */
const route = useRoute()
const session = useSession()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'docs'))
const item = (key: string, label: string) => ({ key, label, to: { name: 'admin-mcp', params: { tab: key } } })
const tabs = computed(() => [
  item('docs', 'Documentation'),
  item('log', session.can('Mcp', 'View') ? 'Log' : 'Your log'),
  ...(session.can('Mcp', 'View') ? [item('tokens', 'Tokens')] : []),
])
</script>

<template>
  <div class="page">
    <PageHeader title="MCP" :crumbs="[{ label: 'Admin' }]" />
    <TabNav :items="tabs" :active="tab" label="MCP" />
    <McpDocsTab v-if="tab === 'docs'" />
    <McpLogTab v-else-if="tab === 'log'" />
    <McpTokensTab v-else-if="tab === 'tokens'" />
  </div>
</template>
