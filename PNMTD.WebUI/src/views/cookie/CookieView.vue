<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import CasesTab from './CasesTab.vue'

/**
 * Cookie's window (docs/cookie-plan.md §9.2), for Cookie's Use: the cases the reader may see, with their timelines,
 * duties and ticket clocks. Step B adds the trace and the chat as tabs of their own (/cookie/trace, /cookie/chat). The
 * case open on the Cases tab is in the query (?case=), so the dashboard can link to it.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'cases'))
const tabs = [{ key: 'cases', label: 'Cases', to: { name: 'cookie', params: { tab: 'cases' } } }]
</script>

<template>
  <div class="page">
    <PageHeader title="Cookie" />
    <TabNav :items="tabs" :active="tab" label="Cookie" />
    <CasesTab v-if="tab === 'cases'" />
  </div>
</template>
