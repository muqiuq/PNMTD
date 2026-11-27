<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, todayIso } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import DayField from './DayField.vue'

/**
 * Mark reviewed… (docs/licences-plan.md §5.7): one item or many (a list's selection), on a day (today by default), with an optional
 * note. The reviews' history is the audit's.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ ids: string[]; label: string }>()
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()
const on = ref<string | null>(todayIso())
const note = ref('')
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  on.value = todayIso()
  note.value = ''
  errors.value = {}
})

async function save() {
  saving.value = true
  try {
    if (props.ids.length === 1)
      await licensesApi.review(props.ids[0], { on: on.value, note: note.value.trim() || null })
    else await licensesApi.reviewMany(props.ids, { on: on.value, note: note.value.trim() || null })
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(props.ids.length === 1 ? 'Marked reviewed' : `${props.ids.length} marked reviewed`)
    visible.value = false
    emit('done')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = { on: e.field('on'), note: e.field('note') }
    else notify.error(e, 'Not marked reviewed')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" header="Mark reviewed" modal :style="{ width: '28rem' }">
    <form id="licence-review" class="form" novalidate @submit.prevent="save">
      <p class="muted">{{ label }}: still needed, on the right hosts, at the right price.</p>
      <div class="field">
        <label for="lr-on">Reviewed on</label>
        <DayField v-model="on" input-id="lr-on" :invalid="!!errors.on" />
        <small v-if="errors.on" class="field-error">{{ errors.on }}</small>
      </div>
      <div class="field">
        <label for="lr-note">Note</label>
        <InputText id="lr-note" v-model="note" :invalid="!!errors.note" placeholder="Seats checked against the users" />
        <small v-if="errors.note" class="field-error">{{ errors.note }}</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="licence-review" label="Mark reviewed" :loading="saving" data-test="review-save" />
    </template>
  </Dialog>
</template>
