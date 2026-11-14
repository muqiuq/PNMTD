<script setup lang="ts">
import { useQueries, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Password from 'primevue/password'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { languageLabel, numericParameter, platformOf, runAsLabel } from './scriptFormat'

/**
 * Run… (docs/windows-sensor-plan.md §7.3; docs/linux-sensor-plan.md §8.4): the servers of the script's platform, the account, and
 * a form built from the parameters (a switch or bool is a checkbox, a set a select, a number a number field, a [securestring] or
 * secret a password field). PowerShell runs on Windows servers as a run-as account; bash on Linux servers as root (the agent's
 * service) or a local account, where each server's config.yaml allows it (refused ones are said here, with the line that would
 * allow them). Passwords and secrets are encrypted to each agent and never stored. Admin, audited. Opened for one server, its
 * registered scripts (Windows, §7.2) come first: they run there only, as the account they are registered with.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  scriptId: string | null
  sensorId?: string | null
  localName?: string | null
  platform?: S['AgentPlatform'] | null
}>()
const emit = defineEmits<{ started: [runs: S['AgentScriptRunItem'][]] }>()

const scripts = useQuery({
  queryKey: [...keys.agents, 'scripts', 'all'],
  queryFn: () => api.agents.scripts({ pageSize: 500 }),
  enabled: visible,
})
const local = useQuery({
  queryKey: computed(() => [...keys.agents, 'local-scripts', props.sensorId]),
  queryFn: () => api.windows.localScripts(props.sensorId!),
  enabled: computed(() => visible.value && !!props.sensorId && props.platform !== 'Linux'),
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
  const library = (scripts.data.value?.items ?? [])
    .filter((s) => !props.platform || platformOf(s.language) === props.platform)
    .map((s) => ({ label: props.platform ? s.name : `${s.name} (${languageLabel(s.language)})`, value: s.id }))
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
const language = computed<S['ScriptLanguage']>(() => {
  if (isLocal.value) return 'PowerShell'
  const item = scripts.data.value?.items.find((s) => s.id === chosen.value)
  return detail.data.value?.language ?? item?.language ?? (props.platform === 'Linux' ? 'Bash' : 'PowerShell')
})
const bash = computed(() => language.value === 'Bash')
const scriptPlatform = computed(() => platformOf(language.value))
// The servers of the script's platform: PowerShell on Windows, bash on Linux.
const servers = useQuery({
  queryKey: computed(() => [...keys.agents, 'servers', scriptPlatform.value]),
  queryFn: () => api.agents.servers({ pageSize: 500, platform: scriptPlatform.value }),
  enabled: visible,
})
const serverName = (id: string) => {
  const s = servers.data.value?.items.find((i) => i.sensorId === id)
  return s ? `${s.host.name} / ${s.name}` : 'a server'
}
const runAs = useQuery({
  queryKey: [...keys.agents, 'run-as'],
  queryFn: api.windows.runAs,
  enabled: computed(() => visible.value && !bash.value),
})
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
  runAsMode.value = 'Service'
  linuxAccount.value = null
})
// Another language means other servers: the choice starts again (a server's own page keeps its server).
watch(language, (l, before) => {
  if (before && l !== before) targets.value = props.sensorId && props.platform === platformOf(l) ? [props.sensorId] : []
})
watch(chosen, () => (values.value = {}))

// --- Linux: root (the service) or a local account, as each server's config.yaml allows (§8.4, §8.5) -----------------------
const runAsMode = ref<'Service' | 'User'>('Service')
const linuxAccount = ref<string | null>(null)
const runAsModes = [
  { label: 'root (the service)', value: 'Service' },
  { label: 'An account', value: 'User' },
]
const accountQueries = useQueries({
  queries: computed(() =>
    (visible.value && bash.value ? targets.value : []).map((id) => ({
      queryKey: [...keys.agents, 'run-as-accounts', id],
      queryFn: () => api.agents.runAsAccounts(id),
    })),
  ),
})
const linuxServers = computed(() =>
  accountQueries.value.map((q) => q.data).filter((d): d is S['LinuxRunAsAccounts'] => !!d),
)
const accountOptions = computed(() =>
  [
    ...new Set(linuxServers.value.flatMap((s) => s.accounts.filter((a) => a.kind === 'User').map((a) => a.name))),
  ].sort(),
)
/** Where the chosen account is refused, server by server, with the line that would allow it. */
const refusals = computed(() =>
  linuxServers.value.flatMap((s) => {
    const where = serverName(s.sensorId)
    if (!s.scriptsAllowed) return [`${where}: ${s.scriptsRefused ?? 'scripts are off'}`]
    if (runAsMode.value === 'User' && !linuxAccount.value) return []
    const a =
      runAsMode.value === 'Service'
        ? s.accounts.find((x) => x.kind === 'Service')
        : s.accounts.find((x) => x.kind === 'User' && x.name === linuxAccount.value)
    if (!a) return [`${where}: no account ${linuxAccount.value}`]
    return a.allowed ? [] : [`${where}: ${a.refused}`]
  }),
)
const maxMinutes = computed(() =>
  linuxServers.value.length ? Math.min(...linuxServers.value.map((s) => s.maxTimeoutMinutes)) : null,
)

const parameters = computed(() =>
  isLocal.value ? (localScript.value?.script.parsed?.parameters ?? []) : (detail.data.value?.parsed?.parameters ?? []),
)
const headerErrors = computed(() => (bash.value ? (detail.data.value?.parsed?.errors.length ?? 0) : 0))
const canRun = computed(() => {
  if (isLocal.value) return !!localScript.value?.script.matches && !!localScript.value.script.parsed && !!props.sensorId
  if (!chosen.value || !targets.value.length || !detail.data.value) return false
  if (bash.value)
    return !!detail.data.value.parsed && !headerErrors.value && (runAsMode.value === 'Service' || !!linuxAccount.value)
  return !!account.value && detail.data.value.parseState === 'Parsed'
})
const selectedRunAs = computed(() => runAs.data.value?.find((r) => r.id === account.value))

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
  const args = argumentsBody as S['AgentScriptRunRequest']['arguments']
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
        : bash.value
          ? await api.agents.runScript(chosen.value!, {
              sensorIds: targets.value,
              runAsId: null,
              arguments: args,
              secureArguments: secure,
              password: null,
              timeoutSeconds: timeout.value,
              shell: 'bash',
              runAs: runAsMode.value,
              account: runAsMode.value === 'User' ? linuxAccount.value : null,
            })
          : await api.agents.runScript(chosen.value!, {
              sensorIds: targets.value,
              runAsId: account.value!,
              arguments: args,
              secureArguments: secure,
              password: selectedRunAs.value?.kind === 'TypedPerRun' ? password.value : null,
              timeoutSeconds: timeout.value,
              shell: shell.value,
            }),
    { success: 'Sent', invalidate: [keys.agents] },
  )
  password.value = ''
  values.value = {}
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
          :empty-message="
            scriptPlatform === 'Linux' || props.platform === 'Linux'
              ? 'No bash scripts: add one to the library'
              : 'No scripts: add one to the library, or register one in the agent\'s window on the server'
          "
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
        v-if="!isLocal && !bash && detail.data.value && detail.data.value.parseState !== 'Parsed'"
        severity="warn"
        :closable="false"
        size="small"
      >
        Its parameters are not known yet: an agent parses it when one is connected.
      </Message>
      <Message v-if="headerErrors" severity="error" :closable="false" size="small">
        Its header has errors, which block runs: fix them in the editor.
      </Message>
      <p v-if="localScript" class="muted local-note">
        Runs on this server only, as <strong>{{ localScript.runAsName }}</strong>, the account it is registered with.
      </p>
      <div v-if="!isLocal" class="field">
        <label for="run-servers">{{ bash ? 'Linux servers' : 'Windows servers' }}</label>
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
        <small class="field-hint">{{
          bash
            ? 'Only servers whose config.yaml allows scripts, as the chosen account, run it.'
            : "Only servers where an administrator allowed scripts in the agent's window run it."
        }}</small>
      </div>
      <template v-if="!isLocal && bash">
        <div class="field">
          <label id="run-as-linux">As</label>
          <SelectButton
            v-model="runAsMode"
            :options="runAsModes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-labelledby="run-as-linux"
          />
          <Select
            v-if="runAsMode === 'User'"
            v-model="linuxAccount"
            :options="accountOptions"
            filter
            editable
            placeholder="A local account"
            aria-label="Account"
          />
          <small class="field-hint">Root starts the script as the account: no passwords on Linux.</small>
        </div>
        <Message v-if="refusals.length" severity="warn" :closable="false" size="small">
          Refused here, and nothing is sent there:
          <div v-for="r in refusals" :key="r">{{ r }}</div>
        </Message>
      </template>
      <template v-if="!isLocal && !bash">
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
      </template>
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
            v-else-if="numericParameter(p)"
            v-model="values[p.name] as number"
            :input-id="`arg-${p.name}`"
            :min="p.rangeMin ?? undefined"
            :max="p.rangeMax ?? undefined"
            :max-fraction-digits="p.type === 'number' ? 6 : 0"
            :placeholder="p.default ?? ''"
          />
          <InputText v-else :id="`arg-${p.name}`" v-model="values[p.name] as string" :placeholder="p.default ?? ''" />
          <small v-if="p.helpMessage" class="field-hint">{{ p.helpMessage }}</small>
          <small v-if="p.isSecure" class="field-hint">Encrypted to each server's agent, never stored or logged.</small>
        </div>
      </template>
      <div class="form-grid">
        <div class="field">
          <label for="run-timeout">Timeout (seconds)</label>
          <InputNumber
            v-model="timeout"
            input-id="run-timeout"
            :min="10"
            :max="bash && maxMinutes ? maxMinutes * 60 : 3600"
          />
          <small v-if="bash && maxMinutes" class="field-hint">At most {{ maxMinutes }} minutes on these servers.</small>
        </div>
        <div v-if="!bash" class="field">
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
