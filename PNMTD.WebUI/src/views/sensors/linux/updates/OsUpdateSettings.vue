<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { useActions } from '@/composables/useActions'
import { platformCodes } from '@/views/admin/servers/agentFormat'
import { CONFIG_YAML, osRightsRows } from './osUpdatesFormat'
import { osUpdatesInvalidate } from './useOsUpdates'

/**
 * A server's OS update settings (docs/linux-os-updates-plan.md §10.1 6, §3, §5.4), for Servers: Manage: its switches (OS
 * updates, high-risk alerts 466, update failures 467), and what config.yaml allows on the server, line by line, with the line
 * that would allow what is off. Only root changes config.yaml.
 */
const props = defineProps<{ view: S['OsUpdatesView'] }>()
const emit = defineEmits<{ changed: [view: S['OsUpdatesView']] }>()
const { run, busy } = useActions()
const codes = platformCodes('Linux')

const form = ref<S['OsUpdateServerSettings']>({ ...props.view.settings })
watch(
  () => props.view.settings,
  (s) => (form.value = { ...s }),
)
const dirty = computed(() => JSON.stringify(form.value) !== JSON.stringify(props.view.settings))
async function save() {
  const view = await run(() => api.linux.osUpdates.settings(props.view.sensorId, form.value), {
    success: 'Saved',
    invalidate: osUpdatesInvalidate(props.view.sensorId),
  })
  if (view) emit('changed', view)
}
const rows = computed(() => (props.view.rights ? osRightsRows(props.view.rights) : []))
</script>

<template>
  <section class="panel" aria-labelledby="os-settings-title">
    <div id="os-settings-title" class="panel-header">Settings</div>
    <div class="panel-body two">
      <form class="form" @submit.prevent="save">
        <label class="check"
          ><Checkbox v-model="form.enabled" binary /> OS updates on this server: the daily apt-get update, and the
          buttons</label
        >
        <label class="check"
          ><Checkbox v-model="form.highRiskEvents" binary /> {{ codes.highRisk }} while a pending update fixes a
          high-risk vulnerability</label
        >
        <small v-if="!view.checkEnabled" class="field-hint"
          >The high-risk check is off in PNMT: OsUpdates__Advisories__Enabled=true in .env turns it on.</small
        >
        <label class="check"
          ><Checkbox v-model="form.updateFailedEvents" binary /> {{ codes.updateFailed }} for a failed upgrade,
          interrupted dpkg, or lists not refreshed for days</label
        >
        <small class="field-hint">Nothing is sent without a notification rule.</small>
        <div class="form-actions">
          <Button type="submit" label="Save" size="small" :disabled="!dirty" :loading="busy" />
        </div>
      </form>
      <div class="allows">
        <h4>What config.yaml allows</h4>
        <Message v-if="view.rightsRefused" severity="error" :closable="false" size="small">
          {{ CONFIG_YAML }} is refused on the server: it grants nothing until root fixes it.
        </Message>
        <p v-else-if="!view.rights" class="muted small none">Not announced yet: the agent names it when it connects.</p>
        <dl v-else class="facts">
          <template v-for="r in rows" :key="r.label">
            <dt>{{ r.label }}</dt>
            <dd>
              <Tag :value="r.on ? 'allowed' : 'off'" :severity="r.on ? 'success' : 'secondary'" />
              <CopyField v-if="r.grant" :value="r.grant" :label="`config.yaml for ${r.label}`" />
            </dd>
          </template>
        </dl>
        <small class="field-hint">
          In {{ CONFIG_YAML }}'s <code>os_updates:</code> block. A missing key means allowed; a <code>false</code> there
          is final, and only root changes it.
        </small>
      </div>
    </div>
  </section>
</template>

<style scoped>
.two {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
  gap: var(--pnmt-space-4);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.allows {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

h4 {
  margin: 0;
  font-size: 0.929rem;
}

.facts :deep(.copy-field) {
  margin-top: var(--pnmt-space-1);
}

.none {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
