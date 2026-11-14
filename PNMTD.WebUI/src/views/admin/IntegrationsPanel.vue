<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import StatusBadge from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'

/**
 * System health's *Integrations* (docs/audit-redmine-zabbix-plan.md §6.1): whether Redmine is configured and answers, its
 * projects, and *Test*, which signs in with the key and looks for every configured project (an audit entry).
 */
const status = useQuery({ queryKey: [...keys.redmine, 'status'], queryFn: api.redmine.status, refetchInterval: 60_000 })
const { run, busy } = useActions()
const result = ref<S['RedmineTestResult'] | null>(null)

async function test() {
  result.value = null
  const r = await run(() => api.redmine.test(), { failure: 'Redmine did not answer' })
  if (r) result.value = r
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
    <div v-else-if="status.isError.value" class="panel-body muted">Could not load the integrations' state.</div>
  </section>
</template>

<style scoped>
.error {
  color: var(--pnmt-fail);
}
</style>
