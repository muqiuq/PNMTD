<script setup lang="ts">
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import type { S } from '@/api/resources'
import ZabbixHostPicker from '@/views/zabbix/ZabbixHostPicker.vue'
import { severityOptions } from '@/views/zabbix/zabbix'

/** *Add sensor → Zabbix* (docs/audit-redmine-zabbix-plan.md §7.8): the Zabbix host and the sensor's rules. */
export interface ZabbixDraft {
  host: S['ZabbixHostItem'] | null
  minSeverity: S['ZabbixSeverity'] | null
  countAcknowledged: boolean
  countSuppressed: boolean
  unavailableFails: boolean
}

defineProps<{ errors: Record<string, string | undefined> }>()
const draft = defineModel<ZabbixDraft>({ required: true })
</script>

<template>
  <div class="field">
    <label for="zf-host">Zabbix host</label>
    <ZabbixHostPicker v-model="draft.host" input-id="zf-host" :invalid="!!errors['zabbix.zabbixHostId']" />
    <small v-if="errors['zabbix.zabbixHostId']" class="field-error">{{ errors['zabbix.zabbixHostId'] }}</small>
    <small v-else class="field-hint">Hosts attached elsewhere are greyed out: a Zabbix host is attached once.</small>
  </div>
  <div class="field">
    <label for="zf-severity">Fails from</label>
    <Select
      v-model="draft.minSeverity"
      input-id="zf-severity"
      :options="severityOptions"
      option-label="label"
      option-value="value"
      placeholder="The default (Zabbix__MinSeverity)"
      show-clear
    />
  </div>
  <label class="switch"><ToggleSwitch v-model="draft.countAcknowledged" /> Acknowledged problems count</label>
  <label class="switch"
    ><ToggleSwitch v-model="draft.countSuppressed" /> Suppressed problems count (in maintenance)</label
  >
  <label class="switch"><ToggleSwitch v-model="draft.unavailableFails" /> An unavailable interface fails it</label>
</template>

<style scoped>
.switch {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
