<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import { ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import HostChoice from '@/views/hosts/HostChoice.vue'
import { platforms } from './agentFormat'
import TokenCreated from './TokenCreated.vue'

/**
 * Add server (docs/windows-sensor-plan.md §3.3, §10.1; docs/linux-sensor-plan.md §11.1): the platform first, then a join
 * token for a host, an existing one or a new one made right here, with an optional name and validity. Opened from
 * Admin → Servers (choose the host) or from a host's page (preselected). A re-join token comes from a server's Agent tab,
 * for the server's own platform.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ hostId?: string | null; rejoin?: { sensorId: string; name: string } | null }>()

const platform = ref<S['AgentPlatform']>('Windows')
const host = ref<string | null>(null)
const hostReady = ref(false)
const choice = ref<InstanceType<typeof HostChoice>>()
const name = ref('')
const hours = ref<number | null>(24)
const created = ref<S['AgentJoinTokenCreated'] | null>(null)
watch(
  visible,
  (open) => {
    if (open) {
      platform.value = 'Windows'
      host.value = props.hostId ?? null
      name.value = ''
      hours.value = 24
      created.value = null
    }
  },
  { immediate: true },
)

const { run, busy } = useActions()
async function create() {
  const result = await run(
    async () => {
      if (props.rejoin) return api.agents.rejoinToken(props.rejoin.sensorId, hours.value)
      // A new host is made here, once; the token is for it.
      const on = await choice.value!.resolve()
      if (!on) return null
      return api.agents.createToken({
        platform: platform.value,
        hostId: on.id,
        name: name.value.trim() || null,
        validHours: hours.value,
      })
    },
    { invalidate: [keys.agents] },
  )
  if (result) created.value = result
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="rejoin ? `Re-join ${rejoin.name}` : 'Add server'"
    modal
    :style="{ width: created?.item.platform === 'Linux' ? '52rem' : '36rem' }"
    :breakpoints="{ '640px': '95vw' }"
  >
    <TokenCreated v-if="created" :created="created" />
    <form v-else class="form" @submit.prevent="create">
      <p v-if="rejoin" class="muted">
        Joining with this token replaces the server's key and keeps its history. Run the agent on the server again and
        choose <em>Re-join…</em>, or use the command line.
      </p>
      <div v-if="!rejoin" class="field">
        <label id="server-platform">Platform</label>
        <SelectButton v-model="platform" :options="platforms" :allow-empty="false" aria-labelledby="server-platform" />
        <small class="field-hint">The token is for this platform only: an agent of the other is refused.</small>
      </div>
      <HostChoice
        v-if="!rejoin"
        ref="choice"
        v-model:host-id="host"
        v-model:ready="hostReady"
        :active="visible"
        :allow-new="!hostId"
        hint="A server is a sensor of a host. Its details arrive when it joins."
      />
      <div v-if="!rejoin" class="field">
        <label for="server-name">Name <span class="muted">(optional)</span></label>
        <InputText id="server-name" v-model="name" :placeholder="platform" maxlength="200" />
      </div>
      <div class="field">
        <label for="server-hours">Valid for (hours)</label>
        <InputNumber v-model="hours" input-id="server-hours" :min="1" :max="168" show-buttons />
        <small class="field-hint">24 hours by default, at most 7 days. Single use.</small>
      </div>
    </form>
    <template #footer>
      <Button v-if="created" label="Done" @click="visible = false" />
      <template v-else>
        <Button label="Cancel" severity="secondary" text @click="visible = false" />
        <Button
          label="Create token"
          icon="pi pi-key"
          :loading="busy"
          :disabled="!rejoin && !hostReady"
          @click="create"
        />
      </template>
    </template>
  </Dialog>
</template>
