<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { keyType } from './sshKeyFormat'

/**
 * A user's SSH keys on Admin → Users (docs/linux-sensor-plan.md §6.4, §11.4): each key with where it is authorized (server and
 * account, last use), and Remove all from every server… (offboarding: their keys are retired too).
 */
const props = defineProps<{ userId: string }>()
const emit = defineEmits<{ offboard: [] }>()
const data = useQuery({
  queryKey: computed(() => [...keys.sshKeys, 'user', props.userId]),
  queryFn: () => api.sshKeys.user(props.userId),
})
const d = computed(() => data.data.value ?? null)
</script>

<template>
  <div class="keys">
    <p v-if="data.isPending.value" class="muted">Loading…</p>
    <p v-else-if="!d?.keys.length" class="muted">No key registered for them.</p>
    <template v-else>
      <ul class="plain">
        <li v-for="k in d.keys" :key="k.key.id">
          <RouterLink :to="{ name: 'ssh-keys', params: { tab: 'keys' }, query: { key: k.key.id } }">{{
            k.key.name
          }}</RouterLink>
          <span class="muted small"> {{ keyType(k.key.type, k.key.bits) }}</span>
          <Tag v-if="k.key.state === 'Retired'" value="retired" severity="secondary" class="small" />
          <div v-for="a in k.authorizations" :key="a.id" class="small place">
            {{ a.server.name }} · {{ a.account }}
            <span v-if="a.lastUsedAt" class="muted"> · used <RelativeTime :value="a.lastUsedAt" /></span>
          </div>
          <div v-if="!k.authorizations.length" class="small muted place">not authorized anywhere</div>
        </li>
      </ul>
      <Button
        v-if="d.authorizations"
        :label="`Remove all from every server… (${d.authorizations})`"
        icon="pi pi-trash"
        size="small"
        severity="danger"
        outlined
        @click="emit('offboard')"
      />
    </template>
  </div>
</template>

<style scoped>
.keys {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  align-items: flex-start;
}

.keys p {
  margin: 0;
}

.plain {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.place {
  padding-left: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
