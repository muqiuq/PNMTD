<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { formatDuration } from '@/format'
import { formatBytes } from '@/format/bytes'
import { otaEstimate } from '@/format/udp'

/**
 * "Update firmware…" (docs/udp-iot-plan.md §4.11, §6.2, §6.6), Admin only: an uploaded image, one
 * or several nodes, the chunk size, the expiry and the offers, with the cost on the wire before it
 * starts (§3.13). One job per node; the server refuses nodes with an open update or no keyed suite.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = withDefaults(
  defineProps<{
    /** Nodes chosen already (the node's page, the admin page's selection); none: pick them here. */
    sensorIds?: string[]
    names?: string[]
    firmwareId?: string | null
  }>(),
  { sensorIds: () => [], names: () => [], firmwareId: null },
)

const { invalidate, notify } = useActions()
const firmware = useQuery({ queryKey: [...keys.udp, 'firmware'], queryFn: api.udp.firmware, enabled: visible })
const pickNodes = computed(() => !props.sensorIds.length)
const nodes = useQuery({
  queryKey: [...keys.udp, 'nodes', 'all'],
  queryFn: () => api.udp.nodes({ pageSize: 500, sort: 'name' }),
  enabled: computed(() => visible.value && pickNodes.value),
})

const image = ref<string | null>(null)
const picked = ref<string[]>([])
const chunk = ref<number | null>(448)
const days = ref<number | null>(7)
const offers = ref<number | null>(20)
const busy = ref(false)
const errors = ref<Record<string, string | undefined>>({})
const result = ref<S['UdpOtaJobsCreated'] | null>(null)

watch(visible, (open) => {
  if (open) {
    image.value = props.firmwareId
    picked.value = []
    chunk.value = 448
    days.value = 7
    offers.value = 20
    errors.value = {}
    result.value = null
  }
})

const chosen = computed(() => firmware.data.value?.find((f) => f.id === image.value) ?? null)
const targets = computed(() => (pickNodes.value ? picked.value : props.sensorIds))
const images = computed(() =>
  (firmware.data.value ?? []).map((f) => ({ label: `${f.name} ${f.version} · ${formatBytes(f.size)}`, value: f.id })),
)
const nodeOptions = computed(() =>
  (nodes.data.value?.items ?? []).map((n) => ({
    label: `${n.name} (${n.host.name}, id ${n.deviceId})`,
    value: n.sensorId,
    disabled: !!n.openUpdate,
  })),
)
const estimate = computed(() =>
  chosen.value && chunk.value && chunk.value >= 64 ? otaEstimate(chosen.value.size, chunk.value) : null,
)
const overhead = computed(() =>
  estimate.value && chosen.value ? Math.round((estimate.value.bytesOnTheWire / chosen.value.size - 1) * 100) : 0,
)
const valid = computed(
  () => !!chosen.value && targets.value.length > 0 && !!chunk.value && chunk.value >= 64 && chunk.value <= 480,
)

