<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'
import { importanceOptions, type Importance } from '@/views/notifications/importance'

/** A new host (§5.6); everything else is edited where it is shown, on the host's page. */
const router = useRouter()
const { invalidate, notify } = useActions()

const name = ref('')
const location = ref('')
const notes = ref('')
const enabled = ref(true)
const importance = ref<Importance>('Normal')
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

async function submit() {
  errors.value = { name: name.value.trim() ? undefined : 'A host needs a name.' }
  if (errors.value.name) {
    return
  }
  busy.value = true
  try {
    const host = await api.hosts.create({
      name: name.value.trim(),
      location: location.value.trim() || null,
      notes: notes.value || null,
      enabled: enabled.value,
      importance: importance.value,
    })
    await invalidate([keys.hosts])
    notify.success(`Host ${host.name} created`)
    await router.replace({ name: 'host', params: { id: host.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { name: e.field('name'), location: e.field('location'), notes: e.field('notes') }
    } else {
      notify.error(e, 'Not created')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="New host" :crumbs="[{ label: 'Hosts', to: { name: 'hosts' } }]" />
    <form class="panel panel-body form narrow" novalidate @submit.prevent="submit">
      <div class="field">
        <label for="host-name">Name</label>
        <InputText id="host-name" v-model="name" autofocus :invalid="!!errors.name" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="host-location">Location</label>
        <InputText id="host-location" v-model="location" placeholder="Zurich DC, rack 4" />
        <small v-if="errors.location" class="field-error">{{ errors.location }}</small>
      </div>
      <div class="field">
        <label for="host-notes">Notes</label>
        <Textarea id="host-notes" v-model="notes" rows="3" auto-resize />
      </div>
      <div class="field">
        <label for="host-importance">Importance</label>
        <Select
          v-model="importance"
          input-id="host-importance"
          :options="importanceOptions"
          option-label="label"
          option-value="value"
        />
        <small class="field-hint">Its sensors take it unless they have their own; notifications can follow it.</small>
      </div>
      <div class="switch-row">
        <ToggleSwitch v-model="enabled" input-id="host-enabled" />
        <label for="host-enabled">Enabled</label>
      </div>
      <div class="form-actions">
        <Button type="submit" label="Create host" :loading="busy" />
        <Button label="Cancel" severity="secondary" text @click="router.back()" />
      </div>
    </form>
  </div>
</template>

<style scoped>
.narrow {
  max-width: 560px;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
