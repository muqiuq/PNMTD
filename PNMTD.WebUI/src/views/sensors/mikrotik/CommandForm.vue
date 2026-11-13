<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Password from 'primevue/password'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, useId, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { commandHints } from './commandHints'

/**
 * A remote command (docs/mikrotik-plan.md §8.2): the text, who it runs as, the timeout and the options. As
 * pnmt the router allows reading only; an Admin may run it as the router's admin with the password typed for
 * this run, which stays in this field and the server's memory.
 */
const props = defineProps<{ routers: { id: string; name: string }[]; isAdmin: boolean }>()
const text = defineModel<string>('text', { default: '' })
const emit = defineEmits<{ started: [created: S['MikrotikCommandsCreated']] }>()
const queryClient = useQueryClient()
const { run, busy } = useActions()
const id = useId()

const runAs = ref<S['MikrotikRunAs']>('Pnmt')
const username = ref('admin')
const password = ref('')
const timeout = ref(60)
const backup = ref(false)
const oneAtATime = ref(false)
const refused = ref<S['MikrotikJobRefusal'][]>([])
const runAsChoices = [
  { label: 'pnmt (read-only)', value: 'Pnmt' },
  { label: 'Router admin', value: 'Admin' },
]
// The defaults of §8.2: exports and one at a time for admin runs, neither for pnmt.
watch(
  runAs,
  (r) => {
    backup.value = r === 'Admin'
    oneAtATime.value = r === 'Admin'
    if (r === 'Pnmt') password.value = ''
  },
  { immediate: true },
)
const hints = computed(() => commandHints(text.value))
const bytes = computed(() => new TextEncoder().encode(text.value).length)
const ready = computed(
  () =>
    !!text.value.trim() &&
    bytes.value <= 16384 &&
    (runAs.value === 'Pnmt' || (!!password.value && !!username.value.trim())),
)

async function start() {
  const admin = runAs.value === 'Admin'
  const created = await run(
    () =>
      api.mikrotik.runCommand({
        sensorIds: props.routers.map((r) => r.id),
        command: text.value,
        runAs: runAs.value,
        username: admin ? username.value.trim() : null,
        password: admin ? password.value : null,
        timeoutSeconds: timeout.value,
        backupBeforeAndAfter: backup.value,
        oneAtATime: oneAtATime.value,
      }),
    { failure: 'Not started' },
  )
  password.value = ''
  if (!created) return
  refused.value = created.refused
  await queryClient.invalidateQueries({ queryKey: [...keys.mikrotik, 'commands'] })
  if (created.jobs.length) emit('started', created)
}
</script>

<template>
  <form class="form" autocomplete="off" novalidate @submit.prevent="start">
    <div class="field">
      <label :for="`${id}-text`">Command or script</label>
      <Textarea
        :id="`${id}-text`"
        v-model="text"
        rows="5"
        auto-resize
        class="mono"
        spellcheck="false"
        placeholder="/ip address print"
      />
      <small class="muted" :class="{ bad: bytes > 16384 }">
        {{ bytes }} of 16384 bytes. Sent as typed and run as one script: a syntax error runs nothing, and the first
        error stops it. The text and the output are kept with the job; an Admin can redact them.
      </small>
    </div>
    <Message v-for="h in hints" :key="h.text" :severity="h.severity" :closable="false" size="small">{{
      h.text
    }}</Message>
    <div class="pair">
      <div v-if="isAdmin" class="field">
        <span :id="`${id}-as`" class="label">Run as</span>
        <SelectButton
          v-model="runAs"
          :options="runAsChoices"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-labelledby="`${id}-as`"
        />
      </div>
      <div class="field">
        <label :for="`${id}-timeout`">Timeout in seconds</label>
        <InputNumber v-model="timeout" :input-id="`${id}-timeout`" :min="5" :max="600" show-buttons fluid />
      </div>
    </div>
    <template v-if="runAs === 'Admin'">
      <div class="pair">
        <div class="field">
          <label :for="`${id}-user`">Router admin</label>
          <InputText :id="`${id}-user`" v-model="username" autocomplete="off" fluid />
        </div>
        <div class="field">
          <label :for="`${id}-password`">Password</label>
          <Password
            v-model="password"
            :input-id="`${id}-password`"
            :feedback="false"
            toggle-mask
            fluid
            autocomplete="new-password"
          />
        </div>
      </div>
      <small class="muted">
        For this run only: checked on every router first, held in PNMTD's memory while the run lasts, never stored or
        logged, and sent only to routers whose host key PNMTD knows.
      </small>
    </template>
    <small v-else class="muted"
      >As pnmt the router allows reading only: it refuses a change by itself. Ping and traceroute need
      <em>Diagnostics</em> on the router.</small
    >
    <label class="check"
      ><Checkbox v-model="backup" binary /> Back up before and after, to show what the command changed</label
    >
    <label v-if="routers.length > 1" class="check"
      ><Checkbox v-model="oneAtATime" binary /> One router at a time; stop at the first failure</label
    >
    <Message v-for="r in refused" :key="r.sensorId" severity="error" :closable="false" size="small"
      >{{ r.name }}: {{ r.reason }}</Message
    >
    <div class="actions">
      <Button
        type="submit"
        :label="routers.length > 1 ? `Run on ${routers.length} routers` : 'Run'"
        icon="pi pi-play"
        :loading="busy"
        :disabled="!ready"
      />
    </div>
  </form>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.field {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.label {
  font-weight: 600;
}

.pair {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-3);
}

@media (max-width: 599px) {
  .pair {
    grid-template-columns: 1fr;
  }
}

.mono {
  font-family: var(--pnmt-mono);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.bad {
  color: var(--pnmt-fail);
}

.actions {
  display: flex;
  justify-content: flex-end;
}
</style>
