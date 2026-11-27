<script setup lang="ts">
import { computed } from 'vue'
import { typeLabels, type LinkedItemSummary } from '@/api/licenses'
import { useSession } from '@/stores/session'
import VerdictTag from './VerdictTag.vue'

/**
 * The services a sensor watches (docs/licences-plan.md §5.4), on its page for everyone who sees it: the provider, the support
 * contact and URL, the customer number and the SLA, so that when the line is down at 3 a.m. the number to call is where the alarm is.
 * *Open* for Licences & services: View. Nothing while the feature is off: the server sends none.
 */
const props = defineProps<{ items: readonly LinkedItemSummary[] }>()
const session = useSession()
const canOpen = computed(() => session.can('Licenses', 'View'))
const safeUrl = (url: string | null | undefined) => (url && /^https?:\/\//i.test(url) ? url : null)
</script>

<template>
  <section class="panel strip" aria-label="Linked licences and services" data-test="linked-items-strip">
    <ul class="items">
      <li v-for="i in props.items" :key="i.id">
        <i class="pi pi-id-card muted" aria-hidden="true" />
        <span class="strong">{{ typeLabels[i.type] }} {{ i.display }}</span>
        <span>· {{ i.name }}</span>
        <VerdictTag :code="i.verdictCode" compact />
        <span v-if="i.provider" class="muted">· {{ i.provider }}</span>
        <span v-if="i.supportContact">· support {{ i.supportContact }}</span>
        <a v-if="safeUrl(i.supportUrl)" :href="safeUrl(i.supportUrl)!" target="_blank" rel="noopener noreferrer"
          >· support site</a
        >
        <span v-if="i.customerNumber">· customer no. {{ i.customerNumber }}</span>
        <span v-if="i.sla">· SLA {{ i.sla }}</span>
        <span v-if="i.note" class="muted">· {{ i.note }}</span>
        <RouterLink v-if="canOpen" :to="{ name: 'licence', params: { number: String(i.number) } }">Open</RouterLink>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.strip {
  padding: var(--pnmt-space-2) var(--pnmt-space-4);
}

.items {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.items li {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.strong {
  font-weight: 600;
}
</style>
