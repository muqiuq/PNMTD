<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, typePlurals, type LicenseCategory, type LicenseType } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'

/**
 * A category, new or changed (docs/licences-plan.md §3.1, §8.6): its name, description, and the type it is the default for (a new
 * item of that type without a template starts in it). One default per type: the category that had it gives it up. Manage.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ category: LicenseCategory | null; categories: readonly LicenseCategory[] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const name = ref('')
const description = ref('')
const defaultFor = ref<LicenseType | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  name.value = props.category?.name ?? ''
  description.value = props.category?.description ?? ''
  defaultFor.value = props.category?.defaultFor ?? null
  errors.value = {}
})

const types: LicenseType[] = ['License', 'Service', 'Hardware']
const typeOptions = types.map((t) => ({ value: t, label: `New ${typePlurals[t].toLowerCase()}` }))
/** The category that gives up the default when this one takes it. */
const givesUp = computed(() =>
  defaultFor.value
    ? props.categories.find((c) => c.defaultFor === defaultFor.value && c.id !== props.category?.id)
    : undefined,
)

async function save() {
  errors.value = name.value.trim() ? {} : { name: 'A name is needed.' }
  if (errors.value.name) return
  saving.value = true
  try {
    const body = {
      name: name.value.trim(),
      description: description.value.trim() || null,
      defaultFor: defaultFor.value,
    }
    if (props.category) await licensesApi.updateCategory(props.category.id, body)
    else await licensesApi.createCategory(body)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(props.category ? `Category ${body.name} saved` : `Category ${body.name} added`)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = { name: e.field('name'), description: e.field('description'), defaultFor: e.field('defaultFor') }
    else if (e instanceof ApiError && e.code === 'duplicate_key') errors.value = { name: e.message }
    else notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="category ? `Category ${category.name}` : 'New category'"
    modal
    :style="{ width: '30rem' }"
  >
    <form id="licence-category" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="lcat-name">Name</label>
        <InputText
          id="lcat-name"
          v-model="name"
          autofocus
          maxlength="60"
          :invalid="!!errors.name"
          placeholder="Cloud & SaaS"
        />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="lcat-description">Description</label>
        <InputText
          id="lcat-description"
          v-model="description"
          maxlength="200"
          :invalid="!!errors.description"
          placeholder="What the money is for"
        />
        <small v-if="errors.description" class="field-error">{{ errors.description }}</small>
      </div>
      <div class="field">
        <label for="lcat-default">The default for</label>
        <Select
          v-model="defaultFor"
          input-id="lcat-default"
          :options="typeOptions"
          option-label="label"
          option-value="value"
          placeholder="None"
          show-clear
          :invalid="!!errors.defaultFor"
          fluid
        />
        <small v-if="givesUp" class="field-hint">{{ givesUp.name }} is the default now, and gives it up.</small>
        <small v-else class="field-hint">Where new ones of that type start without a template.</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-category"
        :label="category ? 'Save' : 'Add category'"
        :loading="saving"
        data-test="category-save"
      />
    </template>
  </Dialog>
</template>
