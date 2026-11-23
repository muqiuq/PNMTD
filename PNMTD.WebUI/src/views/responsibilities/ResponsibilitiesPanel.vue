<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { personLabel, responsibilitiesApi, responsibilityKeys, standsInLabel } from '@/api/responsibilities'
import { useSession } from '@/stores/session'

/**
 * The responsibilities of a host, a location or a page (docs/responsibilities-plan.md §8): only those that link it, nothing
 * inherited, and only for Responsibilities: View. With none, nothing is rendered. Read-only: links are made on the
 * responsibility's page. Not the availability plan's responsible of the day.
 */
const props = defineProps<{ hostId?: string; locationId?: string; pageId?: string }>()
const session = useSession()

const query = computed(() => ({ host: props.hostId, location: props.locationId, page: props.pageId }))
const linked = useQuery({
  queryKey: computed(() => responsibilityKeys.linked(query.value)),
  queryFn: () => responsibilitiesApi.linked(query.value),
  enabled: computed(() => session.can('Responsibilities', 'View')),
  staleTime: 60_000,
})
const items = computed(() => linked.data.value ?? [])
</script>

<template>
  <section
    v-if="items.length"
    class="panel"
    aria-labelledby="responsibilities-title"
    data-test="responsibilities-panel"
  >
    <div id="responsibilities-title" class="panel-header">
      <i class="pi pi-briefcase muted" aria-hidden="true" />
      Responsibilities
    </div>
    <ul class="panel-body list">
      <li v-for="r in items" :key="r.id">
        <div class="head">
          <RouterLink :to="{ name: 'responsibility', params: { id: r.id } }" class="strong">{{ r.name }}</RouterLink>
          <span v-if="r.summary" class="muted detail">{{ r.summary }}</span>
        </div>
        <div class="people">
          <span v-if="r.globalStandsIn" class="stands-in">{{
            standsInLabel({ responsible: r.globalResponsible, deputy: r.globalDeputy })
          }}</span>
          <template v-else>
            <span>{{ personLabel(r.responsible) }}</span>
            <span v-if="r.deputy" class="muted"> · deputy {{ personLabel(r.deputy) }}</span>
          </template>
        </div>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.panel-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.list {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.head {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.detail,
.people {
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.stands-in {
  color: var(--pnmt-warn);
}
</style>
