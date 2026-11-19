<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import type { TakenScript } from './connectorFormat'

/**
 * The script's selectors (docs/connectors-plan.md §3.3, §13.5): *From the library…* (a script and a version, the current one by
 * default) or *From the server…* (a script registered on a Windows server, with its account). It shows the text and its parameters
 * before it is taken. It copies: the connector keeps its own text, and nothing links it to the library or the server.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  type: S['ConnectorType']
  source: 'library' | 'registered'
  serverId?: string | null
  current?: string | null
}>()
const emit = defineEmits<{ take: [script: TakenScript] }>()

const library = useQuery({
  queryKey: computed(() => [...keys.connectors, 'lookups', 'library', props.type]),
  queryFn: () => api.connectors.library(props.type),
  enabled: computed(() => visible.value && props.source === 'library'),
})
const registered = useQuery({
  queryKey: computed(() => [...keys.connectors, 'lookups', 'registered', props.serverId]),
  queryFn: () => api.connectors.registered(props.serverId!),
  enabled: computed(() => visible.value && props.source === 'registered' && !!props.serverId),
})
const scriptId = ref<string | null>(null)
const versionNumber = ref<number | null>(null)
const registeredName = ref<string | null>(null)
watch(visible, (open) => {
  if (open) {
    scriptId.value = null
    versionNumber.value = null
    registeredName.value = null
  }
})
const chosen = computed(() => library.data.value?.find((s) => s.id === scriptId.value) ?? null)
// Objects, not bare numbers: PrimeVue's Select resolves a label only on an object.
const versionOptions = computed(() =>
  (chosen.value?.versions ?? []).map((n) => ({
    label: n === chosen.value?.currentVersion ? `v${n} (current)` : `v${n}`,
    value: n,
  })),
)
watch(chosen, (s) => (versionNumber.value = s?.currentVersion ?? null))
const version = useQuery({
  queryKey: computed(() => [...keys.connectors, 'lookups', 'version', scriptId.value, versionNumber.value]),
  queryFn: () => api.connectors.libraryVersion(scriptId.value!, versionNumber.value!),
  enabled: computed(() => visible.value && !!scriptId.value && versionNumber.value !== null),
})
const local = computed(() => registered.data.value?.find((r) => r.name === registeredName.value) ?? null)
const text = computed(() => (props.source === 'library' ? version.data.value?.text : local.value?.text) ?? null)
const parameters = computed(
  () => (props.source === 'library' ? version.data.value?.parsed : local.value?.parsed) ?? null,
)

function take() {
  if (props.source === 'library' && version.data.value) {
    emit('take', {
      name: version.data.value.name,
      text: version.data.value.text,
      takenFrom: version.data.value.takenFrom,
    })
  } else if (local.value?.text) {
    emit('take', {
      name: local.value.name,
      text: local.value.text,
      takenFrom: local.value.takenFrom,
      runAsKind: local.value.runAs,
      account: local.value.account,
    })
  }
  visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="source === 'library' ? 'From the library' : 'From the server'"
    modal
    :style="{ width: '56rem' }"
    :breakpoints="{ '900px': '96vw' }"
  >
    <div class="form">
      <template v-if="source === 'library'">
        <div class="form-grid">
          <div class="field">
            <label for="take-script">Script</label>
            <Select
              v-model="scriptId"
              input-id="take-script"
              :options="library.data.value ?? []"
              option-label="name"
              option-value="id"
              filter
              placeholder="Choose a script"
              empty-message="No script of this language in the library"
            />
          </div>
          <div class="field">
            <label for="take-version">Version</label>
            <Select
              v-model="versionNumber"
              input-id="take-version"
              :options="versionOptions"
              option-label="label"
              option-value="value"
              :disabled="!chosen"
            />
          </div>
        </div>
      </template>
      <template v-else>
        <Message v-if="!serverId" severity="info" :closable="false" size="small"
          >Choose the server first: its registered scripts are listed.</Message
        >
        <div v-else class="field">
          <label for="take-registered">Registered script</label>
          <Select
            v-model="registeredName"
            input-id="take-registered"
            :options="registered.data.value ?? []"
            :option-label="
              (r: S['ConnectorRegisteredScript']) =>
                `${r.name} (as ${r.runAsName})${r.matches ? '' : ', changed since its registration'}`
            "
            option-value="name"
            placeholder="Choose a script"
            empty-message="No script is registered on this server"
          />
          <small class="field-hint"
            >It runs as the account it is registered with: taking it fills the account too.</small
          >
        </div>
      </template>
      <Message v-if="current && text && text !== current" severity="warn" :closable="false" size="small">
        This replaces the connector's current text: the arguments follow the new parameters when you save.
      </Message>
      <pre v-if="text" class="box mono">{{ text }}</pre>
      <p v-if="parameters" class="small muted">
        Parameters: {{ parameters.parameters.map((p) => p.name).join(', ') || 'none' }}
        <template v-if="parameters.functions?.length">
          · functions: {{ parameters.functions.map((f) => f.name).join(', ') }}</template
        >
      </p>
    </div>
    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="visible = false" />
      <Button label="Take it" icon="pi pi-download" :disabled="!text" @click="take" />
    </template>
  </Dialog>
</template>

<style scoped>
.box {
  white-space: pre;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  max-height: 40vh;
  overflow: auto;
  font-size: 0.8rem;
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
