<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { modeLabel } from './mikrotikDraft'

/**
 * A router's Setup tab (docs/mikrotik-plan.md §11.1): the steps with their live state, the join
 * script (hidden until asked for: viewing it is audit-logged, and in the WireGuard modes it holds a
 * key), the host key with the router's identity, poll now, and the removal script.
 */
const props = defineProps<{ sensor: S['SensorDetail']; canWrite: boolean; isAdmin: boolean }>()
const emit = defineEmits<{ edit: [] }>()
const queryClient = useQueryClient()
const { run, busy } = useActions()

const m = computed(() => props.sensor.mikrotik!)
const script = ref<S['MikrotikScript'] | null>(null)
const removal = ref<S['MikrotikScript'] | null>(null)
watch(
  () => props.sensor.id,
  () => {
    script.value = null
    removal.value = null
  },
)

type Step = { key: string; label: string; state: 'done' | 'current' | 'todo' | 'failed'; detail?: string }
const steps = computed<Step[]>(() => {
  const s = m.value.setupState
  const wireGuard = m.value.mode !== 'DirectSsh'
  const ssh = m.value.mode !== 'WireGuardOnly'
  const list: Step[] = [
    {
      key: 'script',
      label: 'Paste the join script on the router',
      state: s === 'AwaitingRouter' ? 'current' : 'done',
      detail: s === 'AwaitingRouter' && m.value.lastError ? `The last try: ${m.value.lastError}` : undefined,
    },
  ]
  if (wireGuard) {
    list.push({ key: 'tunnel', label: 'Tunnel up', state: s === 'AwaitingRouter' ? 'todo' : 'done' })
  }
  if (ssh) {
    list.push({
      key: 'login',
      label: 'PNMTD logged in as pnmt',
      state: s === 'Ready' ? 'done' : s === 'NeedsAttention' ? 'failed' : 'todo',
      detail: s === 'NeedsAttention' ? (m.value.lastError ?? undefined) : undefined,
    })
  }
  list.push({ key: 'ready', label: 'Ready', state: s === 'Ready' ? 'done' : 'todo' })
  return list
})

async function show() {
  script.value = (await run(() => api.mikrotik.joinScript(props.sensor.id), { failure: 'No join script' })) ?? null
}

async function showRemoval() {
  removal.value =
    (await run(() => api.mikrotik.removalScript(props.sensor.id), { failure: 'No removal script' })) ?? null
}

function download(s: S['MikrotikScript']) {
  const url = URL.createObjectURL(new Blob([s.script], { type: 'text/plain' }))
  const a = document.createElement('a')
  a.href = url
  a.download = s.fileName
  a.click()
  URL.revokeObjectURL(url)
}

async function poll() {
  const detail = await run(() => api.mikrotik.poll(props.sensor.id), { failure: 'Not polled' })
  if (detail) {
    queryClient.setQueryData(keys.sensor(props.sensor.id), { ...props.sensor, mikrotik: detail })
    await queryClient.invalidateQueries({ queryKey: [...keys.sensor(props.sensor.id)] })
  }
}

async function accept() {
  await run(() => api.mikrotik.acceptHostKey(props.sensor.id), {
    success: 'New host key accepted',
    invalidate: [keys.sensor(props.sensor.id)],
  })
}
</script>

