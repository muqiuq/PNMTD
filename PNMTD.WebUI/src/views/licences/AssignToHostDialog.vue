<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'

/**
 * Assign a licence or service… from a host's menu (docs/licences-plan.md §8.8): an item (number, name, the seats left), the seats this
 * host uses and a note. The host gets the item's sensor at once. Over-assigning is allowed, and is a finding (449).
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ hostId: string; hostName: string }>()
const queryClient = useQueryClient()
const notify = useNotify()
const rows = useQuery({
  queryKey: licenseKeys.list(),
  queryFn: () => licensesApi.list(),
  enabled: visible,
  staleTime: 30_000,
})
const options = computed(() =>
  (rows.data.value ?? [])
    .filter((r) => r.status !== 'Retired')
    .map((r) => ({
      value: r.id,
      label: `${r.display} ${r.name}`,
      detail: r.seats ? `${Math.max(0, r.seats - r.usedSeats)} of ${r.seats} ${r.seatUnit} left` : '',
      onHost: r.hostNames.includes(props.hostName),
    })),
)
const itemId = ref<string | null>(null)
const seats = ref<number | null>(1)
const note = ref('')
const error = ref<string | undefined>()
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  itemId.value = null
  seats.value = 1
  note.value = ''
  error.value = undefined
})

async function save() {
  if (!itemId.value) {
    error.value = 'Pick a licence or service.'
    return
  }
  saving.value = true
  try {
    await licensesApi.assign(itemId.value, [props.hostId], seats.value, note.value.trim() || null)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(`Assigned to ${props.hostName}`)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code))
      error.value = e.field('seats') ?? e.field('note') ?? e.message
    else notify.error(e, 'Not assigned')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Assign to ${hostName}`" modal :style="{ width: '30rem' }">
    <form id="assign-to-host" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="ath-item">Licence or service</label>
        <Select
          v-model="itemId"
          input-id="ath-item"
          :options="options"
          option-label="label"
          option-value="value"
          option-disabled="onHost"
          filter
          :loading="rows.isLoading.value"
          :invalid="!!error"
          placeholder="Search by number or name"
          fluid
        >
          <template #option="{ option }">
            <span>{{ option.label }}</span>
            <span v-if="option.onHost" class="muted detail">on this host already</span>
            <span v-else-if="option.detail" class="muted detail">{{ option.detail }}</span>
          </template>
        </Select>
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div class="field">
        <label for="ath-seats">Seats this host uses</label>
        <InputNumber v-model="seats" input-id="ath-seats" :min="1" :max="1000000" fluid />
      </div>
      <div class="field">
        <label for="ath-note">Note</label>
        <InputText id="ath-note" v-model="note" />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="assign-to-host" label="Assign" :loading="saving" data-test="assign-to-host" />
    </template>
  </Dialog>
</template>

<style scoped>
.detail {
  margin-left: var(--pnmt-space-2);
  font-size: 0.857rem;
}
</style>
