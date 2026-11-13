<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import BackupScheduleField from '@/views/sensors/mikrotik/BackupScheduleField.vue'
import { scheduleError, scheduleLabel, type BackupSchedule } from '@/views/sensors/mikrotik/backupSchedule'

/**
 * Backup settings… for the routers chosen in the Routers list (docs/mikrotik-backup-plan.md §2.2): one schedule
 * for all of them, off included.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ routers: { id: string; name: string }[] }>()
const emit = defineEmits<{ changed: [] }>()
const schedule = ref<BackupSchedule>(24)
watch(visible, (open) => {
  if (open) schedule.value = 24
})
const error = computed(() => scheduleError(schedule.value))
const { run } = useActions()

async function apply() {
  const result = await run(
    () =>
      api.mikrotik.applyBackupSettings({
        sensorIds: props.routers.map((r) => r.id),
        setSchedule: true,
        backupIntervalHours: schedule.value ?? null,
      }),
    {
      success: `Scheduled backups: ${scheduleLabel(schedule.value).toLowerCase()} for ${props.routers.length} router(s)`,
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
    :style="{ width: '34rem' }"
    :breakpoints="{ '600px': '95vw' }"
  >
    <p class="routers">
      <strong>{{ routers.length }} router(s):</strong> {{ routers.map((r) => r.name).join(', ') }}
    </p>
    <div class="field">
      <label for="bulk-backup">Scheduled backups</label>
      <BackupScheduleField v-model="schedule" input-id="bulk-backup" :invalid="!!error" />
      <small v-if="error" class="field-error">{{ error }}</small>
      <small v-else-if="schedule === null" class="field-hint"
        >No scheduled export. Back up now, updates and commands still take one.</small
      >
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Apply" icon="pi pi-check" :disabled="!!error" @click="apply" />
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
}
</style>
