<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { computed } from 'vue'
import type { S } from '@/api/resources'
import { anyAddress, networkHint } from './connectorFormat'

/**
 * A caller's networks (docs/connectors-plan.md §6.3): label and CIDR per row, validated as typed. A single address becomes /32 or
 * /128; host bits are refused with the network they belong to; from anywhere is 0.0.0.0/0 and ::/0, entered on purpose and warned of.
 */
const networks = defineModel<S['ConnectorNetworkRequest'][]>({ required: true })
defineProps<{ inside?: (string | null)[] }>()
const any = computed(() => anyAddress(networks.value))
function add() {
  networks.value = [...networks.value, { cidr: '', label: null }]
}
function remove(i: number) {
  networks.value = networks.value.filter((_, j) => j !== i)
}
</script>

<template>
  <div class="networks">
    <div v-for="(n, i) in networks" :key="i" class="row">
      <InputText
        v-model="n.cidr"
        placeholder="10.20.0.0/24 or 2001:db8::17"
        class="mono"
        :aria-label="`Network ${i + 1}`"
      />
      <InputText v-model="n.label" placeholder="Label (office, VPN)" :aria-label="`Label of network ${i + 1}`" />
      <Button icon="pi pi-times" text severity="secondary" :aria-label="`Remove network ${i + 1}`" @click="remove(i)" />
      <small v-if="networkHint(n.cidr)" class="hint">{{ networkHint(n.cidr) }}</small>
      <small v-else-if="inside?.[i]" class="hint muted">inside {{ inside[i] }}</small>
    </div>
    <Button label="Add a network" icon="pi pi-plus" size="small" text @click="add" />
    <Message v-if="any" severity="warn" :closable="false" size="small">
      Any address: the key alone lets a call in. Prefer the networks the caller really calls from.
    </Message>
  </div>
</template>

<style scoped>
.networks {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.row {
  display: grid;
  grid-template-columns: minmax(12rem, 1fr) minmax(8rem, 1fr) auto;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.hint {
  grid-column: 1 / -1;
  margin-top: -4px;
}
</style>
