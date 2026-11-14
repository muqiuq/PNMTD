<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { computed, nextTick, ref } from 'vue'
import { ApiError } from '@/api/client'
import type { S } from '@/api/resources'
import { useNotify } from '@/composables/useNotify'
import LocationPicker from './LocationPicker.vue'

/**
 * A host's location on its page (docs/locations-knowledge-plan.md §3.2): <i>Zurich DC › Cage B · rack 4</i>, the location
 * a link, the text after it; only the text as before when there is no location. Edited in place with the picker and the
 * text, saved together.
 */
const props = withDefaults(
  defineProps<{
    host: Pick<S['HostDetail'], 'location' | 'assignedLocation'>
    save: (patch: Pick<S['HostRequest'], 'locationId' | 'location'>) => Promise<unknown>
    readonly?: boolean
  }>(),
  { readonly: false },
)

const notify = useNotify()
const editing = ref(false)
const saving = ref(false)
const locationId = ref<string | null>(null)
const text = ref('')
const error = ref<string | null>(null)
const root = ref<HTMLElement | null>(null)
const empty = computed(() => !props.host.assignedLocation && !props.host.location)

async function begin() {
  if (props.readonly) return
  locationId.value = props.host.assignedLocation?.id ?? null
  text.value = props.host.location ?? ''
  error.value = null
  editing.value = true
  await nextTick()
  root.value?.querySelector<HTMLElement>('.p-select')?.focus()
}

async function store() {
  saving.value = true
  error.value = null
  try {
    await props.save({ locationId: locationId.value, location: text.value.trim() || null })
    editing.value = false
  } catch (e) {
    const message = e instanceof ApiError ? (e.field('location') ?? e.field('locationId')) : undefined
    if (message) error.value = message
    else notify.error(e, 'Location not saved')
  } finally {
    saving.value = false
  }
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    editing.value = false
  } else if (event.key === 'Enter' && (event.target as HTMLElement | null)?.tagName === 'INPUT') {
    event.preventDefault()
    void store()
  }
}
</script>

<template>
  <div ref="root" class="host-location">
    <div v-if="editing" class="edit" @keydown="onKey">
      <LocationPicker v-model="locationId" size="small" aria-label="Location" class="picker" />
      <InputText
        v-model="text"
        size="small"
        maxlength="128"
        placeholder="Rack 4, room 2.13"
        aria-label="Location text"
        class="text"
      />
      <span class="actions">
        <Button icon="pi pi-check" size="small" :loading="saving" aria-label="Save location" @click="store" />
        <Button
          icon="pi pi-times"
          size="small"
          severity="secondary"
          text
          :disabled="saving"
          aria-label="Discard location"
          @click="editing = false"
        />
      </span>
    </div>
    <div v-else class="shown">
      <RouterLink v-if="host.assignedLocation" :to="{ name: 'location', params: { id: host.assignedLocation.id } }">{{
        host.assignedLocation.path
      }}</RouterLink>
      <span v-if="host.assignedLocation && host.location" class="muted"> · </span>
      <span v-if="host.location" :class="{ muted: !!host.assignedLocation }">{{ host.location }}</span>
      <span v-if="empty" class="muted">–</span>
      <Button
        v-if="!readonly"
        icon="pi pi-pencil"
        text
        rounded
        size="small"
        severity="secondary"
        aria-label="Edit location"
        class="pencil"
        @click="begin"
      />
    </div>
    <div v-if="error" class="field-error" role="alert">{{ error }}</div>
  </div>
</template>

<style scoped>
.edit {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  flex-wrap: wrap;
}

.picker {
  flex: 1 1 12rem;
  min-width: 0;
}

.text {
  flex: 1 1 8rem;
  min-width: 0;
}

.actions {
  display: inline-flex;
  gap: var(--pnmt-space-1);
}

.shown {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  min-height: 28px;
  gap: 0 var(--pnmt-space-1);
}

.pencil {
  width: 1.75rem;
  height: 1.75rem;
  opacity: 0.6;
}

.shown:hover .pencil {
  opacity: 1;
}
</style>
