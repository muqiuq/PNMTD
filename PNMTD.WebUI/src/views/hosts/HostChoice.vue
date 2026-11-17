<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, useId, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import LocationPicker from '@/views/locations/LocationPicker.vue'

/**
 * Which host a server or a router goes on (docs/windows-sensor-plan.md §3.3, docs/mikrotik-plan.md §11.2): an existing one
 * from the list, or a new one made right here with a name and a location. The caller asks resolve() for the host when it
 * continues; a new host is created then, not before, and only once. Making a host needs Monitoring's Edit.
 */
const hostId = defineModel<string | null>('hostId', { required: true })
/** Whether the choice is complete: a host chosen, or a name for the new one. */
const ready = defineModel<boolean>('ready', { default: false })
const props = withDefaults(defineProps<{ active?: boolean; allowNew?: boolean; hint?: string }>(), {
  active: true,
  allowNew: true,
  hint: '',
})
const uid = useId()
const session = useSession()
const { invalidate } = useActions()
const canCreate = computed(() => props.allowNew && session.can('Monitoring', 'Edit'))
const mode = ref<'existing' | 'new'>('existing')
const modes = [
  { label: 'An existing host', value: 'existing' },
  { label: 'A new host', value: 'new' },
]
const name = ref('')
const locationId = ref<string | null>(null)
const errors = ref<Record<string, string | undefined>>({})

// Not the Select's `loading`: PrimeVue ignores clicks while it is set, so a click before the hosts arrive did nothing (and
// e2e's Add server failed on a slow runner). The list opens at once and says it is loading.
const hosts = useQuery({
  queryKey: [...keys.hosts, 'all'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
  enabled: computed(() => props.active),
})

watch(
  () => props.active,
  (on) => {
    if (on) {
      mode.value = 'existing'
      name.value = ''
      locationId.value = null
      errors.value = {}
    }
  },
)
watch(mode, () => (errors.value = {}))
watch(
  [mode, hostId, name],
  () => (ready.value = mode.value === 'existing' ? !!hostId.value : name.value.trim().length > 0),
  { immediate: true },
)

/**
 * The host to go on: the chosen one, or the new one created now. Null when there is none yet, or the new one was refused:
 * the errors under the fields say why. Anything else is thrown, for the caller's own handling.
 */
async function resolve(): Promise<{ id: string; name: string } | null> {
  if (mode.value === 'existing') {
    if (!hostId.value) return null
    const chosen = hosts.data.value?.items.find((h) => h.id === hostId.value)
    return { id: hostId.value, name: chosen?.name ?? '' }
  }
  errors.value = { name: name.value.trim() ? undefined : 'A host needs a name.' }
  if (errors.value.name) return null
  try {
    const host = await api.hosts.create({
      name: name.value.trim(),
      locationId: locationId.value,
      enabled: true,
      importance: 'Normal',
    })
    await invalidate(locationId.value ? [keys.hosts, keys.locations] : [keys.hosts])
    // Made once: a retry of what follows (a token, the next page) goes on with it instead of making another.
    hostId.value = host.id
    mode.value = 'existing'
    return { id: host.id, name: host.name }
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { name: e.field('name'), locationId: e.field('locationId') }
      return null
    }
    throw e
  }
}
defineExpose({ resolve })
</script>

<template>
  <div class="field">
    <label :id="`${uid}-label`">On which host?</label>
    <SelectButton
      v-if="canCreate"
      v-model="mode"
      :options="modes"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      size="small"
      :aria-labelledby="`${uid}-label`"
    />
    <Select
      v-if="mode === 'existing'"
      v-model="hostId"
      :input-id="`${uid}-host`"
      :options="hosts.data.value?.items ?? []"
      option-label="name"
      option-value="id"
      filter
      placeholder="Choose a host"
      aria-label="Host"
      :empty-message="hosts.isPending.value ? 'Loading hosts…' : 'No host yet: create one first.'"
    />
    <small v-if="mode === 'existing' && hint" class="field-hint">{{ hint }}</small>
  </div>
  <template v-if="mode === 'new'">
    <div class="field">
      <label :for="`${uid}-name`">Name of the new host</label>
      <InputText :id="`${uid}-name`" v-model="name" maxlength="200" :invalid="!!errors.name" />
      <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
    </div>
    <div class="field">
      <label :for="`${uid}-location`">Location</label>
      <LocationPicker v-model="locationId" :input-id="`${uid}-location`" :invalid="!!errors.locationId" />
      <small v-if="errors.locationId" class="field-error">{{ errors.locationId }}</small>
      <small v-else class="field-hint">Optional. Everything else about the host is edited on its page.</small>
    </div>
  </template>
</template>
