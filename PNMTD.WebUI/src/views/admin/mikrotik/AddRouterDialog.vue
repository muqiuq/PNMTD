<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useActions } from '@/composables/useActions'
import HostChoice from '@/views/hosts/HostChoice.vue'

/**
 * Add router (docs/mikrotik-plan.md §11.2): the host it goes on, an existing one or a new one made right here, then the
 * router form of that host (docs/mikrotik-plan.md §11.1), whose Setup tab shows the join script after the creation.
 */
const visible = defineModel<boolean>('visible', { required: true })
const router = useRouter()
const { run, busy } = useActions()
const hostId = ref<string | null>(null)
const ready = ref(false)
const choice = ref<InstanceType<typeof HostChoice>>()

async function next() {
  const host = await run(() => choice.value!.resolve(), { failure: 'Host not created' })
  if (!host) return
  visible.value = false
  await router.push({ name: 'sensor-new', params: { id: host.id }, query: { type: 'MIKROTIK' } })
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Add router"
    modal
    :style="{ width: '36rem' }"
    :breakpoints="{ '640px': '95vw' }"
  >
    <form class="form" @submit.prevent="next">
      <p class="muted intro">
        A router is a sensor of a host. Next come its address, how PNMT reaches it, and the join script to paste on the
        router.
      </p>
      <HostChoice ref="choice" v-model:host-id="hostId" v-model:ready="ready" :active="visible" />
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        label="Continue"
        icon="pi pi-arrow-right"
        icon-pos="right"
        :loading="busy"
        :disabled="!ready"
        @click="next"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin: 0;
}
</style>
