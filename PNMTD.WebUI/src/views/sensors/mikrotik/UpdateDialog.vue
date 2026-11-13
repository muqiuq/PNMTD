<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Password from 'primevue/password'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'

/**
 * Starting an update (docs/mikrotik-plan.md §7.1, §7.2): the channel, the router admin's username and password
 * for this update only, and the options. The password stays in this field and the server's memory; it is
 * checked on every router before anything runs. The method and the storage check (docs/ai-operations-plan.md
 * §12.2) start as the routers were last updated, when they all agree.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  routers: {
    id: string
    name: string
    identity?: string | null
    serial?: string | null
    from?: string | null
    to?: string | null
    method?: S['MikrotikUpdateMethod']
    checkStorage?: boolean
    lowStorage?: boolean
    freeStorage?: number | null
  }[]
  channel?: string | null
}>()
const queryClient = useQueryClient()
const { run, busy, notify } = useActions()

const chosenChannel = ref<string | null>(null)
const username = ref('admin')
const password = ref('')
const binaryBackup = ref(true)
const firmware = ref(true)
const stop = ref(true)
const method = ref<S['MikrotikUpdateMethod']>('Download')
const checkStorage = ref(true)
const methods: { value: S['MikrotikUpdateMethod']; label: string; hint: string }[] = [
  {
    value: 'Download',
    label: 'Download, then reboot',
    hint: 'The router downloads the release; PNMT sees it finish, then reboots it. It can be cancelled until the reboot.',
  },
  {
    value: 'Install',
    label: 'Download and install',
    hint: "RouterOS's own install, as WinBox's Download & Install: it downloads and reboots by itself. No cancel once it starts.",
  },
]
const low = computed(() => props.routers.filter((r) => r.lowStorage))
const mib = (bytes?: number | null) => (bytes == null ? '?' : `${Math.floor(bytes / 1024 / 1024)} MiB`)
/** Every router's remembered choice, when they agree; otherwise the defaults. */
function same<T>(values: (T | undefined)[], fallback: T): T {
  const first = values[0]
  return first !== undefined && values.every((v) => v === first) ? first : fallback
}
const refused = ref<S['MikrotikJobRefusal'][]>([])
const channels = [
  { label: "Each router's own", value: null },
  { label: 'stable', value: 'stable' },
  { label: 'long-term', value: 'long-term' },
  { label: 'testing', value: 'testing' },
  { label: 'development', value: 'development' },
]

watch(visible, (open) => {
  if (open) {
    chosenChannel.value = props.channel ?? null
    password.value = ''
    refused.value = []
    method.value = same(
      props.routers.map((r) => r.method),
      'Download',
    )
    checkStorage.value = same(
      props.routers.map((r) => r.checkStorage),
      true,
    )
  } else {
    password.value = ''
  }
})

async function start() {
  const created = await run(
    () =>
      api.mikrotik.startJobs({
        sensorIds: props.routers.map((r) => r.id),
        kind: 'Update',
        channel: chosenChannel.value,
        username: username.value.trim(),
        password: password.value,
        binaryBackup: binaryBackup.value,
        firmware: firmware.value,
        stopAtFirstFailure: stop.value,
        method: method.value,
        checkStorage: checkStorage.value,
      }),
    { failure: 'Not started' },
  )
  password.value = ''
  if (!created) return
  refused.value = created.refused
  await queryClient.invalidateQueries({ queryKey: [...keys.mikrotik] })
  for (const r of props.routers) await queryClient.invalidateQueries({ queryKey: [...keys.sensor(r.id)] })
  if (created.jobs.length) {
    notify.success(`Update started for ${created.jobs.length} router(s)`, 'Each step appears in the job log.')
    if (!created.refused.length) visible.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Update RouterOS"
    modal
    :style="{ width: '40rem' }"
    :breakpoints="{ '680px': '95vw' }"
  >
    <form id="update-form" class="form" autocomplete="off" novalidate @submit.prevent="start">
      <ul class="routers">
        <li v-for="r in routers" :key="r.id">
          <strong>{{ r.name }}</strong>
          <span class="muted">{{ r.identity ?? '' }}{{ r.serial ? ` · ${r.serial}` : '' }}</span>
          <span v-if="r.from" class="mono">{{ r.from }} → {{ r.to ?? 'the newest' }}</span>
        </li>
      </ul>
      <div class="field">
        <label for="update-channel">Channel</label>
        <Select
          v-model="chosenChannel"
          input-id="update-channel"
          :options="channels"
          option-label="label"
          option-value="value"
        />
      </div>
      <div class="pair">
        <div class="field">
          <label for="update-user">Router admin</label>
          <InputText id="update-user" v-model="username" autocomplete="off" />
        </div>
        <div class="field">
          <label for="update-password">Password</label>
          <Password
            v-model="password"
            input-id="update-password"
            :feedback="false"
            toggle-mask
            autocomplete="new-password"
          />
        </div>
      </div>
      <small class="muted">
        Used for this update only: held in PNMTD's memory while it runs, never stored or logged, and sent only to
        routers whose host key PNMTD knows. It needs the policies ssh, read, write, policy and reboot, and for the
        binary backup test, sensitive and ftp too.
      </small>
      <fieldset class="methods">
        <legend>Method</legend>
        <div v-for="m in methods" :key="m.value" class="method">
          <RadioButton v-model="method" :input-id="`update-method-${m.value}`" :value="m.value" />
          <label :for="`update-method-${m.value}`"
            ><strong>{{ m.label }}</strong> <span class="muted small">{{ m.hint }}</span></label
          >
        </div>
      </fieldset>
      <label class="check"><Checkbox v-model="checkStorage" binary /> Check free storage first</label>
      <Message v-if="low.length && checkStorage" severity="warn" :closable="false" size="small">
        {{ low.map((r) => `${r.name} has ${mib(r.freeStorage)} free`).join(', ') }}. On 16 MB-flash routers RouterOS
        keeps the package in RAM: turn the check off, and choose Download and install.
      </Message>
      <label class="check"
        ><Checkbox v-model="binaryBackup" binary /> A binary backup first, encrypted with this password</label
      >
      <small v-if="low.length && binaryBackup" class="muted"
        >The binary backup is written to flash too, and can fail on a nearly full router.</small
      >
      <label class="check"
        ><Checkbox v-model="firmware" binary /> The RouterBOARD firmware afterwards (a second reboot)</label
      >
      <label v-if="routers.length > 1" class="check"
        ><Checkbox v-model="stop" binary /> One router at a time; stop at the first failure</label
      >
      <Message severity="info" :closable="false" size="small">
        Each router exports its configuration, downloads the release from MikroTik itself, reboots (usually 1–5 minutes,
        twice with the firmware), and is checked afterwards. Custom tunnels to it pause meanwhile.
      </Message>
      <Message v-for="r in refused" :key="r.sensorId" severity="error" :closable="false" size="small"
        >{{ r.name }}: {{ r.reason }}</Message
      >
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="update-form"
        label="Update"
        icon="pi pi-upload"
        :loading="busy"
        :disabled="!password || !username.trim()"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.routers {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.routers li {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.pair {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-3);
}

.methods {
  border: 0;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.methods legend {
  padding: 0;
  margin-bottom: var(--pnmt-space-1);
}

.method {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.mono {
  font-family: var(--pnmt-mono);
}

:deep(.p-password),
:deep(.p-password input) {
  width: 100%;
}
</style>
