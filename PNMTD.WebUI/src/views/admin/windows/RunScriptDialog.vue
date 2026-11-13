<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Password from 'primevue/password'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { runAsLabel } from './scriptFormat'

/**
 * Run… (docs/windows-sensor-plan.md §7.3): the servers, the account, and a form built from the parsed parameters (a switch is a
 * checkbox, a set a select, a number a number field, a [securestring] a password field). Passwords and secure arguments are
 * encrypted to each agent and never stored. Admin, audited.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ scriptId: string | null; sensorId?: string | null }>()
const emit = defineEmits<{ started: [runs: S['WindowsScriptRunItem'][]] }>()

const scripts = useQuery({
  queryKey: [...keys.windows, 'scripts', 'all'],
  queryFn: () => api.windows.scripts({ pageSize: 500 }),
  enabled: visible,
})
const chosen = ref<string | null>(null)
const detail = useQuery({
  queryKey: computed(() => [...keys.windows, 'script', chosen.value]),
  queryFn: () => api.windows.script(chosen.value!),
  enabled: computed(() => visible.value && chosen.value !== null),
})
const servers = useQuery({
  queryKey: [...keys.windows, 'servers', 'all'],
  queryFn: () => api.windows.servers({ pageSize: 500 }),
  enabled: visible,
})
const runAs = useQuery({ queryKey: [...keys.windows, 'run-as'], queryFn: api.windows.runAs, enabled: visible })
const targets = ref<string[]>([])
const account = ref<string | null>(null)
const password = ref('')
const timeout = ref(600)
const shell = ref<'powershell' | 'pwsh'>('powershell')
const values = ref<Record<string, unknown>>({})
watch(visible, (open) => {
  if (!open) return
  chosen.value = props.scriptId
  targets.value = props.sensorId ? [props.sensorId] : []
  account.value = null
  password.value = ''
  values.value = {}
})
const parameters = computed(() => detail.data.value?.parsed?.parameters ?? [])
const selectedRunAs = computed(() => runAs.data.value?.find((r) => r.id === account.value))
const numeric = (p: S['ScriptParameter']) => ['Int32', 'Int64', 'Double', 'Decimal', 'Byte', 'UInt32'].includes(p.type)

const { run, busy } = useActions()
async function start() {
  const argumentsBody: Record<string, unknown> = {}
  const secure: Record<string, string> = {}
  for (const p of parameters.value) {
    const v = values.value[p.name]
    if (v === undefined || v === null || v === '') continue
    if (p.isSecure) secure[p.name] = String(v)
    else argumentsBody[p.name] = v
  }
  const runs = await run(
    () =>
      api.windows.runScript(chosen.value!, {
        sensorIds: targets.value,
        runAsId: account.value!,
        arguments: argumentsBody as S['WindowsScriptRunRequest']['arguments'],
        secureArguments: secure,
        password: selectedRunAs.value?.kind === 'TypedPerRun' ? password.value : null,
        timeoutSeconds: timeout.value,
        shell: shell.value,
      }),
    { success: 'Sent', invalidate: [keys.windows] },
  )
  password.value = ''
  if (runs) {
    emit('started', runs)
    visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Run a script"
    modal
    :style="{ width: '40rem' }"
    :breakpoints="{ '700px': '96vw' }"
  >
    <form class="form" @submit.prevent="start">
      <div class="field">
        <label for="run-script">Script</label>
        <Select
          v-model="chosen"
          input-id="run-script"
          :options="scripts.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          filter
          placeholder="Choose a script"
        />
      </div>
      <Message
        v-if="detail.data.value && detail.data.value.parseState !== 'Parsed'"
        severity="warn"
        :closable="false"
        size="small"
      >
        Its parameters are not known yet: an agent parses it when one is connected.
      </Message>
      <div class="field">
        <label for="run-servers">Servers</label>
        <MultiSelect
          v-model="targets"
          input-id="run-servers"
          :options="servers.data.value?.items ?? []"
          :option-label="(s: S['WindowsServerItem']) => `${s.host.name} / ${s.name}`"
          option-value="sensorId"
          filter
          display="chip"
          placeholder="Choose servers"
        />
        <small class="field-hint"
          >Only servers where an administrator allowed scripts in the agent's window run it.</small
        >
      </div>
      <div class="field">
        <label for="run-as">As</label>
        <Select
          v-model="account"
          input-id="run-as"
          :options="runAs.data.value ?? []"
          :option-label="
            (r: S['WindowsRunAsItem']) => `${r.name} (${r.account ?? 'LocalSystem'}, ${runAsLabel(r.kind)})`
          "
          option-value="id"
          placeholder="Choose a run-as account"
        />
      </div>
      <div v-if="selectedRunAs?.kind === 'TypedPerRun'" class="field">
        <label for="run-password">Password of {{ selectedRunAs.account }}</label>
        <Password v-model="password" input-id="run-password" :feedback="false" toggle-mask />
        <small class="field-hint">Encrypted to each server's agent, used once, never stored or logged.</small>
      </div>
      <template v-for="p in parameters" :key="p.name">
        <div class="field">
          <label :for="`arg-${p.name}`"
            >{{ p.name }} <span class="muted small">[{{ p.type }}]{{ p.mandatory ? ' · mandatory' : '' }}</span></label
          >
          <Checkbox v-if="p.isSwitch" v-model="values[p.name]" binary :input-id="`arg-${p.name}`" />
          <Select
            v-else-if="p.validateSet?.length"
            v-model="values[p.name]"
            :input-id="`arg-${p.name}`"
            :options="p.validateSet"
            :placeholder="p.default ?? ''"
            show-clear
          />
          <Password
            v-else-if="p.isSecure"
            v-model="values[p.name] as string"
            :input-id="`arg-${p.name}`"
            :feedback="false"
            toggle-mask
          />
          <InputNumber
            v-else-if="numeric(p)"
            v-model="values[p.name] as number"
            :input-id="`arg-${p.name}`"
            :min="p.rangeMin ?? undefined"
            :max="p.rangeMax ?? undefined"
            :placeholder="p.default ?? ''"
          />
          <InputText v-else :id="`arg-${p.name}`" v-model="values[p.name] as string" :placeholder="p.default ?? ''" />
          <small v-if="p.helpMessage" class="field-hint">{{ p.helpMessage }}</small>
        </div>
      </template>
      <div class="form-grid">
        <div class="field">
          <label for="run-timeout">Timeout (seconds)</label>
          <InputNumber v-model="timeout" input-id="run-timeout" :min="10" :max="3600" />
        </div>
        <div class="field">
          <label for="run-shell">Shell</label>
          <Select v-model="shell" input-id="run-shell" :options="['powershell', 'pwsh']" />
        </div>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="visible = false" />
      <Button
        label="Run"
        icon="pi pi-play"
        :loading="busy"
        :disabled="!chosen || !targets.length || !account || detail.data.value?.parseState !== 'Parsed'"
        @click="start"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.small {
  font-size: 0.857rem;
}
</style>
