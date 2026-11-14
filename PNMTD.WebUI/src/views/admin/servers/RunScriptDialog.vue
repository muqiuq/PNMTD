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
 * encrypted to each agent and never stored. Admin, audited. Opened for one server, its registered scripts (§7.2) come first:
 * they run there only, as the account they are registered with.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ scriptId: string | null; sensorId?: string | null; localName?: string | null }>()
const emit = defineEmits<{ started: [runs: S['AgentScriptRunItem'][]] }>()

const scripts = useQuery({
  queryKey: [...keys.agents, 'scripts', 'all'],
  queryFn: () => api.agents.scripts({ pageSize: 500 }),
  enabled: visible,
})
const local = useQuery({
  queryKey: computed(() => [...keys.agents, 'local-scripts', props.sensorId]),
  queryFn: () => api.windows.localScripts(props.sensorId!),
  enabled: computed(() => visible.value && !!props.sensorId),
})
// A library script by its id, a registered one as "local:<name>".
const chosen = ref<string | null>(null)
const localScript = computed(() =>
  chosen.value?.startsWith('local:')
    ? (local.data.value?.scripts.find((l) => `local:${l.script.name}` === chosen.value) ?? null)
    : null,
)
const isLocal = computed(() => chosen.value?.startsWith('local:') ?? false)
const options = computed(() => {
  const library = (scripts.data.value?.items ?? []).map((s) => ({ label: s.name, value: s.id }))
  const registered = (local.data.value?.scripts ?? []).map((l) => ({
    label: `${l.script.name} (as ${l.runAsName})`,
    value: `local:${l.script.name}`,
  }))
  return [
    ...(registered.length ? [{ label: 'Registered on this server', items: registered }] : []),
    { label: 'Library', items: library },
  ]
})
const detail = useQuery({
  queryKey: computed(() => [...keys.agents, 'script', chosen.value]),
  queryFn: () => api.agents.script(chosen.value!),
  enabled: computed(() => visible.value && chosen.value !== null && !isLocal.value),
})
// PowerShell runs on Windows servers; bash on Linux comes with docs/linux-sensor-plan.md §8 (L7).
const servers = useQuery({
  queryKey: [...keys.agents, 'servers', 'Windows'],
  queryFn: () => api.agents.servers({ pageSize: 500, platform: 'Windows' }),
  enabled: visible,
})
const runAs = useQuery({ queryKey: [...keys.agents, 'run-as'], queryFn: api.windows.runAs, enabled: visible })
const targets = ref<string[]>([])
const account = ref<string | null>(null)
const password = ref('')
const timeout = ref(600)
const shell = ref<'powershell' | 'pwsh'>('powershell')
const values = ref<Record<string, unknown>>({})
watch(visible, (open) => {
  if (!open) return
  chosen.value = props.localName ? `local:${props.localName}` : props.scriptId
  targets.value = props.sensorId ? [props.sensorId] : []
  account.value = null
  password.value = ''
  values.value = {}
})
const parameters = computed(() =>
  isLocal.value ? (localScript.value?.script.parsed?.parameters ?? []) : (detail.data.value?.parsed?.parameters ?? []),
)
const canRun = computed(() =>
  isLocal.value
    ? !!localScript.value?.script.matches && !!localScript.value.script.parsed && !!props.sensorId
    : !!chosen.value && targets.value.length > 0 && !!account.value && detail.data.value?.parseState === 'Parsed',
)
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
    async () =>
      isLocal.value
        ? [
            await api.windows.runLocalScript(props.sensorId!, {
              name: localScript.value!.script.name,
              arguments: argumentsBody as S['WindowsLocalScriptRunRequest']['arguments'],
              secureArguments: secure,
              timeoutSeconds: timeout.value,
              shell: shell.value,
            }),
          ]
        : await api.agents.runScript(chosen.value!, {
            sensorIds: targets.value,
            runAsId: account.value!,
            arguments: argumentsBody as S['AgentScriptRunRequest']['arguments'],
            secureArguments: secure,
            password: selectedRunAs.value?.kind === 'TypedPerRun' ? password.value : null,
            timeoutSeconds: timeout.value,
            shell: shell.value,
          }),
    { success: 'Sent', invalidate: [keys.agents] },
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
          :options="options"
          option-label="label"
          option-value="value"
          option-group-label="label"
          option-group-children="items"
          filter
          placeholder="Choose a script"
          empty-message="No scripts: add one to the library, or register one in the agent's window on the server"
        />
      </div>
      <Message v-if="localScript && !localScript.script.matches" severity="warn" :closable="false" size="small">
        The copy on the server changed after it was registered: replace it in the agent's window.
      </Message>
      <Message v-else-if="localScript && !localScript.script.parsed" severity="warn" :closable="false" size="small">
        The agent could not read its parameters{{
          localScript.script.parseError ? `: ${localScript.script.parseError}` : ''
        }}.
      </Message>
      <Message
        v-if="!isLocal && detail.data.value && detail.data.value.parseState !== 'Parsed'"
        severity="warn"
        :closable="false"
        size="small"
      >
        Its parameters are not known yet: an agent parses it when one is connected.
      </Message>
      <p v-if="localScript" class="muted local-note">
        Runs on this server only, as <strong>{{ localScript.runAsName }}</strong>, the account it is registered with.
      </p>
      <div v-if="!isLocal" class="field">
        <label for="run-servers">Servers</label>
        <MultiSelect
          v-model="targets"
          input-id="run-servers"
          :options="servers.data.value?.items ?? []"
          :option-label="(s: S['AgentServerItem']) => `${s.host.name} / ${s.name}`"
          option-value="sensorId"
          filter
          display="chip"
          placeholder="Choose servers"
        />
        <small class="field-hint"
          >Only servers where an administrator allowed scripts in the agent's window run it.</small
        >
      </div>
      <div v-if="!isLocal" class="field">
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
      <div v-if="!isLocal && selectedRunAs?.kind === 'TypedPerRun'" class="field">
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
      <Button label="Run" icon="pi pi-play" :loading="busy" :disabled="!canRun" @click="start" />
    </template>
  </Dialog>
</template>

<style scoped>
.small {
  font-size: 0.857rem;
}

.local-note {
  margin: 0;
}
</style>
