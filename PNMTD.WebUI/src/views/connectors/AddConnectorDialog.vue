<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import TargetForm from './TargetForm.vue'
import { slugOf, typeLabel } from './connectorFormat'

/**
 * *Add connector…* (docs/connectors-plan.md §13.3) in three steps: the type; the target, each field as text with its selector; the
 * name, the slug (suggested from the name) and the purpose. Saved disabled, it opens on *Arguments*, because they come next.
 */
const visible = defineModel<boolean>('visible', { required: true })
const router = useRouter()
const step = ref<1 | 2 | 3>(1)
const type = ref<S['ConnectorType']>('WindowsScript')
const blank = (t: S['ConnectorType']): S['ConnectorTargetRequest'] => ({
  server: '',
  scriptName: '',
  scriptText: '',
  takenFrom: null,
  function: null,
  runAsKind: 'Service',
  account: null,
  shell: t === 'LinuxScript' ? 'bash' : 'powershell',
  timeoutSeconds: 600,
})
const target = ref<S['ConnectorTargetRequest']>(blank('WindowsScript'))
const name = ref('')
const slug = ref('')
const slugTouched = ref(false)
const purpose = ref('')
watch(visible, (open) => {
  if (!open) return
  step.value = 1
  name.value = ''
  slug.value = ''
  slugTouched.value = false
  purpose.value = ''
})
watch(name, (n) => {
  if (!slugTouched.value) slug.value = slugOf(n)
})
function choose(t: S['ConnectorType']) {
  type.value = t
  target.value = blank(t)
  step.value = 2
}
const targetReady = computed(
  () => !!target.value.server.trim() && !!target.value.scriptName.trim() && !!target.value.scriptText.trim(),
)
const { run, busy } = useActions()
async function save() {
  const created = await run(
    () =>
      api.connectors.create({
        type: type.value,
        name: name.value.trim(),
        slug: slug.value.trim(),
        purpose: purpose.value.trim(),
        enabled: false,
        target: { ...target.value, function: target.value.function || null },
        arguments: null,
        mode: null,
        waitSeconds: null,
        returnOutput: null,
        maxCallsPerHour: null,
        maxQueued: null,
        maxQueueSeconds: null,
        reviewEnabled: null,
        reviewPurpose: null,
        reviewThreshold: null,
        reviewChecks: null,
        reviewFallback: null,
        callerIds: null,
      }),
    { success: 'Added, disabled: set its arguments next', invalidate: [keys.connectors] },
  )
  if (created) {
    visible.value = false
    await router.push({ name: 'connector', params: { id: created.id, tab: 'arguments' } })
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="step === 1 ? 'Add connector' : `Add connector: ${typeLabel(type)}`"
    modal
    :style="{ width: step === 2 ? '64rem' : '40rem' }"
    :breakpoints="{ '900px': '98vw' }"
  >
    <div v-if="step === 1" class="types">
      <button type="button" class="type-card" @click="choose('WindowsScript')">
        <i class="pi pi-microsoft" />
        <strong>Windows Server Script</strong>
        <span class="muted small"
          >A PowerShell script, or a function in one, on a Windows server with the PNMT agent.</span
        >
      </button>
      <button type="button" class="type-card" @click="choose('LinuxScript')">
        <i class="pi pi-desktop" />
        <strong>Linux Server Script</strong>
        <span class="muted small">A bash script, or a function in one, on a Linux server with the PNMT agent.</span>
      </button>
    </div>
    <TargetForm v-else-if="step === 2" v-model="target" :type="type" />
    <form v-else class="form" @submit.prevent="save">
      <div class="field">
        <label for="connector-name">Name</label>
        <InputText id="connector-name" v-model="name" placeholder="Shop: flush cache" autofocus />
      </div>
      <div class="field">
        <label for="connector-slug">Slug</label>
        <InputText id="connector-slug" v-model="slug" class="mono" @input="slugTouched = true" />
        <small class="field-hint">In the URL: /connector/v1/{{ slug || '…' }}</small>
      </div>
      <div class="field">
        <label for="connector-purpose">Purpose</label>
        <Textarea
          id="connector-purpose"
          v-model="purpose"
          rows="3"
          auto-resize
          placeholder="The shop's back office flushes a site's output cache after a product import."
        />
        <small class="field-hint"
          >For the people in PNMT, the caller's developers, and the review, which judges calls against it.</small
        >
      </div>
    </form>
    <template #footer>
      <Button v-if="step > 1" label="Back" text severity="secondary" @click="step = (step - 1) as 1 | 2" />
      <Button label="Cancel" text severity="secondary" @click="visible = false" />
      <Button v-if="step === 2" label="Next" icon="pi pi-arrow-right" :disabled="!targetReady" @click="step = 3" />
      <Button
        v-if="step === 3"
        label="Add, disabled"
        icon="pi pi-check"
        :loading="busy"
        :disabled="!name.trim() || !slug.trim() || !purpose.trim()"
        @click="save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.types {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-3);
}

.type-card {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  align-items: flex-start;
  text-align: left;
  padding: var(--pnmt-space-4);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  color: var(--pnmt-text);
  cursor: pointer;
  font: inherit;
}

.type-card:hover,
.type-card:focus-visible {
  border-color: var(--p-primary-color);
}

.type-card i {
  font-size: 1.5rem;
}

.small {
  font-size: 0.857rem;
}
</style>
