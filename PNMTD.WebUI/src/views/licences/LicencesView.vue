<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import SplitButton from 'primevue/splitbutton'
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { daysUntil, licenseKeys, licensesApi, type LicenseType } from '@/api/licenses'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useNow } from '@/composables/useNow'
import { useSession } from '@/stores/session'
import CostsTab from './CostsTab.vue'
import LicenseTable from './LicenseTable.vue'
import OverviewTab from './OverviewTab.vue'
import TemplatesTab from './TemplatesTab.vue'
import UpcomingTab from './UpcomingTab.vue'

/**
 * Licences & services (docs/licences-plan.md §8.1): what IT pays for, its hosts, dates, seats, costs and documents. The tabs are
 * routes; *Overview* is the default. The badges count what needs attention per type (in the danger tone while something fails) and
 * what comes up in the next 30 days. Only while Licenses__Enabled is on: the route and the menu entry are gone otherwise.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const now = useNow()
const edit = computed(() => session.can('Licenses', 'Edit'))
const manage = computed(() => session.can('Licenses', 'Manage'))

// The rows feed the badges; the tabs read their own (the same query key, so once).
const rows = useQuery({
  queryKey: licenseKeys.list(),
  queryFn: () => licensesApi.list(),
  staleTime: 30_000,
})
function badge(type: LicenseType) {
  const items = (rows.data.value ?? []).filter((r) => r.type === type && (r.verdictCode ?? 0) >= 400)
  return {
    badge: items.length || null,
    tone: items.some((r) => r.verdictCode === 446 || r.verdictCode === 448) ? ('danger' as const) : null,
  }
}
const soon = computed(
  () =>
    (rows.data.value ?? []).filter((r) => {
      const days = [r.expiresOn, r.cancelBy, r.endOfLifeOn, r.reviewDueOn]
        .filter((d): d is string => !!d)
        .map((d) => daysUntil(d, now.value))
      return days.some((d) => d >= 0 && d <= 30)
    }).length || null,
)

const item = (key: string, label: string, extra: object = {}) => ({
  key,
  label,
  to: { name: 'licences', params: { tab: key }, query: {} },
  ...extra,
})
const tabs = computed(() => [
  item('overview', 'Overview'),
  item('licences', 'Licences', badge('License')),
  item('services', 'Services', badge('Service')),
  item('hardware', 'Hardware', badge('Hardware')),
  item('upcoming', 'Upcoming', { badge: soon.value }),
  item('costs', 'Costs'),
  item('templates', 'Templates'),
])
const tab = computed(() => {
  const asked = typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'overview'
  return tabs.value.some((t) => t.key === asked) ? asked : 'overview'
})
const typeOfTab: Record<string, LicenseType> = { licences: 'License', services: 'Service', hardware: 'Hardware' }

// + Add (§8.1): the tab's own type first.
const first = computed<LicenseType>(() => typeOfTab[tab.value] ?? 'License')
const words: Record<LicenseType, string> = { License: 'Licence…', Service: 'Service…', Hardware: 'Hardware…' }
const add = (type: LicenseType) => router.push({ name: 'licence-new', query: { type } })
const addItems = computed(() => [
  ...(['License', 'Service', 'Hardware'] as LicenseType[])
    .filter((t) => t !== first.value)
    .map((t) => ({ label: words[t], icon: 'pi pi-plus', command: () => add(t) })),
  {
    label: 'From a template…',
    icon: 'pi pi-clone',
    command: () => router.push({ name: 'licences', params: { tab: 'templates' } }),
  },
])
</script>

<template>
  <div class="page">
    <PageHeader title="Licences & services">
      <template v-if="edit" #actions>
        <SplitButton
          :label="`Add ${words[first].replace('…', '').toLowerCase()}`"
          icon="pi pi-plus"
          size="small"
          :model="addItems"
          data-test="add-licence"
          @click="add(first)"
        />
      </template>
    </PageHeader>
    <TabNav :items="tabs" :active="tab" label="Licences & services" />
    <OverviewTab v-if="tab === 'overview'" />
    <LicenseTable v-else-if="typeOfTab[tab]" :key="tab" :type="typeOfTab[tab]" :edit="edit" />
    <UpcomingTab v-else-if="tab === 'upcoming'" :edit="edit" />
    <CostsTab v-else-if="tab === 'costs'" :edit="edit" />
    <TemplatesTab v-else-if="tab === 'templates'" :manage="manage" :edit="edit" />
  </div>
</template>
