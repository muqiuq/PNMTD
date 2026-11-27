<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { licenseKeys, licensesApi } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import { useSession } from '@/stores/session'

/**
 * Hosts that use this key (docs/licences-plan.md §8.11): the Windows hosts whose installed key ends in one of the item's keys' last five
 * characters, as their agents report it. Those not assigned are offered: *Assign them*. Only the five characters are compared.
 */
const props = defineProps<{ licenseId: string; retired?: boolean }>()
const session = useSession()
const queryClient = useQueryClient()
const notify = useNotify()
const activations = useQuery({
  queryKey: computed(() => [...licenseKeys.all, 'activations', props.licenseId]),
  queryFn: () => licensesApi.activations({ license: props.licenseId }),
  staleTime: 60_000,
})
const rows = computed(() => activations.data.value ?? [])
const unassigned = computed(() => rows.value.filter((r) => !r.assigned))
const assigning = ref(false)
async function assign() {
  assigning.value = true
  try {
    await licensesApi.assign(
      props.licenseId,
      unassigned.value.map((r) => r.hostId),
    )
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(`Assigned to ${unassigned.value.length} host${unassigned.value.length === 1 ? '' : 's'}`)
  } catch (e) {
    notify.error(e, 'Not assigned')
  } finally {
    assigning.value = false
  }
}
</script>

<template>
  <section v-if="rows.length" class="panel" aria-labelledby="activations-title" data-test="activation-matches">
    <div id="activations-title" class="panel-header">Hosts that use this key</div>
    <div class="panel-body">
      <ul class="list">
        <li v-for="r in rows" :key="r.hostId">
          <RouterLink :to="{ name: 'host', params: { id: r.hostId } }">{{ r.hostName }}</RouterLink>
          <span class="muted mono">…{{ r.partialKey }}</span>
          <span class="muted">{{ r.channel }}</span>
          <span v-if="r.assigned" class="muted">assigned</span>
          <span v-else class="warn"><i class="pi pi-exclamation-triangle" aria-hidden="true" /> not assigned</span>
        </li>
      </ul>
      <p v-if="unassigned.length && !retired" class="hint">
        {{ unassigned.length }} host{{ unassigned.length === 1 ? ' uses' : 's use' }} this key but
        {{ unassigned.length === 1 ? "isn't" : "aren't" }} assigned.
        <Button
          v-if="session.can('Licenses', 'Edit')"
          :label="unassigned.length === 1 ? 'Assign it' : 'Assign them'"
          size="small"
          text
          :loading="assigning"
          @click="assign"
        />
      </p>
    </div>
  </section>
</template>

<style scoped>
.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.list li {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.warn {
  color: var(--pnmt-warn);
}

.hint {
  margin: var(--pnmt-space-2) 0 0;
}
</style>
