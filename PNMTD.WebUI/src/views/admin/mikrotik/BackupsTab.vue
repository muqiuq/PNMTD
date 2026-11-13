<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import BackupScheduleField from '@/views/sensors/mikrotik/BackupScheduleField.vue'
import { scheduleError, type BackupSchedule } from '@/views/sensors/mikrotik/backupSchedule'

/**
 * The routers' backups for the installation (docs/mikrotik-backup-plan.md §2.2): the schedule new routers start
 * with. Many routers at once: Backup settings… on the Routers tab.
 */
const q = useQuery({ queryKey: [...keys.mikrotik, 'backup-settings'], queryFn: api.mikrotik.backupSettings })
const schedule = ref<BackupSchedule>(24)
watch(
  () => q.data.value,
  (s) => {
    if (s) schedule.value = s.defaultBackupIntervalHours ?? null
  },
  { immediate: true },
)
const error = computed(() => scheduleError(schedule.value))
const { done, busy } = useActions()

async function save() {
  if (error.value) return
  await done(() => api.mikrotik.updateBackupSettings({ defaultBackupIntervalHours: schedule.value ?? null }), {
    success: 'Saved',
    invalidate: [keys.mikrotik],
  })
}
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="defaults-title">
      <div id="defaults-title" class="panel-header">New routers</div>
      <form class="panel-body form-grid form-box" novalidate @submit.prevent="save">
        <div class="field">
          <label for="default-backup">Scheduled backups</label>
          <BackupScheduleField v-model="schedule" input-id="default-backup" :invalid="!!error" />
          <small v-if="error" class="field-error">{{ error }}</small>
          <small v-else class="field-hint"
            >What a new router starts with. Existing routers keep theirs: Routers → Backup settings… changes many at
            once.</small
          >
        </div>
        <div class="form-actions wide">
          <Button type="submit" label="Save" :loading="busy" :disabled="!!error" />
        </div>
      </form>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
</style>
