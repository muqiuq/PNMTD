<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { daysLeftText, licenseKeys, licensesApi, money } from '@/api/licenses'
import { useNow } from '@/composables/useNow'
import { formatDay } from '@/format'
import { useSession } from '@/stores/session'
import VerdictTag from './VerdictTag.vue'

/**
 * A host's or a location's licences & services (docs/licences-plan.md §8.8): a host's items, or a location's own and those on its
 * hosts, below it too, with their state, expiry and (for a location) their cost per month. Only while the feature is on and for
 * Licences & services: View; with none, nothing is rendered.
 */
const props = defineProps<{ hostId?: string; locationId?: string }>()
const session = useSession()
const now = useNow()
const query = computed(() => ({ host: props.hostId, location: props.locationId }))
const enabled = computed(() => !!session.info?.licenses && session.can('Licenses', 'View'))
const linked = useQuery({
  queryKey: computed(() => licenseKeys.linked(query.value)),
  queryFn: () => licensesApi.linked(query.value),
  enabled,
  staleTime: 60_000,
})
const items = computed(() => (enabled.value ? (linked.data.value ?? []) : []))
// A Windows host's activation (§8.11): the key's last five characters, matched to the items with that key.
const activation = useQuery({
  queryKey: computed(() => [...licenseKeys.all, 'activations', 'host', props.hostId]),
  queryFn: () => licensesApi.activations({ host: props.hostId }),
  enabled: computed(() => enabled.value && !!props.hostId),
  staleTime: 60_000,
})
const windows = computed(() => (enabled.value && props.hostId ? (activation.data.value?.[0] ?? null) : null))
</script>

<template>
  <section v-if="items.length || windows" class="panel" aria-labelledby="licences-title" data-test="licences-panel">
    <div id="licences-title" class="panel-header">
      <i class="pi pi-id-card muted" aria-hidden="true" />
      Licences &amp; services
    </div>
    <ul class="panel-body list">
      <li v-if="windows" data-test="windows-activation">
        <i class="pi pi-microsoft muted" aria-hidden="true" />
        <span
          >Windows is activated with <span class="mono">…{{ windows.partialKey }}</span></span
        >
        <template v-if="windows.kmsClientKey"><span class="muted">a KMS client key</span></template>
        <template v-else-if="windows.matches.length">
          <RouterLink
            v-for="m in windows.matches"
            :key="m.id"
            :to="{ name: 'licence', params: { number: String(m.number) } }"
            >{{ m.display }}</RouterLink
          >
        </template>
        <span v-else class="muted">a key that is in no licence</span>
      </li>
      <li v-for="i in items" :key="i.id">
        <VerdictTag :code="i.verdictCode" compact />
        <RouterLink :to="{ name: 'licence', params: { number: String(i.number) } }">
          <span class="mono">{{ i.display }}</span> {{ i.name }}
        </RouterLink>
        <span v-if="i.expiresOn" class="muted small" :title="i.verdictMessage ?? undefined"
          >{{ formatDay(i.expiresOn) }} · {{ daysLeftText(i.expiresOn, now) }}</span
        >
        <span v-if="locationId && i.perMonth.length" class="muted small">
          {{ i.perMonth.map((m) => money(m.amount, m.currency)).join(' · ') }} a month</span
        >
        <span v-if="locationId && i.via === 'host'" class="muted small">on a host here</span>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.list {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.list li {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
