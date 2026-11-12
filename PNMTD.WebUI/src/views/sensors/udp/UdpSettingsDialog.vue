<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import { useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, sensorRequest, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import UdpNodeFields from './UdpNodeFields.vue'
import {
  changeHints,
  draftFromNode,
  KEY_BYTES,
  newUdpDraft,
  secretError,
  TOKEN_BYTES,
  udpRequest,
  type UdpDraft,
} from './udpDraft'

/**
 * An existing node's UDP settings, all of them as on the sensor form (docs/udp-iot-plan.md §6.1,
 * §6.2): device id, key family, suites, response suite, session, ACKs, replay protection, and a
 * new key or token if entered. Saved with the sensor (PUT /sensors/{id}); before that the dialog
 * says what the change means for the device. The server's warnings come as notes afterwards.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail'] }>()

const queryClient = useQueryClient()
const { invalidate, notify } = useActions()
const draft = ref<UdpDraft>(newUdpDraft())
const busy = ref(false)
const serverErrors = ref<Record<string, string | undefined>>({})

watch(
  visible,
  (open) => {
    if (open && props.sensor.udp) {
      draft.value = draftFromNode(props.sensor.udp)
      serverErrors.value = {}
    }
  },
  { immediate: true },
)

const fields = [
  'udp.deviceId',
  'udp.keyFamily',
  'udp.allowedSuites',
  'udp.responseSuite',
  'udp.sessionMode',
  'udp.ackMode',
  'udp.key',
  'udp.token',
] as const

// Typing clears the server's error for that field; the client's checks stay until fixed.
const errors = computed<Record<string, string | undefined>>(() => ({
  ...serverErrors.value,
  'udp.allowedSuites': !draft.value.allowedSuites.length
    ? 'Allow at least one suite.'
    : serverErrors.value['udp.allowedSuites'],
  'udp.key': (draft.value.manual && secretError(draft.value.key, KEY_BYTES)) || serverErrors.value['udp.key'],
  'udp.token': (draft.value.manual && secretError(draft.value.token, TOKEN_BYTES)) || serverErrors.value['udp.token'],
}))
const invalid = computed(
  () =>
    !draft.value.allowedSuites.length ||
    (draft.value.manual &&
      (!!secretError(draft.value.key, KEY_BYTES) || !!secretError(draft.value.token, TOKEN_BYTES))),
)
const hints = computed(() => (props.sensor.udp ? changeHints(props.sensor.udp, draft.value) : []))

async function save() {
  if (invalid.value) return
  busy.value = true
  serverErrors.value = {}
  try {
    const updated = await api.sensors.update(
      props.sensor.id,
      sensorRequest(props.sensor, { udp: udpRequest(draft.value) }),
    )
    queryClient.setQueryData(keys.sensor(props.sensor.id), updated)
    await invalidate([keys.udp, keys.host(updated.host.id)])
    notify.success(`UDP settings of ${updated.name} saved`)
    for (const warning of updated.udp?.warnings ?? []) {
      notify.info('Note', warning)
    }
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      serverErrors.value = Object.fromEntries(fields.map((f) => [f, e.field(f)]))
      if (!fields.some((f) => e.field(f))) {
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
    :header="`UDP settings of ${sensor.name}`"
    modal
    :style="{ width: '52rem' }"
    :breakpoints="{ '900px': '95vw' }"
    class="udp-settings"
  >
    <form id="udp-settings-form" class="body" novalidate @submit.prevent="save">
      <UdpNodeFields v-model="draft" :errors="errors" mode="edit" />
    </form>
    <!-- Next to Save, so they are in view however far the form is scrolled. -->
    <template #footer>
      <div class="footer">
        <div v-if="hints.length" class="hints" aria-live="polite">
          <Message v-for="h in hints" :key="h" severity="info" :closable="false" size="small" class="hint">{{
            h
          }}</Message>
        </div>
        <div class="buttons">
          <Button label="Cancel" severity="secondary" text @click="visible = false" />
          <Button type="submit" form="udp-settings-form" label="Save" :loading="busy" :disabled="invalid" />
        </div>
      </div>
    </template>
  </Dialog>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

/* The dialog has the title: the section needs no frame of its own. */
.body :deep(.udp-fields) {
  border: 0;
  padding: 0;
}

.body :deep(.udp-fields > legend) {
  display: none;
}

.footer {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  width: 100%;
}

/* No scroll box of its own: at the dialog's full height the form scrolls, not the hints. */
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
