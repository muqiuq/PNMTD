<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import LocationPicker from './LocationPicker.vue'

/**
 * A new location, a new sub-location, or a location's name and description (docs/locations-knowledge-plan.md §3.1,
 * §3.3). The parent is picked when creating; moving is MoveDialog's. Names are unique among siblings.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = withDefaults(defineProps<{ location?: S['LocationDetail'] | null; parentId?: string | null }>(), {
  location: null,
  parentId: null,
})
const emit = defineEmits<{ saved: [detail: S['LocationDetail']] }>()

const name = ref('')
const description = ref('')
const parent = ref<string | null>(null)
const errors = ref<Record<string, string | undefined>>({})
watch(
  visible,
  (open) => {
    if (!open) return
    name.value = props.location?.name ?? ''
    description.value = props.location?.description ?? ''
    parent.value = props.location ? props.location.parentId : props.parentId
    errors.value = {}
  },
  { immediate: true },
)

const editing = computed(() => props.location !== null)
const header = computed(() =>
  editing.value ? `Rename ${props.location!.name}` : parent.value ? 'New sub-location' : 'New location',
)
const { invalidate, notify, busy } = useActions()

async function save() {
  const trimmed = name.value.trim()
  errors.value = { name: trimmed.length >= 2 ? undefined : 'A name of 2 to 64 characters.' }
  if (errors.value.name) return
  busy.value = true
  try {
    const body = { name: trimmed, description: description.value.trim() || null, parentId: parent.value }
    const detail = editing.value
      ? await api.locations.update(props.location!.id, body)
      : await api.locations.create(body)
    await invalidate([keys.locations, keys.hosts])
    notify.success(editing.value ? 'Saved' : `${detail.name} created`)
    emit('saved', detail)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { name: e.field('name'), description: e.field('description'), parentId: e.field('parentId') }
    } else if (e instanceof ApiError && e.status === 409) {
      errors.value = { name: e.message }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="header"
    modal
    :style="{ width: '30rem' }"
    :breakpoints="{ '599px': '95vw' }"
  >
    <form id="location-form" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="location-name">Name</label>
        <InputText
          id="location-name"
          v-model="name"
          maxlength="64"
          autofocus
          :invalid="!!errors.name"
          placeholder="Zurich DC"
        />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="location-description">Description</label>
        <InputText
          id="location-description"
          v-model="description"
          maxlength="256"
          placeholder="One line, shown under the name"
        />
        <small v-if="errors.description" class="field-error">{{ errors.description }}</small>
      </div>
      <div v-if="!editing" class="field">
        <label for="location-parent">Part of</label>
        <LocationPicker
          v-model="parent"
          input-id="location-parent"
          none-label="None (top level)"
          aria-label="Part of"
        />
        <small v-if="errors.parentId" class="field-error">{{ errors.parentId }}</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="location-form" :label="editing ? 'Save' : 'Create'" :loading="busy" />
    </template>
  </Dialog>
</template>
