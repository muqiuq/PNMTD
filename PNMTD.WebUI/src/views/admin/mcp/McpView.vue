<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import McpDocsTab from './McpDocsTab.vue'
import McpStatusTab from './McpStatusTab.vue'
import McpLogTab from './McpLogTab.vue'
import McpTokensTab from './McpTokensTab.vue'

/**
 * Admin → MCP (docs/mcp-plan.md §10): the status with *Revoke all tokens* (docs/adjustments1.md), the documentation, generated from the tool catalog, for every signed-in user; the
 * log of everyone with MCP's View (one's own without it); every user's tokens with MCP's View, turning one off with Manage.
 */
const route = useRoute()
const session = useSession()
// Status first for those who may see the log and the tokens (docs/adjustments1.md); the documentation first for everyone else.
const tab = computed(() =>
  typeof route.params.tab === 'string' && route.params.tab
    ? route.params.tab
    : session.can('Mcp', 'View')
      ? 'status'
      : 'docs',
)
const item = (key: string, label: string) => ({ key, label, to: { name: 'admin-mcp', params: { tab: key } } })
const tabs = computed(() => [
  ...(session.can('Mcp', 'View') ? [item('status', 'Status')] : []),
  item('docs', 'Documentation'),
  item('log', session.can('Mcp', 'View') ? 'Log' : 'Your log'),
  ...(session.can('Mcp', 'View') ? [item('tokens', 'Tokens')] : []),
])
</script>

<template>
  <div class="page">
    <PageHeader title="MCP" :crumbs="[{ label: 'Admin' }]" />
    <TabNav :items="tabs" :active="tab" label="MCP" />
    <McpStatusTab v-if="tab === 'status' && session.can('Mcp', 'View')" />
    <McpDocsTab v-else-if="tab === 'docs' || tab === 'status'" />
    <McpLogTab v-else-if="tab === 'log'" />
    <McpTokensTab v-else-if="tab === 'tokens'" />
  </div>
</template>