async function start() {
  busy.value = true
  errors.value = {}
  try {
    const created = await api.udp.startUpdates({
      firmwareId: image.value!,
      sensorIds: targets.value,
      chunkSize: chunk.value,
      expiresInDays: days.value,
      maxOfferAttempts: offers.value,
    })
    await invalidate([keys.udp, keys.sensors])
    for (const id of targets.value) {
      await invalidate([keys.sensor(id)])
    }
    if (created.created.length) {
      notify.success(
        `Update queued for ${created.created.length} node${created.created.length === 1 ? '' : 's'}`,
        'It is offered with the next packet of each.',
      )
    }
    if (created.refused.length) {
      result.value = created
    } else {
      visible.value = false
    }
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = {
        firmwareId: e.field('firmwareId'),
        sensorIds: e.field('sensorIds'),
        chunkSize: e.field('chunkSize'),
        expiresInDays: e.field('expiresInDays'),
        maxOfferAttempts: e.field('maxOfferAttempts'),
      }
    } else {
      notify.error(e, 'Not started')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Update firmware"
    modal
    :style="{ width: '40rem' }"
    :breakpoints="{ '700px': '96vw' }"
  >
    <template v-if="result">
      <p class="dialog-text">
        {{ result.created.length }} update{{ result.created.length === 1 ? '' : 's' }} queued;
        {{ result.refused.length }} node{{ result.refused.length === 1 ? '' : 's' }} refused:
      </p>
      <Message v-for="r in result.refused" :key="r.sensorId" severity="warn" :closable="false" class="block">
        <strong>{{ r.name }}</strong>: {{ r.reason }}
      </Message>
    </template>
    <form v-else class="form" novalidate @submit.prevent="valid && start()">
      <p v-if="!pickNodes" class="dialog-text">
        {{ names.length ? names.join(', ') : `${sensorIds.length} node${sensorIds.length === 1 ? '' : 's'}` }}
      </p>
      <Message v-if="targets.length > 1" severity="info" :closable="false">
        Try one node first: an image that does not start on one device does not start on the others either.
      </Message>
      <div class="field">
        <label for="ota-image">Image</label>
        <Select
          v-model="image"
          input-id="ota-image"
          :options="images"
          option-label="label"
          option-value="value"
          :loading="firmware.isLoading.value"
          placeholder="An uploaded .bin"
          :invalid="!!errors.firmwareId"
          empty-message="No image uploaded yet (Admin › UDP nodes › Firmware)."
        />
        <small v-if="errors.firmwareId" class="field-error">{{ errors.firmwareId }}</small>
        <small v-else-if="chosen" class="field-hint mono">SHA-256 {{ chosen.sha256 }}</small>
      </div>
      <div v-if="pickNodes" class="field">
        <label for="ota-nodes">Nodes</label>
        <MultiSelect
          v-model="picked"
          input-id="ota-nodes"
          :options="nodeOptions"
          option-label="label"
          option-value="value"
          option-disabled="disabled"
          filter
          display="chip"
          placeholder="Choose nodes"
          :loading="nodes.isLoading.value"
          :invalid="!!errors.sensorIds"
        />
        <small v-if="errors.sensorIds" class="field-error">{{ errors.sensorIds }}</small>
        <small v-else class="field-hint">Nodes with an open update are not offered.</small>
      </div>
      <div class="grid">
        <div class="field">
          <label for="ota-chunk">Chunk size (bytes)</label>
          <InputNumber
            v-model="chunk"
            input-id="ota-chunk"
            :min="64"
            :max="480"
            :use-grouping="false"
            :invalid="!!errors.chunkSize"
          />
          <small v-if="errors.chunkSize" class="field-error">{{ errors.chunkSize }}</small>
          <small v-else class="field-hint">64–480; 448 fits an 8-byte tag and a 4-byte id.</small>
        </div>
        <div class="field">
          <label for="ota-days">Offer expires (days)</label>
          <InputNumber v-model="days" input-id="ota-days" :min="1" :max="365" :invalid="!!errors.expiresInDays" />
          <small v-if="errors.expiresInDays" class="field-error">{{ errors.expiresInDays }}</small>
        </div>
        <div class="field">
          <label for="ota-offers">Offers at most</label>
          <InputNumber
            v-model="offers"
            input-id="ota-offers"
            :min="1"
            :max="1000"
            :invalid="!!errors.maxOfferAttempts"
          />
          <small v-if="errors.maxOfferAttempts" class="field-error">{{ errors.maxOfferAttempts }}</small>
          <small v-else class="field-hint">Re-offered in later slots until the device answers.</small>
        </div>
      </div>
      <div v-if="estimate && chosen" class="estimate">
        <div class="sub">Per node, over IPv4 with an 8-byte tag</div>
        <dl class="facts">
          <dt>Round trips</dt>
          <dd>{{ estimate.roundTrips.toLocaleString() }} (one GET and one chunk each)</dd>
          <dt>On the wire</dt>
          <dd>
            {{ formatBytes(estimate.bytesOnTheWire) }}, {{ overhead }} % over the image's {{ formatBytes(chosen.size) }}
          </dd>
          <dt>Time</dt>
          <dd>
            about {{ formatDuration(estimate.secondsAt100Ms * 1000) }} at 100 ms per round trip (Wi-Fi, LTE-M), about
            {{ formatDuration(estimate.secondsAt2s * 1000) }} at 2 s (NB-IoT)
          </dd>
        </dl>
      </div>
      <p class="muted small">
        The device verifies the SHA-256 from the authenticated offer before installing; its bootloader should verify the
        image's signature too. PNMT counts the update as applied only when the device reports it.
      </p>
    </form>
    <template #footer>
      <template v-if="result">
        <Button label="Close" @click="visible = false" />
      </template>
      <template v-else>
        <Button label="Cancel" severity="secondary" text @click="visible = false" />
        <Button
          :label="targets.length > 1 ? `Update ${targets.length} nodes` : 'Start update'"
          icon="pi pi-upload"
          :loading="busy"
          :disabled="!valid"
          @click="start"
        />
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

.grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--pnmt-space-3);
}

.estimate {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.sub {
  font-weight: 500;
  margin-bottom: var(--pnmt-space-1);
}

.mono {
  font-family: var(--pnmt-mono);
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
  margin: 0;
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
