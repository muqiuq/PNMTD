<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { responsibilitiesApi, responsibilityKeys } from '@/api/responsibilities'
import { useNotify } from '@/composables/useNotify'
import PersonPicker from './PersonPicker.vue'

/** A new responsibility (docs/responsibilities-plan.md §9.2): its name, short description and people; then its page. */
const visible = defineModel<boolean>('visible', { required: true })
const router = useRouter()
const queryClient = useQueryClient()
const notify = useNotify()

const empty = () => ({ name: '', summary: '', responsibleId: null as string | null, deputyId: null as string | null })
const draft = ref(empty())
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (open) {
    draft.value = empty()
    errors.value = {}
  }
})

async function create() {
  errors.value = draft.value.name.trim() ? {} : { name: 'A name is needed.' }
  if (errors.value.name) return
  saving.value = true
  try {
    const created = await responsibilitiesApi.create({
      name: draft.value.name.trim(),
      summary: draft.value.summary.trim() || null,
      responsibleId: draft.value.responsibleId,
      deputyId: draft.value.deputyId,
    })
    await queryClient.invalidateQueries({ queryKey: responsibilityKeys.all })
    visible.value = false
    await router.push({ name: 'responsibility', params: { id: created.id } })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_name')) {
      errors.value = {
        name: e.field('name') ?? (e.code === 'duplicate_name' ? e.message : undefined),
        summary: e.field('summary'),
        responsibleId: e.field('responsibleId'),
        deputyId: e.field('deputyId'),
      }
    } else {
      notify.error(e, 'Not created')
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="New responsibility" modal :style="{ width: '30rem' }">
    <form id="new-responsibility" class="form" novalidate @submit.prevent="create">
      <div class="field">
        <label for="nr-name">Name</label>
        <InputText id="nr-name" v-model="draft.name" autofocus :invalid="!!errors.name" placeholder="Network" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="nr-summary">Short description</label>
        <InputText
          id="nr-summary"
          v-model="draft.summary"
          :invalid="!!errors.summary"
          placeholder="Switches, routers, the firewalls and the WAN links"
        />
        <small v-if="errors.summary" class="field-error">{{ errors.summary }}</small>
      </div>
      <div class="field">
        <label for="nr-responsible">Responsible</label>
        <PersonPicker v-model="draft.responsibleId" input-id="nr-responsible" :invalid="!!errors.responsibleId" />
        <small v-if="errors.responsibleId" class="field-error">{{ errors.responsibleId }}</small>
      </div>
      <div class="field">
        <label for="nr-deputy">Deputy</label>
        <PersonPicker v-model="draft.deputyId" input-id="nr-deputy" :invalid="!!errors.deputyId" />
        <small v-if="errors.deputyId" class="field-error">{{ errors.deputyId }}</small>
      </div>
      <small class="field-hint">The long description and the links come on its page.</small>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="new-responsibility" label="Create responsibility" :loading="saving" />
    </template>
  </Dialog>
</template>
