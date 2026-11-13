<script setup lang="ts">
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import BackupScheduleField from './BackupScheduleField.vue'
import { MODES, type MikrotikDraft } from './mikrotikDraft'

/**
 * The router section of the sensor form (docs/mikrotik-plan.md §11.1), and of the dialog that edits
 * an existing router: the mode, Direct SSH's address, port and PNMTD's source address, the backup
 * schedule, and for Admins the settings that widen what PNMT may do on the router.
 */
const draft = defineModel<MikrotikDraft>({ required: true })
const props = withDefaults(
  defineProps<{
    errors: Record<string, string | undefined>
    isAdmin: boolean
    /** The WireGuard gateway runs (phase M3); until then only Direct SSH. */
    wireGuard?: boolean
  }>(),
  { wireGuard: false },
)

const set = (patch: Partial<MikrotikDraft>) => (draft.value = { ...draft.value, ...patch })
// The backup key's fingerprint, for the encryption switch (docs/mikrotik-backup-plan.md §3.3).
const backupSettings = useQuery({
  queryKey: [...keys.mikrotik, 'backup-settings'],
  queryFn: api.mikrotik.backupSettings,
  enabled: computed(() => props.isAdmin),
})
const backupKey = computed(() => backupSettings.data.value?.key?.fingerprint ?? null)
const direct = computed(() => draft.value.mode === 'DirectSsh')
const modeOptions = computed(() => MODES.map((m) => ({ ...m, disabled: m.needsGateway && !props.wireGuard })))
const modeHint = computed(() => MODES.find((m) => m.value === draft.value.mode)?.hint ?? '')
</script>

