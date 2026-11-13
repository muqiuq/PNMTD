<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'

/**
 * The tunnel network's settings (§3.3). The external host and port can come from .env
 * (PNMTD_WG_EXTERNAL_HOST, PNMTD_WG_EXTERNAL_PORT), which wins: those fields are then locked. A change of
 * what join scripts carry applies to new scripts; the answer lists the routers that need theirs again.
 */
const queryClient = useQueryClient()
const { notify } = useActions()
const query = useQuery({ queryKey: [...keys.mikrotik, 'network'], queryFn: api.wireGuard.network })
const n = computed(() => query.data.value)

const prefix = ref('')
const confirmPrefix = ref('')
const host = ref('')
const port = ref<number | null>(null)
const keepalive = ref<number | null>(25)
const mtu = ref<number | null>(1420)
const stale = ref<number | null>(180)
const ports = ref('')
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)
const needNew = ref<string[]>([])

function reset(v: S['WireGuardNetworkView'] | undefined) {
  if (!v) return
  prefix.value = v.prefix
  confirmPrefix.value = ''
  host.value = v.externalHostLocked ? (v.externalHostInEffect ?? '') : (v.externalHost ?? '')
  port.value = v.externalPortLocked ? v.externalPortInEffect : v.externalPort
  keepalive.value = v.keepaliveSeconds
  mtu.value = v.mtu
  stale.value = v.staleHandshakeSeconds
  ports.value = v.managementPorts.join(', ')
}
watch(n, reset, { immediate: true })
const prefixChanged = computed(() => !!n.value && prefix.value.trim() !== n.value.prefix)

