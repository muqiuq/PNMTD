<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import CookieCard from '@/views/dashboard/CookieCard.vue'
import CasesTab from './CasesTab.vue'
import ChatTab from './ChatTab.vue'
import TraceTab from './TraceTab.vue'

/**
 * Cookie's window (docs/cookie-plan.md §9.2), for Cookie's Use: the cases the reader may see, with their timelines,
 * duties and ticket clocks; the trace of Cookie's rounds; and the chat with Cookie. The case open on the Cases tab is in
 * the query (?case=), so the dashboard can link to it; the trace's filters too (?case=, ?round=, ?kind=). The dashboard's
 * card heads the window (revision 7, §9.2), from the same cache, so it costs no second call.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'cases'))
const item = (key: string, label: string) => ({ key, label, to: { name: 'cookie', params: { tab: key } } })
const tabs = [item('cases', 'Cases'), item('trace', 'Trace'), item('chat', 'Chat')]
</script>

<template>
  <div class="page">
    <PageHeader title="Cookie" />
    <CookieCard in-window />
    <TabNav :items="tabs" :active="tab" label="Cookie" />
    <CasesTab v-if="tab === 'cases'" />
    <TraceTab v-else-if="tab === 'trace'" />
    <ChatTab v-else-if="tab === 'chat'" />
  </div>
</template>
