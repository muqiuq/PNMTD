<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, sensorRequest, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import MikrotikFields from './MikrotikFields.vue'
import {
  changeHints,
  draftErrors,
  draftFromRouter,
  MIKROTIK_FIELDS,
  mikrotikRequestFromDraft,
  newMikrotikDraft,
  type MikrotikDraft,
} from './mikrotikDraft'

/**
 * An existing router's settings, as on the sensor form (docs/mikrotik-plan.md §11.1), saved with the
 * sensor. Before that the dialog says what the change means on the router.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail']; isAdmin: boolean }>()

const queryClient = useQueryClient()
const session = useSession()
const { invalidate, notify } = useActions()
const draft = ref<MikrotikDraft>(newMikrotikDraft())
const busy = ref(false)
const serverErrors = ref<Record<string, string | undefined>>({})

watch(
  visible,
  (open) => {
    if (open && props.sensor.mikrotik) {
      draft.value = draftFromRouter(props.sensor.mikrotik)
      serverErrors.value = {}
    }
  },
  { immediate: true },
)

const clientErrors = computed(() => draftErrors(draft.value))
const errors = computed(() => ({
  ...serverErrors.value,
  ...Object.fromEntries(Object.entries(clientErrors.value).filter(([, v]) => v)),
}))
const invalid = computed(() => Object.values(clientErrors.value).some((e) => e))
const hints = computed(() => (props.sensor.mikrotik ? changeHints(props.sensor.mikrotik, draft.value) : []))

async function save() {
  if (invalid.value) return
  busy.value = true
  serverErrors.value = {}
  try {
    const updated = await api.sensors.update(
      props.sensor.id,
      sensorRequest(props.sensor, { mikrotik: mikrotikRequestFromDraft(draft.value) }),
    )
    queryClient.setQueryData(keys.sensor(props.sensor.id), updated)
    await invalidate([keys.mikrotik, keys.host(updated.host.id)])
    notify.success(`Settings of ${updated.name} saved`)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      serverErrors.value = Object.fromEntries(MIKROTIK_FIELDS.map((f) => [f, e.field(f)]))
      if (!MIKROTIK_FIELDS.some((f) => e.field(f))) {
        notify.error(e, 'Not saved')
      }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Settings of ${sensor.name}`"
    modal
    :style="{ width: '44rem' }"
    :breakpoints="{ '760px': '95vw' }"
  >
    <form id="mikrotik-settings-form" class="body" novalidate @submit.prevent="save">
      <MikrotikFields v-model="draft" :errors="errors" :is-admin="isAdmin" :wire-guard="!!session.info?.wireGuard" />
    </form>
    <template #footer>
      <div class="footer">
        <div v-if="hints.length" class="hints" aria-live="polite">
          <Message v-for="h in hints" :key="h" severity="info" :closable="false" size="small" class="hint">{{
            h
          }}</Message>
        </div>
        <div class="buttons">
          <Button label="Cancel" severity="secondary" text @click="visible = false" />
          <Button type="submit" form="mikrotik-settings-form" label="Save" :loading="busy" :disabled="invalid" />
        </div>
      </div>
    </template>
  </Dialog>
</template>

<style scoped>
.body :deep(.mikrotik-fields) {
  border: 0;
  padding: 0;
}

.body :deep(.mikrotik-fields > legend) {
  display: none;
}

.footer {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  width: 100%;
}

.hints {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.hint {
  margin: 0;
}

.buttons {
  display: flex;
  justify-content: flex-end;
  gap: var(--pnmt-space-2);
}
</style>
