<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { responsibilitiesApi, responsibilityKeys } from '@/api/responsibilities'
import { firstUnused } from '@/components/colours'
import { useNotify } from '@/composables/useNotify'
import ColourPicker from './ColourPicker.vue'
import PersonPicker from './PersonPicker.vue'

/**
 * A new responsibility (docs/responsibilities-plan.md §9.2): its name, short description, colour and people; then its page. The
 * colour starts as the first swatch nobody uses yet, which can be changed or cleared (docs/responsibilities-extensions-plan.md §4.1).
 */
const visible = defineModel<boolean>('visible', { required: true })
const router = useRouter()
const queryClient = useQueryClient()
const notify = useNotify()

const empty = () => ({
  name: '',
  summary: '',
  color: null as string | null,
  responsibleId: null as string | null,
  deputyId: null as string | null,
})
const draft = ref(empty())
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
// Whether the person chose a colour (or none): the suggestion never overwrites that.
const picked = ref(false)
const colour = computed({
  get: () => draft.value.color,
  set: (value) => {
    picked.value = true
    draft.value.color = value
  },
})
watch(visible, async (open) => {
  if (!open) return
  draft.value = empty()
  errors.value = {}
  picked.value = false
  const list = await queryClient
    .fetchQuery({
      queryKey: responsibilityKeys.list({}),
      queryFn: () => responsibilitiesApi.list({}),
      staleTime: 30_000,
    })
    .catch(() => null)
  if (list && visible.value && !picked.value) draft.value.color = firstUnused(list.items.map((i) => i.color))
})

async function create() {
  errors.value = draft.value.name.trim() ? {} : { name: 'A name is needed.' }
  if (errors.value.name) return
  saving.value = true
  try {
    const created = await responsibilitiesApi.create({
      name: draft.value.name.trim(),
      summary: draft.value.summary.trim() || null,
      color: draft.value.color,
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
        color: e.field('color'),
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
        <label for="nr-colour">Colour</label>
        <ColourPicker v-model="colour" input-id="nr-colour" :invalid="!!errors.color" />
        <small v-if="errors.color" class="field-error">{{ errors.color }}</small>
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
