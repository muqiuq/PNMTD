<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import RadioButton from 'primevue/radiobutton'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import {
  diskText,
  downloadText,
  installBlocked,
  modeBlocked,
  modeLabel,
  modePackages,
  planLine,
  planNotes,
  startMode,
} from './osUpdatesFormat'
import { osUpdatesInvalidate } from './useOsUpdates'

/**
 * *Install updates…* (docs/linux-os-updates-plan.md §10.2): the mode, upgrade (never removes) or full upgrade (may remove), full
 * upgrade by default on Proxmox VE (D3), a mode the server refuses disabled with the reason; the plan from the latest section
 * with the removals in red by name, the download and the disk space; the notes where they apply; with removals, a checkbox
 * before *Install*. A full upgrade sends the removals' hash shown here, so the agent refuses if the plan changed meanwhile (§4.4).
 * After *Install* the dialog closes and the tab follows the run.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ view: S['OsUpdatesView'] }>()
const emit = defineEmits<{ started: [runId: string] }>()
const { run, busy, notify } = useActions()

const mode = ref<S['OsUpgradeMode']>('Upgrade')
const seen = ref(false)
const refused = ref<string | null>(null)
watch(visible, (open) => {
  if (!open) return
  mode.value = startMode(props.view)
  seen.value = false
  refused.value = null
})
const plan = computed(() => (mode.value === 'FullUpgrade' ? props.view.fullUpgrade : props.view.upgrade))
// A new plan (the section changed while the dialog was open) has to be looked at again.
watch(
  () => plan.value?.removalsHash,
  () => (seen.value = false),
)
const modes: { value: S['OsUpgradeMode']; hint: string }[] = [
  { value: 'Upgrade', hint: 'apt-get upgrade --with-new-pkgs: installs new dependencies, never removes a package.' },
  { value: 'FullUpgrade', hint: 'apt-get full-upgrade: may remove packages to install others. Proxmox VE needs it.' },
]
const general = computed(() => installBlocked(props.view))
const blocked = (m: S['OsUpgradeMode']) => modeBlocked(props.view, m)
const removals = computed(() => (mode.value === 'FullUpgrade' ? (plan.value?.removals ?? []) : []))
const notes = computed(() => planNotes(modePackages(props.view.packages, mode.value), mode.value, props.view.proxmox))
const ready = computed(
  () => !!plan.value && !general.value && !blocked(mode.value) && (removals.value.length === 0 || seen.value),
)

async function install() {
  const p = plan.value
  if (!p || !ready.value) return
  refused.value = null
  const chosen = mode.value
  const started = await run(
    () =>
      api.linux.osUpdates.upgrade(props.view.sensorId, {
        mode: chosen,
        removalsHash: chosen === 'FullUpgrade' ? p.removalsHash : null,
      }),
    { failure: 'Not started', invalidate: osUpdatesInvalidate(props.view.sensorId) },
  )
  if (!started) return
  if (started.state === 'Refused' || started.state === 'Lost') {
    refused.value =
      started.message ??
      (started.state === 'Lost' ? "The server didn't answer: nothing ran." : 'The server refused: nothing ran.')
    return
  }
  notify.success(`${modeLabel(chosen)} started`, 'Its log follows on the OS updates tab.')
  emit('started', started.runId)
  visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Install updates on ${view.hostname}`"
    modal
    :style="{ width: '42rem' }"
    :breakpoints="{ '700px': '95vw' }"
  >
    <form id="install-updates-form" class="form" novalidate @submit.prevent="install">
      <Message v-if="general" severity="warn" :closable="false" size="small">{{ general }}</Message>
      <fieldset class="modes">
        <legend>Mode</legend>
        <div v-for="m in modes" :key="m.value" class="mode">
          <RadioButton
            v-model="mode"
            :input-id="`install-mode-${m.value}`"
            :value="m.value"
            :disabled="!!blocked(m.value)"
          />
          <label :for="`install-mode-${m.value}`" :class="{ off: !!blocked(m.value) }">
            <strong>{{ modeLabel(m.value) }}</strong>
            <Tag v-if="m.value === view.defaultMode" value="default here" severity="secondary" class="tag" />
            <span class="muted small hint">{{ m.hint }}</span>
            <span v-if="blocked(m.value)" class="small warn hint">{{ blocked(m.value) }}</span>
          </label>
        </div>
      </fieldset>
      <template v-if="plan && !blocked(mode)">
        <dl class="facts">
          <dt>Plan</dt>
          <dd>{{ planLine(plan) }}</dd>
          <template v-if="removals.length">
            <dt class="bad">Removed</dt>
            <dd>
              <ul class="removals mono">
                <li v-for="r in removals" :key="r">{{ r }}</li>
              </ul>
            </dd>
          </template>
          <dt>Download</dt>
          <dd>{{ downloadText(plan.downloadBytes) }}</dd>
          <dt>Disk space</dt>
          <dd>{{ diskText(plan.diskBytes) }}</dd>
        </dl>
        <Message v-for="n in notes" :key="n" severity="info" :closable="false" size="small">{{ n }}</Message>
        <label v-if="removals.length" class="check"
          ><Checkbox v-model="seen" binary input-id="install-seen" /> I've seen the packages that will be removed</label
        >
      </template>
      <small class="field-hint">
        It runs non-interactively in a unit of its own on the server, so an agent restart doesn't stop it. Local
        configuration files are kept. PNMT never reboots and restarts no service.
      </small>
      <Message v-if="refused" severity="error" :closable="false" size="small">{{ refused }}</Message>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="install-updates-form"
        label="Install"
        icon="pi pi-download"
        :loading="busy"
        :disabled="!ready"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.modes {
  border: 0;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.modes legend {
  padding: 0;
  margin-bottom: var(--pnmt-space-1);
  font-weight: 500;
}

.mode {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.mode label.off strong {
  color: var(--pnmt-muted);
}

.hint {
  display: block;
}

.tag {
  margin-left: var(--pnmt-space-2);
  font-size: 0.75rem;
}

.removals {
  list-style: none;
  margin: 0;
  padding: 0;
  color: var(--pnmt-fail);
  max-height: 10rem;
  overflow: auto;
}

.bad {
  color: var(--pnmt-fail);
}

.warn {
  color: var(--pnmt-warn);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}
</style>