async function save() {
  if (!n.value) return
  busy.value = true
  errors.value = {}
  needNew.value = []
  try {
    const saved = await api.wireGuard.saveNetwork({
      prefix: prefix.value.trim(),
      confirmPrefix: prefixChanged.value ? confirmPrefix.value.trim() : null,
      externalHost: n.value.externalHostLocked ? null : host.value.trim() || null,
      externalPort: n.value.externalPortLocked ? null : port.value,
      keepaliveSeconds: keepalive.value ?? 25,
      mtu: mtu.value ?? 1420,
      staleHandshakeSeconds: stale.value ?? 180,
      managementPorts: ports.value
        .split(/[\s,]+/)
        .filter(Boolean)
        .map(Number),
    })
    queryClient.setQueryData([...keys.mikrotik, 'network'], saved.network)
    await queryClient.invalidateQueries({ queryKey: [...keys.mikrotik] })
    needNew.value = saved.needNewScripts
    notify.success(
      'Network saved',
      saved.needNewScripts.length ? 'Some routers need their new join script.' : undefined,
    )
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      const fields = [
        'prefix',
        'confirmPrefix',
        'externalHost',
        'externalPort',
        'keepaliveSeconds',
        'mtu',
        'staleHandshakeSeconds',
        'managementPorts',
      ]
      errors.value = Object.fromEntries(fields.map((f) => [f, e.field(f)]))
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ErrorState v-if="query.isError.value && !n" :error="query.error.value" what="the network" @retry="query.refetch()" />
  <form v-else-if="n" class="panel panel-body form-grid form-box" novalidate @submit.prevent="save">
    <div class="field">
      <label for="wg-host">External host</label>
      <InputText
        id="wg-host"
        v-model="host"
        class="mono"
        :disabled="n.externalHostLocked"
        :invalid="!!errors.externalHost"
      />
      <small v-if="errors.externalHost" class="field-error">{{ errors.externalHost }}</small>
      <small v-else-if="n.externalHostLocked" class="field-hint">Set by PNMTD_WG_EXTERNAL_HOST in .env.</small>
      <small v-else class="field-hint"
        >What routers and tunnels dial. A DNS name is best. Empty: PublicBaseUrl's host{{
          n.externalHostInEffect ? ` (${n.externalHostInEffect})` : ''
        }}.</small
      >
    </div>
    <div class="field">
      <label for="wg-port">External port</label>
      <InputNumber
        v-model="port"
        input-id="wg-port"
        :min="1"
        :max="65535"
        :use-grouping="false"
        :disabled="n.externalPortLocked"
        :placeholder="String(n.publishedPort)"
        :invalid="!!errors.externalPort"
      />
      <small v-if="errors.externalPort" class="field-error">{{ errors.externalPort }}</small>
      <small v-else-if="n.externalPortLocked" class="field-hint">Set by PNMTD_WG_EXTERNAL_PORT in .env.</small>
      <small v-else class="field-hint"
        >Different from the published port ({{ n.publishedPort }}/udp) when an upstream router forwards another
        one.</small
      >
    </div>
    <div class="field">
      <label for="wg-prefix">Network</label>
      <InputText id="wg-prefix" v-model="prefix" class="mono" :invalid="!!errors.prefix" />
      <small v-if="errors.prefix" class="field-error">{{ errors.prefix }}</small>
      <small v-else class="field-hint">Its first /30 is PNMTD's; routers and tunnels get the addresses after it.</small>
    </div>
    <div v-if="prefixChanged" class="field">
      <label for="wg-confirm">Type the new network to confirm</label>
      <InputText id="wg-confirm" v-model="confirmPrefix" class="mono" :invalid="!!errors.confirmPrefix" />
      <small v-if="errors.confirmPrefix" class="field-error">{{ errors.confirmPrefix }}</small>
      <small v-else class="field-hint bad"
        >Every router and tunnel gets a new address and needs its new script or file.</small
      >
    </div>
    <div class="field">
      <label for="wg-keepalive">Keepalive (seconds)</label>
      <InputNumber
        v-model="keepalive"
        input-id="wg-keepalive"
        :min="10"
        :max="120"
        :invalid="!!errors.keepaliveSeconds"
      />
      <small v-if="errors.keepaliveSeconds" class="field-error">{{ errors.keepaliveSeconds }}</small>
      <small v-else class="field-hint">Keeps the routers' NAT open, so PNMTD can reach them at any time.</small>
    </div>
    <div class="field">
      <label for="wg-mtu">MTU</label>
      <InputNumber
        v-model="mtu"
        input-id="wg-mtu"
        :min="1280"
        :max="1500"
        :use-grouping="false"
        :invalid="!!errors.mtu"
      />
      <small v-if="errors.mtu" class="field-error">{{ errors.mtu }}</small>
      <small v-else class="field-hint">Lower for PPPoE or LTE links.</small>
    </div>
    <div class="field">
      <label for="wg-stale">A tunnel is down after (seconds)</label>
      <InputNumber
        v-model="stale"
        input-id="wg-stale"
        :min="30"
        :max="3600"
        :invalid="!!errors.staleHandshakeSeconds"
      />
      <small v-if="errors.staleHandshakeSeconds" class="field-error">{{ errors.staleHandshakeSeconds }}</small>
      <small v-else class="field-hint">Without a handshake; at least three keepalives.</small>
    </div>
    <div class="field">
      <label for="wg-ports">Management ports</label>
      <InputText id="wg-ports" v-model="ports" class="mono" :invalid="!!errors.managementPorts" />
      <small v-if="errors.managementPorts" class="field-error">{{ errors.managementPorts }}</small>
      <small v-else class="field-hint">What custom tunnels may reach on routers with management access.</small>
    </div>
    <Message v-if="needNew.length" severity="warn" :closable="false" class="wide">
      These routers and tunnels need their new join script or file: {{ needNew.join(', ') }}.
    </Message>
    <div class="form-actions wide">
      <Button type="submit" label="Save" :loading="busy" />
      <Button label="Reset" severity="secondary" text @click="reset(n)" />
    </div>
  </form>
</template>

<style scoped>
.form-box {
  max-width: 760px;
}

.mono :deep(input),
input.mono {
  font-family: var(--pnmt-mono);
}

.bad {
  color: var(--pnmt-fail);
}
</style>
