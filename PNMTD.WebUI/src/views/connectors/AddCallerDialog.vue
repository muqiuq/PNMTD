<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Textarea from 'primevue/textarea'
import { ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import NetworksEditor from './NetworksEditor.vue'

/**
 * *Add caller…* (docs/connectors-plan.md §13.4): the name, a description, the networks it calls from, and the connectors to grant it.
 * Saved disabled with its key; its page opens, where the key is shown and the caller enabled.
 */
const visible = defineModel<boolean>('visible', { required: true })
const router = useRouter()
const name = ref('')
const description = ref('')
const networks = ref<S['ConnectorNetworkRequest'][]>([{ cidr: '', label: null }])
const connectorIds = ref<string[]>([])
watch(visible, (open) => {
  if (!open) return
  name.value = ''
  description.value = ''
  networks.value = [{ cidr: '', label: null }]
  connectorIds.value = []
})
const connectors = useQuery({
  queryKey: [...keys.connectors, 'list', 'all'],
  queryFn: () => api.connectors.list({ pageSize: 500 }),
  enabled: visible,
})
const { run, busy } = useActions()
async function save() {
  const created = await run(
    () =>
      api.connectors.createCaller({
        name: name.value.trim(),
        description: description.value.trim() || null,
        enabled: false,
        networks: networks.value.filter((n) => n.cidr.trim()),
        connectorIds: connectorIds.value,
      }),
    { success: 'Added, disabled', invalidate: [keys.connectors] },
  )
  if (created) {
    visible.value = false
    await router.push({ name: 'connector-caller', params: { id: created.caller.id } })
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Add caller"
    modal
    :style="{ width: '44rem' }"
    :breakpoints="{ '760px': '98vw' }"
  >
    <form class="form" @submit.prevent="save">
      <div class="field">
        <label for="caller-name">Name</label>
        <InputText id="caller-name" v-model="name" placeholder="Shop back office" autofocus />
      </div>
      <div class="field">
        <label for="caller-description">Description</label>
        <Textarea
          id="caller-description"
          v-model="description"
          rows="2"
          auto-resize
          placeholder="Who runs it, and whom to ask"
        />
      </div>
      <div class="field">
        <label>Networks it calls from</label>
        <NetworksEditor v-model="networks" />
      </div>
      <div class="field">
        <label for="caller-connectors">Connectors it may call</label>
        <MultiSelect
          v-model="connectorIds"
          input-id="caller-connectors"
          :options="connectors.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          filter
          display="chip"
          placeholder="None yet"
        />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="visible = false" />
      <Button label="Add, disabled" icon="pi pi-check" :loading="busy" :disabled="!name.trim()" @click="save" />
    </template>
  </Dialog>
</template>
