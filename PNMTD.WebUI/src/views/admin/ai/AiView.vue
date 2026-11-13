<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import QueueTab from './QueueTab.vue'
import RequestsTab from './RequestsTab.vue'
import SettingsTab from './SettingsTab.vue'

/**
 * Admin → AI (docs/ai-operations-plan.md §15): every request to a model, live; the work queue with its retries; and
 * the settings, the budget and the spending. Admin only: requests hold mail contents.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'requests'))
const tabs = [
  { key: 'requests', label: 'Requests', to: { name: 'ai', params: { tab: 'requests' } } },
  { key: 'queue', label: 'Queue', to: { name: 'ai', params: { tab: 'queue' } } },
  { key: 'settings', label: 'Settings', to: { name: 'ai', params: { tab: 'settings' } } },
]
</script>

<template>
  <div class="page">
    <PageHeader title="AI" />
    <TabNav :items="tabs" :active="tab" label="AI" />
    <RequestsTab v-if="tab === 'requests'" />
    <QueueTab v-else-if="tab === 'queue'" />
    <SettingsTab v-else-if="tab === 'settings'" />
  </div>
</template>
