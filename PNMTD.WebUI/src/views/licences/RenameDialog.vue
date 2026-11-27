<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, type LicenseNames } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import NameField from './NameField.vue'
import { mergeTarget, nameLabels, renameFields, usesText, type NameField as Field } from './templates'

type NameUse = LicenseNames['manufacturers'][number]

/**
 * Rename… a manufacturer, vendor, cost centre or seat unit everywhere (docs/licences-plan.md §8.6): in every item and template
 * that uses it. To a name in use, the two merge (*Microsoft Corp.* → *Microsoft*), and the dialog says how many use that one
 * already. Manage; audited.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ field: Field; name: NameUse; uses: readonly NameUse[] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const to = ref<string | null>(null)
const error = ref<string | undefined>()
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  to.value = props.name.name
  error.value = undefined
})
const target = computed(() => mergeTarget(props.uses, props.name.name, to.value ?? ''))
const unchanged = computed(() => (to.value ?? '').trim() === props.name.name)

async function save() {
  const name = (to.value ?? '').trim()
  if (!name) {
    error.value = 'A name is needed.'
    return
  }
  saving.value = true
  try {
    const merging = target.value
    const result = await licensesApi.rename(renameFields[props.field], props.name.name, name)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    const where = usesText(result.items, result.templates)
    notify.success(merging ? `${props.name.name} merged into ${name}` : `Renamed to ${name}`, `In ${where}.`)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) error.value = e.field('to') ?? e.message
    else notify.error(e, 'Not renamed')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Rename the ${nameLabels[field].one} ${name.name}`"
    modal
    :style="{ width: '30rem' }"
  >
    <form id="licence-rename" class="form" novalidate @submit.prevent="save">
      <p class="muted">Used by {{ usesText(name.items, name.templates) }}: each takes the new name.</p>
      <div class="field">
        <label for="lrn-to">New name</label>
        <NameField v-model="to" :field="field" input-id="lrn-to" :invalid="!!error" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <p v-if="target" class="notice" role="status" data-test="rename-merges">
        <i class="pi pi-arrow-right-arrow-left" aria-hidden="true" />
        Merges into {{ target.name }}: {{ usesText(target.items, target.templates) }} use it already. The two become
        one.
      </p>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-rename"
        :label="target ? 'Merge' : 'Rename'"
        :disabled="unchanged"
        :loading="saving"
        data-test="rename-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.notice {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-warn-soft);
}
</style>
