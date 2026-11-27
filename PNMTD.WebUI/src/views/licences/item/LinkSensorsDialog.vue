<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, type LicenseTarget } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import TargetPicker from './TargetPicker.vue'

/**
 * Link sensors… (docs/licences-plan.md §5.4): the sensors that watch the item (the line's ping, the SaaS's HTTP check), several at
 * once, with a note. Their states don't change the item's verdict; their pages show the item's provider and support. Item sensors
 * can't be linked: they are the item already.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ licenseId: string; display: string }>()
const emit = defineEmits<{ done: [] }>()
const queryClient = useQueryClient()
const notify = useNotify()
const chosen = ref<LicenseTarget[]>([])
const note = ref('')
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  chosen.value = []
  note.value = ''
  errors.value = {}
})

function pick(target: LicenseTarget) {
  if (target.assigned) {
    notify.info(`${target.name} is linked already`)
    return
  }
  if (!chosen.value.some((c) => c.id === target.id)) chosen.value = [...chosen.value, target]
}

async function save() {
  if (!chosen.value.length) return
  saving.value = true
  errors.value = {}
  try {
    await licensesApi.link(
      props.licenseId,
      chosen.value.map((c) => c.id),
      note.value.trim() || null,
    )
    await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
    notify.success(
      chosen.value.length === 1 ? `${chosen.value[0].name} linked` : `${chosen.value.length} sensors linked`,
    )
    visible.value = false
    emit('done')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = { sensorIds: e.field('sensorIds'), note: e.field('note') }
    else notify.error(e, 'Not linked')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Link sensors to ${display}`" modal :style="{ width: '34rem' }">
    <form id="licence-link" class="form" novalidate @submit.prevent="save">
      <p class="muted">
        The sensors that watch it: the line's ping, the service's HTTP check. Their pages then show its provider,
        support and customer number; their states don't change its own.
      </p>
      <div class="field">
        <label for="ll-sensor">Sensors</label>
        <TargetPicker kind="sensor" :license-id="licenseId" input-id="ll-sensor" @pick="pick" />
        <small v-if="errors.sensorIds" class="field-error">{{ errors.sensorIds }}</small>
      </div>
      <ul v-if="chosen.length" class="chosen">
        <li v-for="c in chosen" :key="c.id">
          <i class="pi pi-wave-pulse muted" aria-hidden="true" />
          <span class="name ellipsis"
            >{{ c.name }}<span v-if="c.hostName" class="muted"> on {{ c.hostName }}</span></span
          >
          <Button
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Remove ${c.name}`"
            @click="chosen = chosen.filter((x) => x.id !== c.id)"
          />
        </li>
      </ul>
      <div class="field">
        <label for="ll-note">Note</label>
        <InputText id="ll-note" v-model="note" placeholder="the primary line" :invalid="!!errors.note" />
        <small v-if="errors.note" class="field-error">{{ errors.note }}</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="licence-link"
        :label="chosen.length > 1 ? `Link ${chosen.length} sensors` : 'Link'"
        :disabled="!chosen.length"
        :loading="saving"
        data-test="link-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.chosen {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--pnmt-space-1);
}

.chosen li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.name {
  flex: 1;
}
</style>
