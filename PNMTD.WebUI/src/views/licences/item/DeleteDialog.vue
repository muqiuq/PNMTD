<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, money, type LicenseDetail } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import { spentByYear } from './item'

/**
 * Delete… (docs/licences-plan.md §5.8, Manage): the item goes with its sensors, hosts, links, renewals, charges and documents. The
 * dialog names the hosts, the charges' total and the documents, says that the charges leave the costs, suggests *Retire* to keep
 * them, and asks to type the number (LIC-12).
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ item: Pick<LicenseDetail, 'id' | 'number' | 'display' | 'name' | 'status'> }>()
/** `deleted`: gone; the page leaves and then refreshes the lists. */
const emit = defineEmits<{ deleted: []; retire: [] }>()
const notify = useNotify()

const assignments = useQuery({
  queryKey: computed(() => licenseKeys.assignments(props.item.id)),
  queryFn: () => licensesApi.assignments(props.item.id),
  enabled: visible,
})
const charges = useQuery({
  queryKey: computed(() => licenseKeys.itemCharges(props.item.id)),
  queryFn: () => licensesApi.itemCharges(props.item.id),
  enabled: visible,
})
const documents = useQuery({
  queryKey: computed(() => licenseKeys.documents(props.item.id)),
  queryFn: () => licensesApi.documents(props.item.id),
  enabled: visible,
})
const loading = computed(() => assignments.isPending.value || charges.isPending.value || documents.isPending.value)
const hosts = computed(() => assignments.data.value ?? [])
const docs = computed(() => documents.data.value ?? [])
const totals = computed(() => {
  const sums = new Map<string, number>()
  for (const year of spentByYear(charges.data.value ?? []))
    for (const m of year.amounts) sums.set(m.currency, Math.round(((sums.get(m.currency) ?? 0) + m.amount) * 100) / 100)
  return [...sums.entries()].map(([currency, amount]) => money(amount, currency))
})

const typed = ref('')
const error = ref<string | undefined>()
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  typed.value = ''
  error.value = undefined
})
const matches = computed(() => {
  const t = typed.value.trim().toUpperCase()
  return t === props.item.display.toUpperCase() || t === String(props.item.number)
})

async function save() {
  if (!matches.value) return
  saving.value = true
  error.value = undefined
  try {
    await licensesApi.remove(props.item.id, typed.value.trim())
    notify.success(`${props.item.display} deleted`)
    visible.value = false
    // The page leaves first, so that its item isn't fetched again (404) before the lists are.
    emit('deleted')
  } catch (e) {
    if (e instanceof ApiError && e.field('number')) error.value = e.field('number')
    else notify.error(e, 'Not deleted')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Delete ${item.display}`" modal :style="{ width: '34rem' }">
    <form id="licence-delete" class="form" novalidate @submit.prevent="save">
      <p>
        {{ item.name }} goes, with its sensors, hosts, linked sensors, renewals, charges and documents. The audit log
        keeps that it was.
      </p>
      <div v-if="loading" class="center"><ProgressSpinner style="width: 24px; height: 24px" /></div>
      <ul v-else class="what" data-test="delete-what">
        <li>
          <template v-if="hosts.length"
            >On {{ hosts.length === 1 ? 'one host' : `${hosts.length} hosts` }}:
            {{
              hosts
                .slice(0, 10)
                .map((h) => h.hostName)
                .join(', ')
            }}<template v-if="hosts.length > 10"> and {{ hosts.length - 10 }} more</template>; their sensors go with
            their events.</template
          >
          <template v-else>On no host.</template>
        </li>
        <li>
          <template v-if="totals.length"
            ><strong>Its charges leave the costs:</strong> {{ totals.join(' + ') }} in all, in
            {{ charges.data.value?.length }} {{ charges.data.value?.length === 1 ? 'charge' : 'charges' }}.</template
          >
          <template v-else>No charges recorded.</template>
        </li>
        <li>
          <template v-if="docs.length"
            >{{ docs.length === 1 ? 'One document' : `${docs.length} documents` }}:
            {{
              docs
                .slice(0, 10)
                .map((d) => d.name)
                .join(', ')
            }}<template v-if="docs.length > 10"> and {{ docs.length - 10 }} more</template>.</template
          >
          <template v-else>No documents.</template>
        </li>
      </ul>
      <p v-if="item.status !== 'Retired'" class="retire">
        To keep its costs and records, retire it instead.
        <Button label="Retire…" size="small" text @click="emit('retire')" />
      </p>
      <div class="field">
        <label for="ld-number">Type {{ item.display }} to delete it</label>
        <InputText id="ld-number" v-model="typed" autocomplete="off" :invalid="!!error" data-test="delete-number" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
    </form>
    <template #footer>
      <Button label="Keep it" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-delete"
        label="Delete"
        severity="danger"
        :disabled="!matches"
        :loading="saving"
        data-test="delete-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
p {
  margin: 0;
}

.what {
  margin: 0;
  padding-left: 1.1rem;
  display: grid;
  gap: var(--pnmt-space-1);
}

.retire {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.center {
  display: flex;
  justify-content: center;
}
</style>
