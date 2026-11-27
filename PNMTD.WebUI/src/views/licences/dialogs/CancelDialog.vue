<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi } from '@/api/licenses'
import { formatDay } from '@/format'
import { useNotify } from '@/composables/useNotify'
import DayField from './DayField.vue'

/**
 * Cancel… (docs/licences-plan.md §5.6): it runs to its expiry and isn't renewed any more; one without an expiry is asked for its last
 * day. Reactivate takes it back.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ item: { id: string; display: string; name: string; expiresOn?: string | null } }>()
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()
const endsOn = ref<string | null>(null)
const error = ref<string | undefined>()
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  endsOn.value = null
  error.value = undefined
})

async function save() {
  saving.value = true
  try {
    await licensesApi.cancel(props.item.id, props.item.expiresOn ? null : endsOn.value)
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(`${props.item.display} cancelled`)
    visible.value = false
    emit('done')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) error.value = e.field('endsOn') ?? e.message
    else notify.error(e, 'Not cancelled')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Cancel ${item.display}`" modal :style="{ width: '28rem' }">
    <form id="licence-cancel" class="form" novalidate @submit.prevent="save">
      <p v-if="item.expiresOn">
        {{ item.name }} runs until {{ formatDay(item.expiresOn) }} and isn't renewed after it. Its warnings and its end
        follow that day; its charges stop after it.
      </p>
      <template v-else>
        <p>{{ item.name }} has no expiry: its last day becomes its expiry.</p>
        <div class="field">
          <label for="lc-ends">Last day</label>
          <DayField v-model="endsOn" input-id="lc-ends" :invalid="!!error" />
          <small v-if="error" class="field-error">{{ error }}</small>
        </div>
      </template>
    </form>
    <template #footer>
      <Button label="Keep it" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-cancel"
        label="Cancel it"
        severity="danger"
        :loading="saving"
        data-test="cancel-save"
      />
    </template>
  </Dialog>
</template>
