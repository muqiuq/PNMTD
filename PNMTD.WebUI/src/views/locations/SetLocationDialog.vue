<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import LocationPicker from './LocationPicker.vue'

/** *Set location…* for several hosts at once (docs/locations-knowledge-plan.md §3.2), or *None*. Their texts stay. */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ ids: string[] }>()
const emit = defineEmits<{ done: [] }>()

const locationId = ref<string | null>(null)
watch(visible, (open) => open && (locationId.value = null))
const { run, busy, notify } = useActions()

async function apply() {
  const changed = await run(() => api.hosts.setLocation({ ids: props.ids, locationId: locationId.value }), {
    failure: 'Not changed',
    invalidate: [keys.hosts, keys.locations],
  })
  if (changed !== undefined) {
    notify.success(`${changed} host${changed === 1 ? '' : 's'} changed`)
    emit('done')
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Set the location of ${ids.length} host${ids.length === 1 ? '' : 's'}`"
    modal
    :style="{ width: '28rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <div class="field">
      <label for="set-location">Location</label>
      <LocationPicker v-model="locationId" input-id="set-location" />
      <small class="field-hint">Their location texts stay as they are.</small>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Set" :loading="busy" @click="apply" />
    </template>
  </Dialog>
</template>
