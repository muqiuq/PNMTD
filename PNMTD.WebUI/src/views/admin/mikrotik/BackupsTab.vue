<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import BackupScheduleField from '@/views/sensors/mikrotik/BackupScheduleField.vue'
import { scheduleError, type BackupSchedule } from '@/views/sensors/mikrotik/backupSchedule'
import BackupKeyDialog from './BackupKeyDialog.vue'

/**
 * The routers' backups for the installation (docs/mikrotik-backup-plan.md §2.2, §3.5): what new routers start with,
 * and the backup key, which encrypts the backups of the routers that ask for it, one way. Many routers at once:
 * Backup settings… on the Routers tab.
 */
const q = useQuery({ queryKey: [...keys.mikrotik, 'backup-settings'], queryFn: api.mikrotik.backupSettings })
const s = computed(() => q.data.value)
const schedule = ref<BackupSchedule>(24)
const encrypt = ref(false)
watch(
  s,
  (settings) => {
    if (settings) {
      schedule.value = settings.defaultBackupIntervalHours ?? null
      encrypt.value = settings.defaultEncryptBackups
    }
  },
  { immediate: true },
)
const error = computed(() => scheduleError(schedule.value))
const retired = computed(() => s.value?.keys?.filter((k) => k.retiredAt) ?? [])
const settingUp = ref(false)
const { done, ask, busy } = useActions()

async function save() {
  if (error.value) return
  await done(
    () =>
      api.mikrotik.updateBackupSettings({
        defaultBackupIntervalHours: schedule.value ?? null,
        defaultEncryptBackups: encrypt.value,
      }),
    { success: 'Saved', invalidate: [keys.mikrotik] },
  )
}

async function turnOff() {
  const n = s.value?.routersEncrypted ?? 0
  if (
    await ask(
      'Turn encryption off?',
      `The key is retired, and every router's backups are stored without encryption from now on${n ? `: ${n} router(s)` : ''}. Backups encrypted so far stay encrypted, and only the private key opens them.`,
      'Turn off',
    )
  ) {
    await done(() => api.mikrotik.retireBackupKey(), {
      success: 'Encryption off',
      invalidate: [keys.mikrotik, ['sensor']],
    })
  }
}
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="encryption-title">
      <div id="encryption-title" class="panel-header">
        Encryption
        <Tag v-if="s?.key" value="on" severity="success" icon="pi pi-lock" />
        <Tag v-else value="off" severity="secondary" />
      </div>
      <div v-if="s" class="panel-body">
        <template v-if="s.key">
          <dl class="facts">
            <dt>Public key</dt>
            <dd><CopyField :value="s.key.recipient" label="Public key" /></dd>
            <dt>Fingerprint</dt>
            <dd class="mono">{{ s.key.fingerprint }}</dd>
            <dt>Set</dt>
            <dd>
              {{ formatAbsolute(s.key.createdAt) }}<template v-if="s.key.createdBy"> by {{ s.key.createdBy }}</template>
            </dd>
            <dt>In use</dt>
            <dd>
              <RouterLink :to="{ name: 'mikrotik', query: { tab: 'routers' } }"
                >{{ s.routersEncrypted }} router(s)</RouterLink
              >
              encrypt their backups; {{ s.key.backups }} backup(s) so far.
            </dd>
          </dl>
          <p class="muted small">
            Read them in a router's Backups tab with Load key…, or offline with the age tool:
            <code>age -d -i pnmt-backups.age-key router-…-v12.rsc.gz.age | gunzip</code>
          </p>
          <div class="actions">
            <Button label="Replace…" icon="pi pi-refresh" size="small" severity="secondary" @click="settingUp = true" />
            <Button
              label="Turn encryption off…"
              icon="pi pi-unlock"
              size="small"
              severity="danger"
              text
              @click="turnOff"
            />
          </div>
        </template>
        <template v-else>
          <p>
            With a backup key, routers can have their backups encrypted one way: PNMT holds only the public key, so it
            stores what it cannot read, and a copy of its database reveals no configuration. Each router chooses, in its
            settings or with Backup settings… on the Routers tab.
          </p>
          <div class="actions">
            <Button label="Set up…" icon="pi pi-key" size="small" @click="settingUp = true" />
          </div>
        </template>
        <template v-if="retired.length">
          <h3 class="small-title">Earlier keys</h3>
          <ul class="retired">
            <li v-for="k in retired" :key="k.fingerprint">
              <span class="mono">{{ k.fingerprint }}</span>
              <span class="muted small">
                {{ formatAbsolute(k.createdAt) }} – {{ formatAbsolute(k.retiredAt!) }} · {{ k.backups }} backup(s)
              </span>
            </li>
          </ul>
        </template>
      </div>
    </section>

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
        <div class="switch-row wide">
          <ToggleSwitch v-model="encrypt" input-id="default-encrypt" :disabled="!s?.key" />
          <label for="default-encrypt">Encrypt their backups</label>
        </div>
        <small class="field-hint wide hint">{{
          s?.key ? 'With the backup key above.' : 'Needs the backup key.'
        }}</small>
        <div class="form-actions wide">
          <Button type="submit" label="Save" :loading="busy" :disabled="!!error" />
        </div>
      </form>
    </section>
    <BackupKeyDialog v-model:visible="settingUp" :replacing="!!s?.key" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.panel-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
.panel-body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
.facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: var(--pnmt-space-2) var(--pnmt-space-4);
  margin: 0;
}
.facts dt {
  color: var(--pnmt-muted);
}
.facts dd {
  margin: 0;
  min-width: 0;
}
.actions {
  display: flex;
  gap: var(--pnmt-space-2);
}
.retired li {
  display: flex;
  gap: var(--pnmt-space-3);
  align-items: baseline;
}
.retired {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}
.small-title {
  font-size: 0.93rem;
  margin: 0;
}
.mono {
  font-family: var(--pnmt-mono);
}
.small {
  font-size: 0.857rem;
}
</style>
