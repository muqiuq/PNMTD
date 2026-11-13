<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import BackupScheduleField from '@/views/sensors/mikrotik/BackupScheduleField.vue'
import { scheduleError, type BackupSchedule } from '@/views/sensors/mikrotik/backupSchedule'

/**
 * Backup settings… for the routers chosen in the Routers list (docs/mikrotik-backup-plan.md §2.2, §3.3): one schedule
 * for all of them, off included, and their backups encrypted or not.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ routers: { id: string; name: string }[] }>()
const emit = defineEmits<{ changed: [] }>()
const settings = useQuery({ queryKey: [...keys.mikrotik, 'backup-settings'], queryFn: api.mikrotik.backupSettings })
const setSchedule = ref(true)
const schedule = ref<BackupSchedule>(24)
const encrypt = ref<boolean | null>(null)
watch(visible, (open) => {
  if (open) {
    setSchedule.value = true
    schedule.value = 24
    encrypt.value = null
  }
})
const error = computed(() => (setSchedule.value ? scheduleError(schedule.value) : undefined))
const nothing = computed(() => !setSchedule.value && encrypt.value === null)
const encryptions = computed(() => [
  { label: 'Leave as it is', value: null },
  { label: 'Encrypt their backups', value: true, disabled: !settings.data.value?.key },
  { label: 'Do not encrypt them', value: false },
])
const { run, busy } = useActions()

async function apply() {
  const result = await run(
    () =>
      api.mikrotik.applyBackupSettings({
        sensorIds: props.routers.map((r) => r.id),
        setSchedule: setSchedule.value,
        backupIntervalHours: setSchedule.value ? (schedule.value ?? null) : null,
        encryptBackups: encrypt.value,
      }),
    {
      success: `Backup settings applied to ${props.routers.length} router(s)`,
      invalidate: [keys.mikrotik, keys.sensors, ['sensor']],
    },
  )
  if (result !== undefined) {
    visible.value = false
    emit('changed')
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Backup settings"
    modal
    :style="{ width: '36rem' }"
    :breakpoints="{ '600px': '95vw' }"
  >
    <p class="routers">
      <strong>{{ routers.length }} router(s):</strong> {{ routers.map((r) => r.name).join(', ') }}
    </p>
    <div class="switch-row">
      <ToggleSwitch v-model="setSchedule" input-id="bulk-set-schedule" />
      <label for="bulk-set-schedule">Change the schedule</label>
    </div>
    <div v-if="setSchedule" class="field">
      <label for="bulk-backup">Scheduled backups</label>
      <BackupScheduleField v-model="schedule" input-id="bulk-backup" :invalid="!!error" />
      <small v-if="error" class="field-error">{{ error }}</small>
      <small v-else-if="schedule === null" class="field-hint"
        >No scheduled export. Back up now, updates and commands still take one.</small
      >
    </div>
    <div class="field">
      <label for="bulk-encrypt">Encryption</label>
      <Select
        v-model="encrypt"
        :options="encryptions"
        option-label="label"
        option-value="value"
        option-disabled="disabled"
        input-id="bulk-encrypt"
      />
      <small class="field-hint">{{
        settings.data.value?.key
          ? `With the backup key ${settings.data.value.key.fingerprint}, from their next backup on.`
          : 'Encrypting needs the backup key (the Backups tab).'
      }}</small>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Apply" icon="pi pi-check" :loading="busy" :disabled="!!error || nothing" @click="apply" />
    </template>
  </Dialog>
</template>

<style scoped>
.routers {
  margin: 0 0 var(--pnmt-space-3);
}
.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  margin-top: var(--pnmt-space-3);
}
.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
