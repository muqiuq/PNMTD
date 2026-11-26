<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import PeopleTab from './PeopleTab.vue'
import PlanTab from './PlanTab.vue'
import SettingsTab from './SettingsTab.vue'
import StatusTab from './status/StatusTab.vue'

/**
 * Admin → Availability plan (docs/cookie-plan.md §8.2, §9.4): who is responsible, who works, and where. The plan is for
 * Availability: View and for everyone in it, who edits their own column; People and Settings are for its Manage. It works
 * whether Cookie is enabled or not. Status, for the plan's readers too, counts it (docs/availability-extensions-plan.md §9, §10).
 */
const route = useRoute()
const session = useSession()
const manage = computed(() => session.can('Availability', 'Manage'))
const item = (key: string, label: string) => ({ key, label, to: { name: 'availability', params: { tab: key } } })
const tabs = computed(() => [
  item('plan', 'Plan'),
  item('status', 'Status'),
  ...(manage.value ? [item('people', 'People'), item('settings', 'Settings')] : []),
])
const tab = computed(() => {
  const asked = typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'plan'
  return tabs.value.some((t) => t.key === asked) ? asked : 'plan'
})
</script>

<template>
  <div class="page">
    <PageHeader title="Availability plan" :crumbs="[{ label: 'Admin' }]" />
    <TabNav :items="tabs" :active="tab" label="Availability plan" />
    <PlanTab v-if="tab === 'plan'" />
    <StatusTab v-else-if="tab === 'status'" />
    <PeopleTab v-else-if="tab === 'people'" />
    <SettingsTab v-else-if="tab === 'settings'" />
  </div>
</template>
