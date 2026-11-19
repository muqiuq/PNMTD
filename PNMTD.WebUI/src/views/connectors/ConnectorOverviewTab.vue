<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, connectorRequest, keys, type S } from '@/api/resources'
import DiffView from '@/components/DiffView.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { lineDiff } from '@/components/markdown/lineDiff'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'

/**
 * A connector's overview (docs/connectors-plan.md §13.5): the target as a sentence, the server the name finds now, the warnings, the
 * library's hint with *Compare…* and *Take vN…*; the answer (wait or ticket, the wait, *Return output*), enabled, the last 24 hours.
 */
const props = defineProps<{ connector: S['ConnectorDetail'] }>()
const session = useSession()
const canManage = computed(() => session.can('Connectors', 'Manage'))
const mode = ref(props.connector.mode)
const waitSeconds = ref(props.connector.waitSeconds)
const returnOutput = ref(props.connector.returnOutput)
const enabled = ref(props.connector.enabled)
watch(
  () => props.connector,
  (c) => {
    mode.value = c.mode
    waitSeconds.value = c.waitSeconds
    returnOutput.value = c.returnOutput
    enabled.value = c.enabled
  },
)
const dirty = computed(
  () =>
    mode.value !== props.connector.mode ||
    waitSeconds.value !== props.connector.waitSeconds ||
    returnOutput.value !== props.connector.returnOutput ||
    enabled.value !== props.connector.enabled,
)
const modes = [
  { label: 'Wait for the outcome', value: 'Wait' },
  { label: 'A ticket at once', value: 'Ticket' },
]
const { run } = useActions()
async function save() {
  await run(
    () =>
      api.connectors.update(
        props.connector.id,
        connectorRequest(props.connector, {
          mode: mode.value,
          waitSeconds: waitSeconds.value,
          returnOutput: returnOutput.value,
          enabled: enabled.value,
        }),
      ),
    { success: 'Saved', invalidate: [keys.connectors] },
  )
}

const comparing = ref(false)
const compare = useQuery({
  queryKey: computed(() => [...keys.connector(props.connector.id), 'compare']),
  queryFn: () => api.connectors.compare(props.connector.id),
  enabled: comparing,
})
const diff = computed(() =>
  compare.data.value ? lineDiff(compare.data.value.connectorText, compare.data.value.libraryText) : null,
)
async function takeLibrary() {
  const hint = props.connector.library
  if (!hint) return
  const version = await run(() => api.connectors.libraryVersion(hint.scriptId, hint.version))
  if (!version) return
  const c = props.connector
  await run(
    () =>
      api.connectors.update(
        c.id,
        connectorRequest(c, {
          target: { ...connectorRequest(c).target, scriptText: version.text, takenFrom: version.takenFrom },
        }),
      ),
    { success: `Took v${hint.version}: check the arguments`, invalidate: [keys.connectors] },
  )
  comparing.value = false
}
const states = computed(() => Object.entries(props.connector.figures.byState24h).sort((a, b) => b[1] - a[1]))
</script>

<template>
  <div class="tab">
    <section class="panel panel-body">
      <p class="sentence">{{ connector.target.sentence }}</p>
      <p class="small">
        <template v-if="connector.target.serverNow">
          The name finds
          <RouterLink :to="{ name: 'sensor', params: { id: connector.target.serverNow.id } }">{{
            connector.target.serverNow.name
          }}</RouterLink>
          now.
        </template>
        <template v-else-if="connector.target.serversFound.length > 1">
          The name finds {{ connector.target.serversFound.length }} servers:
          {{ connector.target.serversFound.join(', ') }}.
        </template>
        <template v-else>The name finds no server now.</template>
      </p>
      <p class="muted small">{{ connector.purpose }}</p>
      <Message v-for="w in connector.warnings" :key="w.code" severity="warn" :closable="false" size="small">{{
        w.message
      }}</Message>
      <Message v-if="connector.library?.differs" severity="info" :closable="false" size="small">
        The library's {{ connector.library.name }} is at v{{ connector.library.version }} and differs from this
        connector's text.
        <Button label="Compare…" size="small" text @click="comparing = true" />
      </Message>
    </section>

    <section class="panel panel-body">
      <h3>Answer</h3>
      <div class="form">
        <div class="field">
          <label id="overview-mode">Mode</label>
          <SelectButton
            v-model="mode"
            :options="modes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :disabled="!canManage"
            aria-labelledby="overview-mode"
          />
        </div>
        <div v-if="mode === 'Wait'" class="field">
          <label for="overview-wait">The longest wait (seconds)</label>
          <InputNumber
            v-model="waitSeconds"
            input-id="overview-wait"
            :min="5"
            :max="connector.maxWaitSeconds"
            :disabled="!canManage"
          />
          <small class="field-hint">A call not final by then is answered with its ticket (202).</small>
        </div>
        <div class="field">
          <label for="overview-output">Return output</label>
          <ToggleSwitch v-model="returnOutput" input-id="overview-output" :disabled="!canManage" />
          <small class="field-hint"
            >Off: the output stays in PNMT; the caller gets the state, the exit code and the times.</small
          >
        </div>
        <div class="field">
          <label for="overview-enabled">Enabled</label>
          <ToggleSwitch v-model="enabled" input-id="overview-enabled" :disabled="!canManage" />
          <small class="field-hint">Disabling cancels the connector's queued calls.</small>
        </div>
        <div v-if="canManage" class="actions">
          <Button label="Save" icon="pi pi-check" :disabled="!dirty" @click="save" />
        </div>
      </div>
    </section>

    <section class="panel panel-body">
      <h3>The last 24 hours</h3>
      <p>
        {{ connector.figures.calls24h }} calls<template v-for="[s, n] in states" :key="s"> · {{ n }} {{ s }}</template
        >; now {{ connector.figures.queued }} queued, {{ connector.figures.running }} running. The last call:
        <RelativeTime :value="connector.lastCallAt" empty="never" />.
      </p>
      <p class="small muted">
        At most {{ connector.maxCallsPerHour }} calls an hour and {{ connector.maxQueued }} waiting, each for at most
        {{ connector.maxQueueSeconds }} s; one run of its script on its server at a time.
      </p>
    </section>

    <Dialog
      v-model:visible="comparing"
      header="This connector's text and the library's"
      modal
      maximizable
      :style="{ width: '80rem' }"
    >
      <p v-if="compare.data.value" class="small muted">
        − this connector's text, + {{ compare.data.value.name }} v{{ compare.data.value.version }} in the library.
      </p>
      <DiffView v-if="diff" :diff="diff" />
      <template #footer>
        <Button label="Close" text severity="secondary" @click="comparing = false" />
        <Button
          v-if="canManage && connector.library"
          :label="`Take v${connector.library.version}`"
          icon="pi pi-download"
          @click="takeLibrary"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.sentence {
  font-weight: 600;
  margin-top: 0;
}

.actions {
  display: flex;
  justify-content: flex-end;
}

.small {
  font-size: 0.857rem;
}
</style>
