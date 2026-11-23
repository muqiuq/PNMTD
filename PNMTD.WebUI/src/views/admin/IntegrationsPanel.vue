<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import StatusBadge from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'

/**
 * System health's *Integrations* (docs/audit-redmine-zabbix-plan.md §6.1, §7.1, §8.5): whether Redmine and Zabbix are
 * configured and answer, Redmine's projects, Zabbix's version and host groups, and *Test* for each (an audit entry):
 * Redmine signs in with the key and looks for every project; Zabbix reads its problems and hosts now.
 */
const status = useQuery({ queryKey: [...keys.redmine, 'status'], queryFn: api.redmine.status, refetchInterval: 60_000 })
const zabbix = useQuery({ queryKey: [...keys.zabbix, 'status'], queryFn: api.zabbix.status, refetchInterval: 60_000 })
const { run, busy } = useActions()
const result = ref<S['RedmineTestResult'] | null>(null)
const zabbixResult = ref<S['ZabbixTestResult'] | null>(null)

async function test() {
  result.value = null
  const r = await run(() => api.redmine.test(), { failure: 'Redmine did not answer' })
  if (r) result.value = r
}

async function testZabbix() {
  zabbixResult.value = null
  const r = await run(() => api.zabbix.test(), { failure: 'Zabbix did not answer', invalidate: [keys.zabbix] })
  if (r) zabbixResult.value = r
}
</script>

<template>
  <section class="panel" aria-labelledby="integrations-title">
    <div id="integrations-title" class="panel-header">Integrations</div>
    <dl v-if="status.data.value" class="panel-body facts">
      <dt>Redmine</dt>
      <dd>
        <StatusBadge v-if="!status.data.value.enabled" status="Disabled" label="Not configured" />
        <StatusBadge v-else-if="status.data.value.error" status="Degraded" label="Not answering" />
        <StatusBadge v-else status="Ok" label="Configured" />
      </dd>
      <template v-if="status.data.value.enabled">
        <dt>Address</dt>
        <dd class="mono">{{ status.data.value.url }}</dd>
        <dt>Projects</dt>
        <dd>{{ status.data.value.projects.map((p) => p.name).join(', ') || '–' }}</dd>
        <template v-if="status.data.value.error">
          <dt>Error</dt>
          <dd class="error">{{ status.data.value.error }}</dd>
        </template>
        <dt />
        <dd>
          <Button label="Test" icon="pi pi-bolt" size="small" outlined :loading="busy" @click="test" />
        </dd>
        <template v-if="result">
          <dt>Account</dt>
          <dd>{{ result.account }}</dd>
          <dt>Found</dt>
          <dd>{{ result.projects.map((p) => p.identifier).join(', ') || '–' }}</dd>
          <template v-if="result.notFound.length">
            <dt>Not found</dt>
            <dd class="error">{{ result.notFound.join(', ') }}</dd>
          </template>
        </template>
      </template>
    </dl>
    <dl v-if="zabbix.data.value" class="panel-body facts">
      <dt>Zabbix</dt>
      <dd>
        <StatusBadge v-if="!zabbix.data.value.enabled" status="Disabled" label="Not configured" />
        <StatusBadge v-else-if="zabbix.data.value.error" status="Degraded" label="Not answering" />
        <StatusBadge v-else status="Ok" :label="`Zabbix ${zabbix.data.value.version ?? ''}`" />
      </dd>
      <template v-if="zabbix.data.value.enabled">
        <dt>Address</dt>
        <dd class="mono">{{ zabbix.data.value.url }}</dd>
        <dt>Host groups</dt>
        <dd>{{ zabbix.data.value.hostGroups.join(', ') || 'all the token sees' }}</dd>
        <template v-if="zabbix.data.value.error">
          <dt>Error</dt>
          <dd class="error">{{ zabbix.data.value.error }}</dd>
        </template>
        <dt />
        <dd>
          <Button label="Test" icon="pi pi-bolt" size="small" outlined :loading="busy" @click="testZabbix" />
        </dd>
        <template v-if="zabbixResult">
          <dt>Sees</dt>
          <dd>
            {{ zabbixResult.hosts }} hosts, {{ zabbixResult.problems }} open problems<template
              v-if="zabbixResult.hidden"
              >, {{ zabbixResult.hidden }} hidden as in Zabbix</template
            >
          </dd>
          <template v-if="zabbixResult.notFound.length">
            <dt>Not found</dt>
            <dd class="error">{{ zabbixResult.notFound.join(', ') }}</dd>
          </template>
        </template>
      </template>
    </dl>
    <div v-else-if="status.isError.value" class="panel-body muted">Could not load the integrations' state.</div>
  </section>
</template>

<style scoped>
.error {
  color: var(--pnmt-fail);
}
</style>
