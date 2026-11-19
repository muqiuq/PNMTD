<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import CallersTab from './CallersTab.vue'
import CallsTab from './CallsTab.vue'
import ConnectorsDocsTab from './ConnectorsDocsTab.vue'
import ConnectorsTab from './ConnectorsTab.vue'

/**
 * Integrations → 3rd Party Connectors (docs/connectors-plan.md §13.1): it opens on *Calls*, every active and processed call of every
 * connector (D19); then the connectors, the callers with their keys and networks, and the callers' documentation.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'calls'))
const item = (key: string, label: string) => ({ key, label, to: { name: 'connectors', params: { tab: key } } })
const tabs = [
  item('calls', 'Calls'),
  item('connectors', 'Connectors'),
  item('callers', 'Callers'),
  item('docs', 'Documentation'),
]
</script>

<template>
  <div class="page">
    <PageHeader title="3rd Party Connectors" :crumbs="[{ label: 'Integrations' }]" />
    <TabNav :items="tabs" :active="tab" label="3rd Party Connectors" />
    <CallsTab v-if="tab === 'calls'" />
    <ConnectorsTab v-else-if="tab === 'connectors'" />
    <CallersTab v-else-if="tab === 'callers'" />
    <ConnectorsDocsTab v-else-if="tab === 'docs'" />
  </div>
</template>