<template>
  <fieldset class="mikrotik-fields wide">
    <legend>MikroTik router</legend>
    <div class="grid">
      <div class="field wide">
        <label for="mikrotik-mode">Connection</label>
        <Select
          :model-value="draft.mode"
          input-id="mikrotik-mode"
          :options="modeOptions"
          option-label="label"
          option-value="value"
          option-disabled="disabled"
          :invalid="!!errors['mikrotik.mode']"
          @update:model-value="(v: MikrotikDraft['mode']) => set({ mode: v })"
        >
          <template #option="{ option }">
            <span class="option">
              <span>{{ option.label }}</span>
              <small class="muted">{{
                option.disabled ? 'Needs the WireGuard gateway, which this installation does not run yet.' : option.hint
              }}</small>
            </span>
          </template>
        </Select>
        <small v-if="errors['mikrotik.mode']" class="field-error">{{ errors['mikrotik.mode'] }}</small>
        <small v-else class="field-hint">{{ modeHint }}</small>
      </div>
      <template v-if="direct">
        <div class="field">
          <label for="mikrotik-address">Address</label>
          <InputText
            id="mikrotik-address"
            :model-value="draft.address"
            class="mono"
            placeholder="192.0.2.1 or gw.example.org"
            :invalid="!!errors['mikrotik.address']"
            @update:model-value="(v?: string) => set({ address: v ?? '' })"
          />
          <small v-if="errors['mikrotik.address']" class="field-error">{{ errors['mikrotik.address'] }}</small>
          <small v-else class="field-hint">Where PNMTD reaches the router's SSH service.</small>
        </div>
        <div class="field">
          <label for="mikrotik-port">SSH port</label>
          <InputNumber
            :model-value="draft.sshPort"
            input-id="mikrotik-port"
            :min="1"
            :max="65535"
            :use-grouping="false"
            :invalid="!!errors['mikrotik.sshPort']"
            @update:model-value="(v: number | null) => set({ sshPort: v })"
          />
          <small v-if="errors['mikrotik.sshPort']" class="field-error">{{ errors['mikrotik.sshPort'] }}</small>
          <small v-else class="field-hint">22 unless the router or a port forward uses another.</small>
        </div>
        <div class="field wide">
          <label for="mikrotik-source"
            >PNMTD's address, as the router sees it <span class="muted">(recommended)</span></label
          >
          <InputText
            id="mikrotik-source"
            :model-value="draft.sourceAddress"
            class="mono"
            placeholder="203.0.113.5 or 203.0.113.0/28"
            :invalid="!!errors['mikrotik.sourceAddress']"
            @update:model-value="(v?: string) => set({ sourceAddress: v ?? '' })"
          />
          <small v-if="errors['mikrotik.sourceAddress']" class="field-error">{{
            errors['mikrotik.sourceAddress']
          }}</small>
          <small v-else class="field-hint"
            >The user pnmt may log in only from it. Empty: from any address, with PNMTD's key only.</small
          >
        </div>
      </template>
      <div class="field">
        <label for="mikrotik-backup">Scheduled backups</label>
        <BackupScheduleField
          :model-value="draft.backupIntervalHours"
          input-id="mikrotik-backup"
          :invalid="!!errors['mikrotik.backupIntervalHours']"
          @update:model-value="(v) => set({ backupIntervalHours: v })"
        />
        <small v-if="errors['mikrotik.backupIntervalHours']" class="field-error">{{
          errors['mikrotik.backupIntervalHours']
        }}</small>
        <small v-else-if="draft.backupIntervalHours === null" class="field-hint"
          >No scheduled export. Back up now, updates and commands still take one.</small
        >
        <small v-else class="field-hint"
          >How often the configuration is exported; a version is stored only when it changed.</small
        >
      </div>
      <template v-if="isAdmin">
        <div class="switch-row wide">
          <ToggleSwitch
            :model-value="draft.encryptBackups ?? false"
            input-id="mikrotik-encrypt"
            :disabled="!backupKey && !draft.encryptBackups"
            @update:model-value="(v: boolean) => set({ encryptBackups: v })"
          />
          <label for="mikrotik-encrypt">Encrypt backups</label>
        </div>
        <small class="field-hint wide hint">
          <template v-if="backupKey"
            >One way, with the backup key {{ backupKey }}: PNMT stores what it cannot read. Load the key in the Backups
            tab to read them.</template
          >
          <template v-else>Needs the backup key: Admin → MikroTik &amp; WireGuard → Backups.</template>
        </small>
        <div class="switch-row wide">
          <ToggleSwitch
            :model-value="draft.diagnostics"
            input-id="mikrotik-diagnostics"
            @update:model-value="(v: boolean) => set({ diagnostics: v })"
          />
          <label for="mikrotik-diagnostics">Diagnostics: ping and traceroute as pnmt</label>
        </div>
        <small class="field-hint wide hint"
          >Adds the policy test to the group pnmt-read. It also allows the bandwidth test, which can fill a link, and a
          wireless scan, which interrupts Wi-Fi clients.</small
        >
        <div class="switch-row wide">
          <ToggleSwitch
            :model-value="draft.includeSecrets"
            input-id="mikrotik-secrets"
            @update:model-value="(v: boolean) => set({ includeSecrets: v })"
          />
          <label for="mikrotik-secrets">Include secrets in exports</label>
        </div>
        <small class="field-hint wide hint"
          >Adds the policy sensitive. An export then restores the router completely, but every backup becomes as
          sensitive as the router's own secrets; only Admins may see those.</small
        >
        <div v-if="!direct" class="switch-row wide">
          <ToggleSwitch
            :model-value="draft.managementAccess"
            input-id="mikrotik-management"
            @update:model-value="(v: boolean) => set({ managementAccess: v })"
          />
          <label for="mikrotik-management">Management access through custom tunnels</label>
        </div>
      </template>
      <Message v-if="errors['mikrotik']" severity="error" :closable="false" class="wide" size="small">{{
        errors['mikrotik']
      }}</Message>
    </div>
  </fieldset>
</template>

<style scoped>
.mikrotik-fields {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pnmt-space-3);
  margin: 0;
  min-width: 0;
}

legend {
  font-weight: 600;
  padding: 0 var(--pnmt-space-1);
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.grid .wide {
  grid-column: 1 / -1;
}

.option {
  display: flex;
  flex-direction: column;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 32px;
}

.hint {
  margin-top: calc(-1 * var(--pnmt-space-2));
}

.mono :deep(input),
input.mono {
  font-family: var(--pnmt-mono);
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
