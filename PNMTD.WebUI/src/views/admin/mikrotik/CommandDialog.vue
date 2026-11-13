<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { ref, watch } from 'vue'
import type { S } from '@/api/resources'
import CommandForm from '@/views/sensors/mikrotik/CommandForm.vue'

/** Run command… for the routers chosen in the Routers list (docs/mikrotik-plan.md §8.2, §11.2): one batch. */
const visible = defineModel<boolean>('visible', { required: true })
defineProps<{ routers: { id: string; name: string }[] }>()
const emit = defineEmits<{ started: [created: S['MikrotikCommandsCreated']] }>()
const text = ref('')
watch(visible, (open) => {
  if (open) text.value = ''
})

function started(created: S['MikrotikCommandsCreated']) {
  emit('started', created)
  if (!created.refused.length) visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Run a command"
    modal
    :style="{ width: '46rem' }"
    :breakpoints="{ '760px': '95vw' }"
  >
    <p class="routers">
      <strong>{{ routers.length }} router(s):</strong> {{ routers.map((r) => r.name).join(', ') }}
    </p>
    <CommandForm v-model:text="text" :routers="routers" is-admin @started="started" />
    <template #footer>
      <Button label="Close" severity="secondary" text @click="visible = false" />
    </template>
  </Dialog>
</template>

<style scoped>
.routers {
  margin: 0 0 var(--pnmt-space-3);
}
</style>
