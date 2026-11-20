<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import AccessTab from './AccessTab.vue'
import ActionsTab from './ActionsTab.vue'
import CaseSettingsTab from './CaseSettingsTab.vue'
import IdentityTab from './IdentityTab.vue'
import InstructionsTab from './InstructionsTab.vue'
import LessonsTab from './LessonsTab.vue'
import LoungeTab from './LoungeTab.vue'
import ModelsTab from './ModelsTab.vue'
import OverviewTab from './OverviewTab.vue'
import RedmineTab from './RedmineTab.vue'
import RightsTab from './RightsTab.vue'

/**
 * Admin → Cookie (docs/cookie-plan.md §9.3, §21.5), for Cookie's Manage: what Cookie and PNMT's duties are doing, and
 * every setting, each section saved as a version that can be viewed and rolled back; the mutes and suppressions code
 * enforces, and the lessons from people's ratings. Cookie itself can't reach any of it. Revision 7 moved the working
 * hours to the availability plan's settings, and who gets what to the plan and the user. Access is the access watch's
 * section (docs/cookie-access-plan.md §8): who logs in, and whom Cookie tells.
 */
const route = useRoute()
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'overview'))
const item = (key: string, label: string) => ({ key, label, to: { name: 'cookie-admin', params: { tab: key } } })
const tabs = [
  item('overview', 'Overview'),
  item('identity', 'Identity'),
  item('cases', 'Cases'),
  item('redmine', 'Redmine'),
  item('actions', 'Actions'),
  item('access', 'Access'),
  item('lounge', 'Lounge'),
  item('instructions', 'Mute, suppress'),
  item('lessons', 'Lessons'),
  item('models', 'Models'),
  item('rights', 'Rights'),
]
</script>

<template>
  <div class="page">
    <PageHeader title="Cookie" :crumbs="[{ label: 'Admin' }]" />
    <TabNav :items="tabs" :active="tab" label="Cookie" wrap />
    <OverviewTab v-if="tab === 'overview'" />
    <IdentityTab v-else-if="tab === 'identity'" />
    <CaseSettingsTab v-else-if="tab === 'cases'" />
    <RedmineTab v-else-if="tab === 'redmine'" />
    <ActionsTab v-else-if="tab === 'actions'" />
    <AccessTab v-else-if="tab === 'access'" />
    <LoungeTab v-else-if="tab === 'lounge'" />
    <InstructionsTab v-else-if="tab === 'instructions'" />
    <LessonsTab v-else-if="tab === 'lessons'" />
    <ModelsTab v-else-if="tab === 'models'" />
    <RightsTab v-else-if="tab === 'rights'" />
  </div>
</template>
