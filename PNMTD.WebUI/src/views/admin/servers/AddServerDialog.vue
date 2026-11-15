<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { platforms } from './agentFormat'
import TokenCreated from './TokenCreated.vue'

/**
 * Add server (docs/windows-sensor-plan.md §3.3, §10.1; docs/linux-sensor-plan.md §11.1): the platform first, then a join
 * token for an existing host, with an optional name and validity. Opened from Admin → Servers (choose the host) or from a
 * host's page (preselected). A re-join token comes from a server's Agent tab, for the server's own platform.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ hostId?: string | null; rejoin?: { sensorId: string; name: string } | null }>()

// Not the Select's `loading`: PrimeVue ignores clicks while it is set, so a click before the hosts arrive did nothing (and
// e2e's Add server failed on a slow runner). The list opens at once and says it is loading.
const hosts = useQuery({
  queryKey: [...keys.hosts, 'all'],
  queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
  enabled: visible,
})
const platform = ref<S['AgentPlatform']>('Windows')
const host = ref<string | null>(null)
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
    () =>
      props.rejoin
        ? api.agents.rejoinToken(props.rejoin.sensorId, hours.value)
        : api.agents.createToken({
            platform: platform.value,
            hostId: host.value!,
            name: name.value.trim() || null,
            validHours: hours.value,
          }),
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
      <div v-if="!rejoin" class="field">
        <label for="server-host">On which host?</label>
        <Select
          v-model="host"
          input-id="server-host"
          :options="hosts.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          filter
          placeholder="Choose a host"
          :empty-message="hosts.isPending.value ? 'Loading hosts…' : 'No host yet: create one first.'"
        />
        <small class="field-hint">A server is a sensor of an existing host. Its details arrive when it joins.</small>
      </div>
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
        <Button label="Create token" icon="pi pi-key" :loading="busy" :disabled="!rejoin && !host" @click="create" />
      </template>
    </template>
  </Dialog>
</template>
