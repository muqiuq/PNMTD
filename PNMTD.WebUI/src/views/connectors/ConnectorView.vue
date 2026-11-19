<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import CallsTab from './CallsTab.vue'
import ConnectorArgumentsTab from './ConnectorArgumentsTab.vue'
import ConnectorOverviewTab from './ConnectorOverviewTab.vue'
import ConnectorSecurityTab from './ConnectorSecurityTab.vue'
import ConnectorTargetTab from './ConnectorTargetTab.vue'
import ConnectorUsageTab from './ConnectorUsageTab.vue'
import { typeLabel } from './connectorFormat'

/**
 * A connector (docs/connectors-plan.md §13.5): *Overview*, *Target*, *Arguments*, *Security* (its callers, the review, the limits),
 * its *Calls*, and *Usage* for the caller's developers.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const id = computed(() => String(route.params.id))
const tab = computed(() => (typeof route.params.tab === 'string' && route.params.tab ? route.params.tab : 'overview'))
const connector = useQuery({
  queryKey: computed(() => keys.connector(id.value)),
  queryFn: () => api.connectors.get(id.value),
  refetchInterval: (q) =>
    q.state.data?.target.parseState === 'Pending' && q.state.data.type === 'WindowsScript' ? 3000 : 30_000,
})
const item = (key: string, label: string, badge?: string | number | null) => ({
  key,
  label,
  badge,
  to: { name: 'connector', params: { id: id.value, tab: key } },
})
const tabs = computed(() => [
  item('overview', 'Overview', connector.data.value?.warnings.length || null),
  item('target', 'Target'),
  item('arguments', 'Arguments'),
  item('security', 'Security'),
  item('calls', 'Calls'),
  item('usage', 'Usage'),
])
const { ask, done } = useActions()
async function remove() {
  if (
    !(await ask(
      'Delete the connector?',
      'Its queued calls are cancelled; its calls stay in the history, under its name.',
      'Delete',
    ))
  )
    return
  if (await done(() => api.connectors.remove(id.value), { success: 'Deleted', invalidate: [keys.connectors] })) {
    await router.push({ name: 'connectors', params: { tab: 'connectors' } })
  }
}
</script>

<template>
  <div class="page">
    <PageHeader
      :title="connector.data.value?.name ?? 'Connector'"
      :crumbs="[{ label: '3rd Party Connectors', to: { name: 'connectors', params: { tab: 'connectors' } } }]"
    >
      <template v-if="connector.data.value">
        <Tag :value="typeLabel(connector.data.value.type)" severity="secondary" />
        <Tag
          :value="connector.data.value.enabled ? 'enabled' : 'disabled'"
          :severity="connector.data.value.enabled ? 'success' : 'secondary'"
        />
      </template>
      <Button v-if="session.can('Connectors', 'Manage')" label="Delete…" text severity="danger" @click="remove" />
    </PageHeader>
    <ErrorState
      v-if="connector.isError.value"
      :error="connector.error.value"
      what="the connector"
      @retry="connector.refetch()"
    />
    <template v-else-if="connector.data.value">
      <TabNav :items="tabs" :active="tab" label="Connector" />
      <ConnectorOverviewTab v-if="tab === 'overview'" :connector="connector.data.value" />
      <ConnectorTargetTab v-else-if="tab === 'target'" :connector="connector.data.value" />
      <ConnectorArgumentsTab v-else-if="tab === 'arguments'" :connector="connector.data.value" />
      <ConnectorSecurityTab v-else-if="tab === 'security'" :connector="connector.data.value" />
      <CallsTab v-else-if="tab === 'calls'" :connector-id="id" />
      <ConnectorUsageTab v-else-if="tab === 'usage'" :connector="connector.data.value" />
    </template>
  </div>
</template>
