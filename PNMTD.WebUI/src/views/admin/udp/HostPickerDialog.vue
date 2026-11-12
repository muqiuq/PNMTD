<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import { ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys } from '@/api/resources'

/**
 * A node lives on a host, and the sensor form is the host's (/hosts/:id/sensors/new): this asks for
 * the host first, then opens the form with the type preset and, for an unknown sender, its id.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ deviceId?: number | null }>()
const router = useRouter()

const hosts = useQuery({
  queryKey: [...keys.hosts, 'all'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
  enabled: visible,
})
const hostId = ref<string | null>(null)
watch(visible, (open) => open && (hostId.value = null))

async function go() {
  if (!hostId.value) return
  visible.value = false
  await router.push({
    name: 'sensor-new',
    params: { id: hostId.value },
    query: { type: 'UDP_NODE', deviceId: props.deviceId ? String(props.deviceId) : undefined },
  })
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="deviceId ? `Create node with id ${deviceId}` : 'Add UDP node'"
    modal
    :style="{ width: '26rem' }"
    :breakpoints="{ '480px': '95vw' }"
  >
    <div class="field">
      <label for="node-host">On which host?</label>
      <Select
        v-model="hostId"
        input-id="node-host"
        :options="hosts.data.value?.items ?? []"
        option-label="name"
        option-value="id"
        filter
        placeholder="Choose a host"
        :loading="hosts.isLoading.value"
        empty-message="No host yet: create one first."
      />
      <small class="field-hint">A node is a sensor of its host, as every sensor is.</small>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Continue" icon="pi pi-arrow-right" icon-pos="right" :disabled="!hostId" @click="go" />
    </template>
  </Dialog>
</template>
