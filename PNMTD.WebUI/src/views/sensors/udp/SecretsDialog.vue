<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { randomBytes, toHex } from '@/format/bytes'
import { KEY_BYTES, secretError, TOKEN_BYTES } from './udpDraft'

/**
 * A node's key and token, set by hand or regenerated (docs/udp-iot-plan.md §4.3, §6.2). Either takes
 * effect at once: the device stops working until it has the new value, and its sessions, counter
 * window and retransmission cache are reset. The server's warnings (the same key on another node)
 * stay in the dialog until it is closed.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensorId: string; name: string; mode: 'manual' | 'regenerate' }>()

const { invalidate, notify } = useActions()
const key = ref('')
const token = ref('')
const regenKey = ref(true)
const regenToken = ref(false)
const busy = ref(false)
const serverErrors = ref<Record<string, string | undefined>>({})
const warnings = ref<string[] | null>(null)

watch(visible, (open) => {
  if (open) {
    key.value = token.value = ''
    regenKey.value = true
    regenToken.value = false
    serverErrors.value = {}
    warnings.value = null
  }
})

const keyError = computed(() => secretError(key.value, KEY_BYTES) ?? serverErrors.value.key)
const tokenError = computed(() => secretError(token.value, TOKEN_BYTES) ?? serverErrors.value.token)
const canSave = computed(() =>
  props.mode === 'manual'
    ? (key.value.trim() !== '' || token.value.trim() !== '') &&
      !secretError(key.value, KEY_BYTES) &&
      !secretError(token.value, TOKEN_BYTES)
    : regenKey.value || regenToken.value,
)

async function save() {
  busy.value = true
  serverErrors.value = {}
  try {
    const result =
      props.mode === 'manual'
        ? await api.udp.setSecrets(props.sensorId, {
            key: key.value.trim() || null,
            token: token.value.trim() || null,
          })
        : await api.udp.regenerate(props.sensorId, { key: regenKey.value, token: regenToken.value })
    await invalidate([keys.sensor(props.sensorId), keys.udp])
    const what =
      props.mode === 'manual'
        ? [key.value.trim() && 'key', token.value.trim() && 'token'].filter(Boolean).join(' and ')
        : [regenKey.value && 'key', regenToken.value && 'token'].filter(Boolean).join(' and ')
    notify.success(`New ${what} for ${props.name}`, 'Sessions and the counter are reset.')
    if (result.warnings.length) {
      warnings.value = result.warnings
    } else {
      visible.value = false
    }
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      serverErrors.value = { key: e.field('key'), token: e.field('token') }
    } else {
      notify.error(e, 'Not changed')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="mode === 'manual' ? `Set key or token of ${name}` : `Regenerate key or token of ${name}`"
    modal
    :style="{ width: '34rem' }"
    :breakpoints="{ '600px': '95vw' }"
  >
    <template v-if="warnings">
      <p class="dialog-text">Saved. The device needs the new value now.</p>
      <Message v-for="w in warnings" :key="w" severity="warn" :closable="false" class="warning">{{ w }}</Message>
    </template>
    <template v-else>
      <Message severity="warn" :closable="false" class="warning">
        The device stops working until it has the new value. Its sessions and its counter are reset, and packets under
        the old value are rejected from now on.
      </Message>
      <form v-if="mode === 'manual'" class="form" novalidate @submit.prevent="canSave && save()">
        <div class="field">
          <label for="secret-key">Key <span class="muted">(32 hex digits, empty: unchanged)</span></label>
          <div class="with-button">
            <InputText
              id="secret-key"
              v-model="key"
              class="mono grow"
              autocomplete="off"
              spellcheck="false"
              autofocus
              :invalid="!!keyError"
            />
            <Button
              label="Generate"
              icon="pi pi-refresh"
              size="small"
              severity="secondary"
              outlined
              @click="key = toHex(randomBytes(KEY_BYTES))"
            />
          </div>
          <small v-if="keyError" class="field-error">{{ keyError }}</small>
          <small v-else class="field-hint">Case-insensitive; spaces, colons and 0x are fine.</small>
        </div>
        <div class="field">
          <label for="secret-token">Token <span class="muted">(8 hex digits, empty: unchanged)</span></label>
          <div class="with-button">
            <InputText
              id="secret-token"
              v-model="token"
              class="mono grow"
              autocomplete="off"
              spellcheck="false"
              :invalid="!!tokenError"
            />
            <Button
              label="Generate"
              icon="pi pi-refresh"
              size="small"
              severity="secondary"
              outlined
              @click="token = toHex(randomBytes(TOKEN_BYTES))"
            />
          </div>
          <small v-if="tokenError" class="field-error">{{ tokenError }}</small>
        </div>
      </form>
      <div v-else class="checks">
        <label><Checkbox v-model="regenKey" binary /> The key (16 bytes, for the keyed suites)</label>
        <label><Checkbox v-model="regenToken" binary /> The token (4 bytes, for TOKEN32)</label>
      </div>
    </template>
    <template #footer>
      <template v-if="warnings">
        <Button label="Close" @click="visible = false" />
      </template>
      <template v-else>
        <Button label="Cancel" severity="secondary" text @click="visible = false" />
        <Button
          :label="mode === 'manual' ? 'Set' : 'Regenerate'"
          severity="danger"
          :loading="busy"
          :disabled="!canSave"
          @click="save"
        />
      </template>
    </template>
  </Dialog>
</template>

<style scoped>
.dialog-text {
  margin-top: 0;
}

.warning {
  margin-bottom: var(--pnmt-space-3);
}

.with-button {
  display: flex;
  gap: var(--pnmt-space-2);
}

.grow {
  flex: 1;
  min-width: 0;
}

.mono {
  font-family: var(--pnmt-mono);
}

.checks {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.checks label {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
