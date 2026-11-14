<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import LocationPicker from './LocationPicker.vue'

/**
 * *Move…* (docs/locations-knowledge-plan.md §3.3): a new parent, or *None* for the top. The location and its
 * sub-locations aren't offered; a seventh level is refused by the server (409 too_deep) and said here.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ location: Pick<S['LocationDetail'], 'id' | 'name' | 'parentId'> }>()
const emit = defineEmits<{ moved: [detail: S['LocationDetail']] }>()

const parent = ref<string | null>(null)
watch(visible, (open) => open && (parent.value = props.location.parentId), { immediate: true })
const { run, busy } = useActions()

async function move() {
  const detail = await run(() => api.locations.move(props.location.id, parent.value), {
    success: `${props.location.name} moved`,
    failure: 'Not moved',
    invalidate: [keys.locations, keys.hosts],
  })
  if (detail) {
    emit('moved', detail)
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Move ${location.name}`"
    modal
    :style="{ width: '30rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <div class="field">
      <label for="move-parent">Part of</label>
      <LocationPicker v-model="parent" input-id="move-parent" :exclude="location.id" none-label="None (top level)" />
      <small class="field-hint">Its sub-locations move with it. Locations nest at most 6 levels deep.</small>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Move" :loading="busy" :disabled="parent === location.parentId" @click="move" />
    </template>
  </Dialog>
</template>
