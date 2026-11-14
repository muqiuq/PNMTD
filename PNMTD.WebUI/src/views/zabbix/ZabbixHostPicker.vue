<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { ref, watch } from 'vue'
import { api, type S } from '@/api/resources'

/**
 * A Zabbix host to attach (§7.8), from the mirror as one types: name, technical name or address. Hosts attached already
 * are greyed out, with the PNMT host they belong to.
 */
const props = defineProps<{ inputId?: string; invalid?: boolean }>()
const model = defineModel<S['ZabbixHostItem'] | null>({ default: null })
const text = ref<S['ZabbixHostItem'] | string>(model.value ?? '')
const suggestions = ref<S['ZabbixHostItem'][]>([])
watch(model, (m) => (text.value = m ?? ''))

async function complete(event: { query: string }) {
  const r = await api.zabbix.hosts({ search: event.query.trim() || undefined, pageSize: 20 })
  suggestions.value = [...r.items].sort((a, b) => Number(!!a.pnmtHost) - Number(!!b.pnmtHost))
}

function select(event: { value: S['ZabbixHostItem'] }) {
  model.value = event.value.pnmtHost ? null : event.value
}
</script>

<template>
  <AutoComplete
    v-model="text"
    :input-id="props.inputId"
    :suggestions="suggestions"
    option-label="name"
    :option-disabled="(h: S['ZabbixHostItem']) => !!h.pnmtHost"
    placeholder="A Zabbix host's name or address…"
    :invalid="invalid"
    :delay="200"
    dropdown
    fluid
    @complete="complete"
    @option-select="select"
    @clear="model = null"
  >
    <template #option="{ option }">
      <span class="option">
        <span>{{ option.name }}</span>
        <span v-if="option.technicalName !== option.name" class="muted mono">{{ option.technicalName }}</span>
        <span v-if="option.pnmtHost" class="muted">· attached to {{ option.pnmtHost.name }}</span>
        <span v-else-if="option.addresses.length" class="muted">· {{ option.addresses[0] }}</span>
      </span>
    </template>
  </AutoComplete>
</template>

<style scoped>
.option {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
  min-width: 0;
}
</style>
