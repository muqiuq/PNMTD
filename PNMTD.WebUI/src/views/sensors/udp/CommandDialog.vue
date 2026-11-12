<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import HexDump from '@/components/HexDump.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { fromBase64, printableText, toBase64, toHex } from '@/format/bytes'
import { deliveryEstimate, MAX_COMMAND, type SecuritySuite } from '@/format/udp'
import {
  commandBytes,
  commandSuiteOptions,
  EXPIRY_OPTIONS,
  weakWarning,
  wireSummary,
  type InputMode,
} from './commandDraft'

/**
 * "New command" (docs/udp-iot-plan.md §6.4): the bytes as text (UTF-8) or hex with a live preview
 * and their size on the wire, the delivery mode, the suite, the expiry and the attempts, and when it
 * will probably go out. "Send again" opens it with a command's bytes and settings.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail']; prefill?: S['UdpCommandItem'] | null }>()

const { invalidate, notify } = useActions()
const now = useNow()
const udp = computed(() => props.sensor.udp!)

const mode = ref<InputMode>('text')
const input = ref('')
const delivery = ref<S['UdpCommandDelivery']>('ResultRequired')
const suite = ref<SecuritySuite | null>(null)
const expiry = ref(24)
const attempts = ref<number | null>(3)
const busy = ref(false)
const errors = ref<Record<string, string | undefined>>({})
const warnings = ref<string[] | null>(null)
const modeHint = ref<string | null>(null)

watch(visible, (open) => {
  if (!open) return
  const p = props.prefill
  errors.value = {}
  warnings.value = null
  modeHint.value = null
  if (p) {
    const bytes = fromBase64(p.payload)
    const text = printableText(bytes)
    mode.value = text !== null ? 'text' : 'hex'
    input.value = text ?? toHex(bytes)
    delivery.value = p.delivery
    suite.value = p.suite
    attempts.value = p.maxAttempts
  } else {
    mode.value = 'text'
    input.value = ''
    delivery.value = 'ResultRequired'
    suite.value = null
    expiry.value = 24
    attempts.value = 3
  }
})

// Switching the input keeps the bytes: text shows as hex, hex as text when it is text.
function switchMode(next: InputMode) {
  if (next === mode.value) return
  const current = commandBytes(mode.value, input.value)
  if (current.bytes) {
    if (next === 'hex') {
      input.value = toHex(current.bytes)
    } else {
      const text = printableText(current.bytes)
      if (current.bytes.length && text === null) {
        modeHint.value = 'These bytes are not printable text, so they stay hex.'
        return
      }
      input.value = text ?? ''
    }
  }
  modeHint.value = null
  mode.value = next
}

const parsed = computed(() => commandBytes(mode.value, input.value))
const summary = computed(() =>
  parsed.value.bytes ? wireSummary(parsed.value.bytes.length, suite.value, udp.value) : null,
)
const warning = computed(() => weakWarning(suite.value, udp.value))
const estimate = computed(() => deliveryEstimate(udp.value.lastSeenAt, props.sensor.interval, now.value))
const suites = computed(() => commandSuiteOptions(udp.value))
const modes = [
  { label: 'Text (UTF-8)', value: 'text' },
  { label: 'Hex', value: 'hex' },
]
const deliveries = [
  { label: 'Until a result arrives', value: 'ResultRequired' },
  { label: 'Fire and forget', value: 'FireAndForget' },
]

async function send() {
  const bytes = parsed.value.bytes
  if (!bytes) return
  busy.value = true
  errors.value = {}
  try {
    const created = await api.udp.queueCommand(props.sensor.id, {
      payload: toBase64(bytes),
      delivery: delivery.value,
      suite: suite.value,
      expiresInHours: expiry.value,
      maxAttempts: delivery.value === 'FireAndForget' ? 1 : (attempts.value ?? 3),
    })
    await invalidate([keys.udpNode(props.sensor.id), keys.sensor(props.sensor.id), keys.udp])
    notify.success(`Command ${created.command.cmdId} queued`, 'It goes out with the next packet of the node.')
    if (created.warnings.length) {
      warnings.value = created.warnings
    } else {
      visible.value = false
    }
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        payload: e.field('payload'),
        suite: e.field('suite'),
        expiresInHours: e.field('expiresInHours'),
        maxAttempts: e.field('maxAttempts'),
        delivery: e.field('delivery'),
      }
    } else {
      notify.error(e, 'Not queued')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="prefill ? `Send command ${prefill.cmdId} again` : 'New command'"
    modal
    :style="{ width: '44rem' }"
    :breakpoints="{ '760px': '96vw' }"
  >
    <template v-if="warnings">
      <p class="dialog-text">Queued. PNMTD says:</p>
      <Message v-for="w in warnings" :key="w" severity="warn" :closable="false" class="block">{{ w }}</Message>
    </template>
    <form v-else class="form" novalidate @submit.prevent="send">
      <div class="field">
        <div class="label-row">
          <label for="command-input">Command</label>
          <SelectButton
            :model-value="mode"
            :options="modes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-label="Input"
            @update:model-value="switchMode"
          />
        </div>
        <Textarea
          id="command-input"
          v-model="input"
          rows="3"
          auto-resize
          class="mono"
          spellcheck="false"
          autofocus
          :placeholder="mode === 'hex' ? '02 02 58' : 'reboot'"
          :invalid="!!(parsed.error || errors.payload)"
        />
        <small v-if="parsed.error || errors.payload" class="field-error">{{ parsed.error ?? errors.payload }}</small>
        <small v-else-if="modeHint" class="field-hint">{{ modeHint }}</small>
        <small v-else class="field-hint"
          >Opaque to PNMTD: the firmware decides what the bytes mean (a first byte as opcode is a common convention). At
          most {{ MAX_COMMAND }} bytes; under 64 for NB-IoT.</small
        >
      </div>
      <div v-if="parsed.bytes" class="preview">
        <HexDump :bytes="parsed.bytes" label="Command preview" />
        <div class="muted size">{{ summary }}</div>
      </div>
      <div class="grid">
        <div class="field">
          <label for="command-delivery">Delivery</label>
          <Select
            v-model="delivery"
            input-id="command-delivery"
            :options="deliveries"
            option-label="label"
            option-value="value"
          />
        </div>
        <div class="field">
          <label for="command-attempts">Attempts at most</label>
          <InputNumber
            v-model="attempts"
            input-id="command-attempts"
            :min="1"
            :max="100"
            :disabled="delivery === 'FireAndForget'"
            :invalid="!!errors.maxAttempts"
          />
          <small v-if="errors.maxAttempts" class="field-error">{{ errors.maxAttempts }}</small>
          <small v-else class="field-hint">Re-sent with later packets until the result arrives.</small>
        </div>
        <div class="field">
          <label for="command-suite">Suite</label>
          <Select
            v-model="suite"
            input-id="command-suite"
            :options="suites"
            option-label="label"
            option-value="value"
            :placeholder="suites[0]?.label"
            :invalid="!!errors.suite"
          />
          <small v-if="errors.suite" class="field-error">{{ errors.suite }}</small>
        </div>
        <div class="field">
          <span id="command-expiry-label" class="label">Expires</span>
          <SelectButton
            v-model="expiry"
            :options="EXPIRY_OPTIONS"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-labelledby="command-expiry-label"
          />
          <small v-if="errors.expiresInHours" class="field-error">{{ errors.expiresInHours }}</small>
          <small v-else class="field-hint">Not delivered by then: expired, never run late.</small>
        </div>
      </div>
      <Message v-if="warning" severity="warn" :closable="false">{{ warning }}</Message>
      <p class="muted estimate">{{ estimate }}</p>
    </form>
    <template #footer>
      <template v-if="warnings">
        <Button label="Close" @click="visible = false" />
      </template>
      <template v-else>
        <Button label="Cancel" severity="secondary" text @click="visible = false" />
        <Button label="Queue command" icon="pi pi-send" :loading="busy" :disabled="!parsed.bytes" @click="send" />
      </template>
    </template>
  </Dialog>
</template>

<style scoped>
.dialog-text {
  margin-top: 0;
}

.block {
  margin-bottom: var(--pnmt-space-2);
}

.label-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
}

.label-row label,
.label {
  font-weight: 500;
}

.mono {
  font-family: var(--pnmt-mono);
}

.preview {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.size {
  font-size: 0.929rem;
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.estimate {
  margin: 0;
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
