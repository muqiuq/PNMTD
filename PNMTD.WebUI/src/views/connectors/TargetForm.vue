<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import TakeScriptDialog from './TakeScriptDialog.vue'
import { targetSentence, type TakenScript } from './connectorFormat'

/**
 * A script connector's target as text (docs/connectors-plan.md §3.3, §13.3): each field editable, with its selector beside it. The
 * selectors copy what PNMT knows now (a server, a library script, a registered script, an account) and link nothing. The text's parse
 * shows live: bash's header at once, PowerShell's once a connected agent parsed it.
 */
const target = defineModel<S['ConnectorTargetRequest']>({ required: true })
const props = defineProps<{ type: S['ConnectorType'] }>()
const emit = defineEmits<{ parsed: [parsed: S['ConnectorParseResult'] | null] }>()
const linux = computed(() => props.type === 'LinuxScript')

const servers = useQuery({
  queryKey: computed(() => [...keys.connectors, 'lookups', 'servers', props.type]),
  queryFn: () => api.connectors.servers(props.type),
})
const serverChoice = ref<string | null>(null)
watch(serverChoice, (id) => {
  const s = servers.data.value?.find((x) => x.sensorId === id)
  if (s) target.value = { ...target.value, server: s.name }
})
/** The server the name finds now, for the account's and the registered scripts' selectors. */
const serverNow = computed(() => {
  const name = target.value.server.trim().toLowerCase()
  const full = servers.data.value?.filter((s) => s.name.toLowerCase() === name) ?? []
  const found = full.length
    ? full
    : (servers.data.value?.filter((s) => s.name.split(' / ')[0].toLowerCase() === name) ?? [])
  return found.length === 1 ? found[0] : null
})
const accounts = useQuery({
  queryKey: computed(() => [...keys.connectors, 'lookups', 'accounts', props.type, serverNow.value?.sensorId ?? null]),
  queryFn: () => api.connectors.accounts(props.type, serverNow.value?.sensorId),
})
const accountChoice = ref<number | null>(null)
watch(accountChoice, (i) => {
  const a = i === null ? null : accounts.data.value?.[i]
  if (a) target.value = { ...target.value, runAsKind: a.kind, account: a.account }
})
const kinds = computed(() =>
  linux.value
    ? [
        { label: 'root (the service)', value: 'Service' },
        { label: 'An account', value: 'User' },
      ]
    : [
        { label: 'The service', value: 'Service' },
        { label: 'Stored', value: 'Stored' },
        { label: 'gMSA', value: 'Gmsa' },
      ],
)

// The parse, live, debounced; while pending, asked again every 2 s.
const parseText = ref(target.value.scriptText)
let timer: ReturnType<typeof setTimeout> | undefined
watch(
  () => target.value.scriptText,
  (t) => {
    clearTimeout(timer)
    timer = setTimeout(() => (parseText.value = t), 500)
  },
)
const parse = useQuery({
  queryKey: computed(() => [...keys.connectors, 'parse', props.type, parseText.value]),
  queryFn: () => api.connectors.parse({ type: props.type, text: parseText.value, function: null }),
  enabled: computed(() => parseText.value.trim().length > 0),
  refetchInterval: (q) => (q.state.data?.state === 'Pending' ? 2000 : false),
})
watch(
  () => parse.data.value,
  (p) => emit('parsed', p ?? null),
  { immediate: true },
)
// Text like every field (§3.3): typed, or chosen from the parse's functions; empty is the whole script.
const functionText = computed({
  get: () => target.value.function ?? '',
  set: (v: string) => (target.value = { ...target.value, function: v.trim() || null }),
})
const functionChoice = ref<string | null>(null)
watch(functionChoice, (f) => (target.value = { ...target.value, function: f }))
const functions = computed(() => [
  { label: 'The whole script', value: null as string | null },
  ...(parse.data.value?.parsed?.functions ?? []).map((f) => ({
    label: f.help?.synopsis ? `${f.name}: ${f.help.synopsis}` : f.name,
    value: f.name as string | null,
  })),
])
const parameters = computed(() => {
  const parsed = parse.data.value?.parsed
  if (!parsed) return []
  return target.value.function
    ? (parsed.functions?.find((f) => f.name === target.value.function)?.parameters ?? [])
    : parsed.parameters
})

const taking = ref<'library' | 'registered' | null>(null)
const takeVisible = computed({ get: () => taking.value !== null, set: (v) => !v && (taking.value = null) })
function take(s: TakenScript) {
  target.value = {
    ...target.value,
    scriptName: s.name,
    scriptText: s.text,
    takenFrom: s.takenFrom,
    ...(s.runAsKind ? { runAsKind: s.runAsKind, account: s.account ?? null } : {}),
  }
}
const sentence = computed(() =>
  targetSentence({
    server: target.value.server,
    scriptName: target.value.scriptName,
    function: target.value.function,
    runAsKind: target.value.runAsKind,
    account: target.value.account,
    linux: linux.value,
  }),
)
</script>

