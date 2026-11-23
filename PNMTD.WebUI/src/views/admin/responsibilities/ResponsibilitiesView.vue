<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import RedmineTab from './RedmineTab.vue'
import ResponsibilitiesTab from './ResponsibilitiesTab.vue'

/**
 * Admin → Responsibilities (docs/responsibilities-plan.md §9): who owns which area of work, with a deputy, the global people,
 * and the links. Information only: nothing is sent to these people. Not the availability plan's responsible of the day.
 */
const route = useRoute()
const session = useSession()
const manage = computed(() => session.can('Responsibilities', 'Manage'))
const item = (key: string, label: string) => ({ key, label, to: { name: 'responsibilities', params: { tab: key } } })
// The Redmine tab only while Redmine is configured (§6.1); combinations made before stay and come back with it.
const tabs = computed(() => [
  item('list', 'Responsibilities'),
  ...(session.info?.redmine ? [item('redmine', 'Redmine')] : []),
])
const tab = computed(() => {
  const asked = typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'list'
  return tabs.value.some((t) => t.key === asked) ? asked : 'list'
})
</script>

<template>
  <div class="page">
    <PageHeader title="Responsibilities" :crumbs="[{ label: 'Admin' }]" />
    <TabNav v-if="tabs.length > 1" :items="tabs" :active="tab" label="Responsibilities" />
    <ResponsibilitiesTab v-if="tab === 'list'" :manage="manage" />
    <RedmineTab v-else-if="tab === 'redmine'" :manage="manage" />
  </div>
</template>
