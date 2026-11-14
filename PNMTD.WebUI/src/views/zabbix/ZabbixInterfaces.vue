<script setup lang="ts">
import type { S } from '@/api/resources'
import { availabilityLabel, interfaceIcon } from './zabbix'

/** A host's interfaces as icons (§7.6): green available, red unavailable (Zabbix's error as the tooltip), grey unknown. */
withDefaults(defineProps<{ interfaces: S['ZabbixInterfaceItem'][]; detailed?: boolean }>(), { detailed: false })
</script>

<template>
  <span v-if="!detailed" class="icons">
    <i
      v-for="(i, n) in interfaces"
      :key="n"
      :class="[interfaceIcon[i.type] ?? 'pi pi-circle', i.availability]"
      :title="`${i.type} ${i.address}:${i.port}: ${availabilityLabel[i.availability]}${i.error ? ` (${i.error})` : ''}`"
      :aria-label="`${i.type} ${availabilityLabel[i.availability]}`"
    />
  </span>
  <ul v-else class="list">
    <li v-for="(i, n) in interfaces" :key="n">
      <i :class="[interfaceIcon[i.type] ?? 'pi pi-circle', i.availability]" aria-hidden="true" />
      <span>
        <strong>{{ i.type }}</strong> <span class="mono">{{ i.address }}:{{ i.port }}</span>
        <span class="muted"> · {{ availabilityLabel[i.availability] }}</span>
        <span v-if="i.error" class="error"> · {{ i.error }}</span>
      </span>
    </li>
  </ul>
</template>

<style scoped>
.icons {
  display: inline-flex;
  gap: var(--pnmt-space-1);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.list li {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}

.Available {
  color: var(--pnmt-ok);
}

.Unavailable {
  color: var(--pnmt-fail);
}

.Unknown {
  color: var(--pnmt-muted);
}

.error {
  color: var(--pnmt-fail);
}
</style>