<template>
  <div class="form">
    <div class="field">
      <label for="target-server">Server</label>
      <div class="with-selector">
        <InputText id="target-server" v-model="target.server" placeholder="WEB01 / Windows, or the host's name" fluid />
        <Select
          v-model="serverChoice"
          :options="servers.data.value ?? []"
          :option-label="(s: S['ConnectorServerOption']) => `${s.name}${s.note ? ` (${s.note})` : ''}`"
          option-value="sensorId"
          placeholder="Choose…"
          filter
          aria-label="Choose a server"
          :empty-message="`No ${linux ? 'Linux' : 'Windows'} server with an agent`"
        />
      </div>
      <small class="field-hint"
        >Text: each call looks the name up again, so joining the agent again or moving it changes nothing.
        <template v-if="target.server.trim()">{{
          serverNow ? `Finds ${serverNow.name} now.` : 'Finds no single server now.'
        }}</template></small
      >
    </div>
    <div class="form-grid">
      <div class="field">
        <label for="target-name">Script's name</label>
        <InputText id="target-name" v-model="target.scriptName" :placeholder="linux ? 'backup.sh' : 'Shop-Tools.ps1'" />
      </div>
      <div class="field buttons">
        <Button label="From the library…" icon="pi pi-book" size="small" text @click="taking = 'library'" />
        <Button
          v-if="!linux"
          label="From the server…"
          icon="pi pi-server"
          size="small"
          text
          :disabled="!serverNow"
          @click="taking = 'registered'"
        />
      </div>
    </div>
    <div class="field">
      <label for="target-text">The connector's own copy of the text</label>
      <Textarea id="target-text" v-model="target.scriptText" class="mono script" rows="14" spellcheck="false" />
      <small v-if="target.takenFrom" class="field-hint"
        >Taken from {{ target.takenFrom }} (information only: nothing is linked).</small
      >
    </div>
    <Message v-if="parse.data.value?.state === 'Pending'" severity="info" :closable="false" size="small">
      {{
        parse.data.value.parserAvailable
          ? 'Parsing on a connected Windows agent…'
          : 'Parameters pending: no Windows agent is connected to parse the text. It can be saved; calls wait for the parse.'
      }}
    </Message>
    <Message
      v-for="e in parse.data.value?.parsed?.errors ?? []"
      :key="`${e.line}-${e.message}`"
      severity="error"
      :closable="false"
      size="small"
      >Line {{ e.line }}: {{ e.message }}</Message
    >
    <div v-if="parse.data.value?.parsed" class="parse small">
      <div>
        <strong>Parameters:</strong>
        {{ parameters.map((p) => `${p.name}${p.mandatory ? '*' : ''}`).join(', ') || 'none' }}
      </div>
      <div v-for="f in parse.data.value.parsed.findings" :key="`${f.rule}-${f.line}`" class="muted">
        ⚠ {{ f.message }}
      </div>
    </div>
    <div class="form-grid">
      <div class="field">
        <label for="target-function">Function</label>
        <div class="with-selector">
          <InputText id="target-function" v-model="functionText" placeholder="The whole script" class="mono" />
          <Select
            v-model="functionChoice"
            :options="functions"
            option-label="label"
            option-value="value"
            placeholder="Choose…"
            aria-label="Choose a function"
          />
        </div>
        <small class="field-hint"
          >A function runs instead of the whole script; the agent must be able to call functions.</small
        >
      </div>
      <div class="field">
        <label id="target-as">As</label>
        <SelectButton
          v-model="target.runAsKind"
          :options="kinds"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-labelledby="target-as"
        />
        <div v-if="target.runAsKind !== 'Service'" class="with-selector">
          <InputText
            v-model="target.account"
            :placeholder="linux ? 'deploy' : 'DOMAIN\\svc-shop'"
            aria-label="Account"
          />
          <Select
            v-model="accountChoice"
            :options="(accounts.data.value ?? []).map((a, i) => ({ ...a, i }))"
            :option-label="
              (a: S['ConnectorAccountOption']) => `${a.label}${a.allowed === false ? ' (refused there)' : ''}`
            "
            option-value="i"
            :option-disabled="(a: S['ConnectorAccountOption']) => a.kind === 'TypedPerRun'"
            placeholder="Choose…"
            aria-label="Choose an account"
          />
        </div>
        <small class="field-hint">Passed to the agent as named; the server's own rights decide.</small>
      </div>
    </div>
    <div class="form-grid">
      <div class="field">
        <label for="target-timeout">Timeout (seconds)</label>
        <InputNumber v-model="target.timeoutSeconds" input-id="target-timeout" :min="10" :max="3600" />
      </div>
      <div v-if="!linux" class="field">
        <label for="target-shell">Shell</label>
        <Select v-model="target.shell" input-id="target-shell" :options="['powershell', 'pwsh']" />
      </div>
    </div>
    <p class="sentence">{{ sentence }}</p>
    <TakeScriptDialog
      v-model:visible="takeVisible"
      :type="type"
      :source="taking ?? 'library'"
      :server-id="serverNow?.sensorId ?? null"
      :current="target.scriptText"
      @take="take"
    />
  </div>
</template>

<style scoped>
.with-selector {
  display: flex;
  gap: var(--pnmt-space-2);
}

.with-selector > :first-child {
  flex: 1;
}

.buttons {
  display: flex;
  flex-direction: row;
  align-items: flex-end;
  gap: var(--pnmt-space-2);
}

.script {
  font-size: 0.8rem;
  width: 100%;
}

.parse {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.sentence {
  font-weight: 600;
}

.small {
  font-size: 0.857rem;
}
</style>
