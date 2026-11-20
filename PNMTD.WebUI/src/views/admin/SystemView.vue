<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import OutboundTab from './system/OutboundTab.vue'
import OverviewTab from './system/OverviewTab.vue'

/**
 * System health (§5.6) with its tabs (docs/redmine-queue-outbound-plan.md §7.1): *Overview*, the tasks, the uplink, the
 * listeners and the integrations; *Outbound*, every notification and every Redmine call, live. Both need System's
 * Manage, the page's right: the records hold message bodies, addresses and ticket contents.
 */
const route = useRoute()
const tab = computed(() => (route.params.tab === 'outbound' ? 'outbound' : 'overview'))
const tabs = [
  { key: 'overview', label: 'Overview', icon: 'pi pi-heart', to: { name: 'system', params: { tab: 'overview' } } },
  { key: 'outbound', label: 'Outbound', icon: 'pi pi-send', to: { name: 'system', params: { tab: 'outbound' } } },
]
</script>

<template>
  <div class="page">
    <PageHeader title="System health" />
    <TabNav :items="tabs" :active="tab" label="System health" />
    <OutboundTab v-if="tab === 'outbound'" />
    <OverviewTab v-else />
  </div>
</template>
