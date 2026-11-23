<script setup lang="ts">
import { computed, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useSession } from '@/stores/session'
import AccessTab from './AccessTab.vue'
import ActionsTab from './ActionsTab.vue'
import CaseSettingsTab from './CaseSettingsTab.vue'
import { cookieAdminTabs, shownTab } from './cookieAdmin'
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
 * section (docs/cookie-access-plan.md §8): who logs in, and whom Cookie tells. The Redmine tab is Redmine: Manage's
 * (docs/cookie-redmine-tab-plan.md §3.2): each tab names its right, only the allowed ones show, and a tab the reader
 * may not see opens the first allowed one.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const tabs = computed(() =>
  cookieAdminTabs
    .filter((t) => session.allows(t.right))
    .map((t) => ({ key: t.key, label: t.label, to: { name: 'cookie-admin', params: { tab: t.key } } })),
)
const asked = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : null))
const tab = computed(() =>
  shownTab(
    asked.value,
    cookieAdminTabs.filter((t) => session.allows(t.right)),
  ),
)
watchEffect(() => {
  // A tab the reader may not see: the URL follows the one shown.
  if (asked.value && asked.value !== tab.value)
    void router.replace({ name: 'cookie-admin', params: { tab: tab.value } })
})
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