<template>
  <div class="setup">
    <section class="panel" aria-labelledby="steps-title">
      <div id="steps-title" class="panel-header">
        Setup · {{ modeLabel(m.mode) }}
        <span class="spacer" />
        <Button v-if="canWrite" label="Edit settings…" icon="pi pi-sliders-h" size="small" text @click="emit('edit')" />
        <Button
          v-if="canWrite && m.mode !== 'WireGuardOnly'"
          label="Poll now"
          icon="pi pi-refresh"
          size="small"
          :loading="busy"
          @click="poll"
        />
      </div>
      <div class="panel-body">
        <ol class="steps">
          <li v-for="step in steps" :key="step.key" :class="step.state">
            <i
              :class="{
                'pi pi-check-circle': step.state === 'done',
                'pi pi-circle': step.state === 'todo',
                'pi pi-arrow-circle-right': step.state === 'current',
                'pi pi-times-circle': step.state === 'failed',
              }"
              aria-hidden="true"
            />
            <div>
              <div>{{ step.label }}</div>
              <small v-if="step.detail" class="muted">{{ step.detail }}</small>
            </div>
          </li>
        </ol>
        <Message v-if="m.scriptOutdated" severity="warn" :closable="false" size="small">
          <div>The router differs from its join script; paste the script again to apply it:</div>
          <ul class="differences">
            <li v-for="d in m.scriptDifferences" :key="d">{{ d }}</li>
          </ul>
        </Message>
      </div>
    </section>

    <section v-if="canWrite" class="panel" aria-labelledby="script-title">
      <div id="script-title" class="panel-header">Join script</div>
      <div class="panel-body script-body">
        <p class="text">
          Paste it into a terminal on the router (WinBox, WebFig or SSH) while signed in as an admin, or upload
          <code>pnmt-join.rsc</code> and run <code>/import file-name=pnmt-join.rsc</code>. It creates the user pnmt,
          which may read but not change anything, and can be pasted again: it replaces what it created before.
        </p>
        <Button v-if="!script" label="Show join script" icon="pi pi-eye" :loading="busy" @click="show" />
        <template v-else>
          <pre class="script" aria-label="Join script">{{ script.script }}</pre>
          <div class="buttons">
            <CopyField :value="script.script" label="Join script" display="Copy the whole script" />
            <Button
              label="Download .rsc"
              icon="pi pi-download"
              size="small"
              severity="secondary"
              @click="download(script)"
            />
          </div>
          <Message v-for="n in script.notes" :key="n" severity="info" :closable="false" size="small">{{ n }}</Message>
          <small class="muted">Every view of a join script is recorded in the audit log.</small>
        </template>
      </div>
    </section>

    <section class="panel" aria-labelledby="hostkey-title">
      <div id="hostkey-title" class="panel-header">The router PNMTD talks to</div>
      <dl class="panel-body facts">
        <dt>Identity</dt>
        <dd>{{ m.identity ?? '–' }}</dd>
        <dt>Model</dt>
        <dd>{{ m.model ?? m.boardName ?? '–' }}</dd>
        <dt>Serial number</dt>
        <dd class="mono">{{ m.serialNumber ?? '–' }}</dd>
        <dt>Host key</dt>
        <dd>
          <template v-if="m.hostKey">
            <span class="mono">{{ m.hostKey.type }} SHA256:{{ m.hostKey.fingerprint }}</span>
            <div class="muted small">pinned <RelativeTime :value="m.hostKey.at" /> at the first login</div>
          </template>
          <span v-else class="muted">pinned at the first login</span>
        </dd>
        <template v-if="m.pendingHostKey">
          <dt>Presented instead</dt>
          <dd>
            <span class="mono bad">{{ m.pendingHostKey.type }} SHA256:{{ m.pendingHostKey.fingerprint }}</span>
            <div class="muted small">
              seen <RelativeTime :value="m.pendingHostKey.at" />. PNMTD refuses to log in until an Admin accepts it, for
              example after the router was replaced or reinstalled.
            </div>
            <Button
              v-if="isAdmin"
              label="Accept the new host key"
              icon="pi pi-check"
              size="small"
              severity="danger"
              class="accept"
              @click="accept"
            />
          </dd>
        </template>
      </dl>
    </section>

    <section v-if="canWrite" class="panel" aria-labelledby="removal-title">
      <div id="removal-title" class="panel-header">Removing PNMT from the router</div>
      <div class="panel-body script-body">
        <p class="text">
          Deleting the sensor stops PNMTD, but what the join script created stays on the router: PNMTD may not change
          it. The removal script takes it away.
        </p>
        <Button
          v-if="!removal"
          label="Show removal script"
          icon="pi pi-eye"
          severity="secondary"
          @click="showRemoval"
        />
        <template v-else>
          <pre class="script" aria-label="Removal script">{{ removal.script }}</pre>
          <div class="buttons">
            <CopyField :value="removal.script" label="Removal script" display="Copy the whole script" />
            <Button
              label="Download .rsc"
              icon="pi pi-download"
              size="small"
              severity="secondary"
              @click="download(removal)"
            />
          </div>
        </template>
      </div>
    </section>
  </div>
</template>

<style scoped>
.setup {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.panel-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.steps {
  list-style: none;
  margin: 0 0 var(--pnmt-space-3);
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-6);
}

.steps li {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.steps .done i {
  color: var(--pnmt-ok);
}

.steps .current i {
  color: var(--pnmt-warn);
}

.steps .failed i {
  color: var(--pnmt-fail);
}

.steps .todo {
  color: var(--pnmt-muted);
}

.differences {
  margin: var(--pnmt-space-1) 0 0;
  padding-left: var(--pnmt-space-4);
}

.script-body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  align-items: flex-start;
}

.text {
  margin: 0;
}

.script {
  width: 100%;
  max-height: 24rem;
  overflow: auto;
  margin: 0;
  padding: var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre;
}

.buttons {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.mono {
  font-family: var(--pnmt-mono);
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}

.bad {
  color: var(--pnmt-fail);
}

.accept {
  margin-top: var(--pnmt-space-2);
}
</style>
