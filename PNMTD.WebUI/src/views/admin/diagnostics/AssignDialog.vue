<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'

/** Assign… (docs/windows-sensor-plan.md §5.2, §10.3): a set to one or more servers, scheduled with an interval or by hand. */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ setId?: string | null; targetSensorId?: string | null }>()

const sets = useQuery({
  queryKey: [...keys.diagnostics, 'sets', 'all'],
  queryFn: () => api.diagnostics.sets({ pageSize: 500 }),
  enabled: visible,
})
const servers = useQuery({
  queryKey: [...keys.agents, 'servers', 'all'],
  queryFn: () => api.agents.servers({ pageSize: 500 }),
  enabled: visible,
})
const chosenSet = ref<string | null>(null)
const targets = ref<string[]>([])
const mode = ref<S['DiagnosticMode']>('Scheduled')
const interval = ref(15)
const enabled = ref(true)
watch(visible, (open) => {
  if (!open) return
  chosenSet.value = props.setId ?? null
  targets.value = props.targetSensorId ? [props.targetSensorId] : []
  mode.value = 'Scheduled'
  interval.value = 15
  enabled.value = true
})
const { run, busy } = useActions()
async function assign() {
  const done = await run(
    () =>
      api.diagnostics.assign({
        setId: chosenSet.value!,
        targetSensorIds: targets.value,
        mode: mode.value,
        intervalMinutes: interval.value,
        enabled: enabled.value,
      }),
    { success: 'Assigned', invalidate: [keys.diagnostics, keys.agents, keys.sensors] },
  )
  if (done) visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Assign a diagnostics set"
    modal
    :style="{ width: '34rem' }"
    :breakpoints="{ '640px': '95vw' }"
  >
    <form class="form" @submit.prevent="assign">
      <div class="field">
        <label for="assign-set">Set</label>
        <Select
          v-model="chosenSet"
          input-id="assign-set"
          :options="sets.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          filter
          placeholder="Choose a set"
        />
      </div>
      <div class="field">
        <label for="assign-servers">Servers</label>
        <MultiSelect
          v-model="targets"
          input-id="assign-servers"
          :options="servers.data.value?.items ?? []"
          :option-label="(s: S['AgentServerItem']) => `${s.host.name} / ${s.name}`"
          option-value="sensorId"
          filter
          display="chip"
          placeholder="Choose servers"
        />
        <small class="field-hint"
          >Each server gets a sensor "Diagnostics: set" of its own, with its state and notifications.</small
        >
      </div>
      <div class="field">
        <label for="assign-mode">Schedule</label>
        <div class="line">
          <Select v-model="mode" input-id="assign-mode" :options="['Scheduled', 'Manual']" />
          <template v-if="mode === 'Scheduled'">
            every <InputNumber v-model="interval" :min="1" :max="1440" show-buttons input-class="num" /> minutes
          </template>
        </div>
      </div>
      <label class="line"><Checkbox v-model="enabled" binary /> enabled</label>
    </form>
    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="visible = false" />
      <Button label="Assign" :loading="busy" :disabled="!chosenSet || !targets.length" @click="assign" />
    </template>
  </Dialog>
</template>

<style scoped>
.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.line :deep(.num) {
  width: 5rem;
}
</style>
