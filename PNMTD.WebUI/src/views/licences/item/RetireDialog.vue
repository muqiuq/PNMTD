<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, type LicenseDetail } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'

/**
 * Retire… (docs/licences-plan.md §5.8): for an item no longer in use. It lists the hosts it is on, whose assignments and sensors go,
 * confirmed with a checkbox when there are any; its charges and notices stop. The item, its renewals, charges, documents and history
 * stay; it can be read, renewed and reactivated, not assigned.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ item: Pick<LicenseDetail, 'id' | 'display' | 'name'> }>()
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const assignments = useQuery({
  queryKey: computed(() => licenseKeys.assignments(props.item.id)),
  queryFn: () => licensesApi.assignments(props.item.id),
  enabled: visible,
})
const hosts = computed(() => assignments.data.value ?? [])
const confirmed = ref(false)
const problem = ref<string | null>(null)
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  confirmed.value = false
  problem.value = null
})

async function save() {
  saving.value = true
  problem.value = null
  try {
    await licensesApi.retire(props.item.id, confirmed.value)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(`${props.item.display} retired`)
    visible.value = false
    emit('done')
  } catch (e) {
    if (e instanceof ApiError && e.code === 'confirm_needed') {
      problem.value = e.message
      await assignments.refetch()
    } else notify.error(e, 'Not retired')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Retire ${item.display}`" modal :style="{ width: '32rem' }">
    <form id="licence-retire" class="form" novalidate @submit.prevent="save">
      <p>
        {{ item.name }} is no longer in use. It stays for the record, with its renewals, charges, documents and history;
        its charges and notices stop, and it leaves the lists (the filter <em>Retired</em> shows it).
      </p>
      <div v-if="assignments.isPending.value" class="center"><ProgressSpinner style="width: 24px; height: 24px" /></div>
      <template v-else-if="hosts.length">
        <p>
          It is on {{ hosts.length === 1 ? 'one host' : `${hosts.length} hosts` }};
          {{ hosts.length === 1 ? 'its sensor goes' : 'their sensors go' }}, with their events:
        </p>
        <ul class="hosts" data-test="retire-hosts">
          <li v-for="h in hosts" :key="h.hostId">
            {{ h.hostName }}<span v-if="h.locationName" class="muted"> · {{ h.locationName }}</span>
          </li>
        </ul>
        <label class="check">
          <Checkbox v-model="confirmed" binary input-id="lr-confirm" />
          <span>Remove it from {{ hosts.length === 1 ? 'this host' : `these ${hosts.length} hosts` }}</span>
        </label>
      </template>
      <p v-else class="muted">It is on no host.</p>
      <small v-if="problem" class="field-error" role="alert">{{ problem }}</small>
    </form>
    <template #footer>
      <Button label="Keep it" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-retire"
        label="Retire"
        severity="danger"
        :disabled="assignments.isPending.value || (hosts.length > 0 && !confirmed)"
        :loading="saving"
        data-test="retire-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.hosts {
  margin: 0;
  padding-left: 1.1rem;
  max-height: 12rem;
  overflow-y: auto;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.center {
  display: flex;
  justify-content: center;
}

p {
  margin: 0;
}
</style>
