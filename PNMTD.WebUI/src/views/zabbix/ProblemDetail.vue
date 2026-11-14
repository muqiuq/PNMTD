<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import ProgressSpinner from 'primevue/progressspinner'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'

/**
 * A problem's row opened (§7.5): the trigger's description and URL, and with Zabbix's Details its updates with their
 * messages, asked of Zabbix now.
 */
const props = defineProps<{ problem: S['ZabbixProblemItem'] }>()
const session = useSession()
const detail = useQuery({
  queryKey: [...keys.zabbix, 'problem', props.problem.id],
  queryFn: () => api.zabbix.problem(props.problem.id),
})
</script>

<template>
  <div class="detail">
    <ErrorState v-if="detail.isError.value" :error="detail.error.value" what="the problem" @retry="detail.refetch()" />
    <ProgressSpinner v-else-if="!detail.data.value" style="width: 24px; height: 24px" />
    <template v-else>
      <p v-if="detail.data.value.description" class="wrap">{{ detail.data.value.description }}</p>
      <p v-else class="muted">The trigger has no description.</p>
      <p v-if="detail.data.value.triggerUrl">
        <a :href="detail.data.value.triggerUrl" target="_blank" rel="noopener noreferrer nofollow">{{
          detail.data.value.triggerUrl
        }}</a>
      </p>
      <template v-if="detail.data.value.updates">
        <ul v-if="detail.data.value.updates.length" class="updates">
          <li v-for="(u, i) in detail.data.value.updates" :key="i">
            <span class="muted"><RelativeTime :value="u.at" /> · {{ u.user }} · {{ u.actions.join(', ') }}</span>
            <span v-if="u.message" class="wrap">{{ u.message }}</span>
          </li>
        </ul>
        <p v-else class="muted">No updates.</p>
      </template>
      <p v-else-if="problem.updates && !session.can('Zabbix', 'Details')" class="muted">
        {{ problem.updates }} update(s): their messages need Zabbix's Details.
      </p>
      <p v-if="problem.tags.length" class="muted small">
        Tags: {{ problem.tags.map((t) => (t.value ? `${t.tag}: ${t.value}` : t.tag)).join(', ') }}
      </p>
    </template>
  </div>
</template>

<style scoped>
.detail p {
  margin: 0 0 var(--pnmt-space-2);
}

.wrap {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.updates {
  list-style: none;
  margin: 0 0 var(--pnmt-space-2);
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.updates li {
  display: flex;
  flex-direction: column;
}

.small {
  font-size: 0.85em;
}
</style>
