<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { severityOptions } from './zabbix'

/**
 * *Attach to PNMT host…* (§7.8): a Zabbix host as a ZABBIX sensor of a PNMT host, the suggested one preselected, or of a
 * new host named as in Zabbix.
 */
const props = defineProps<{ host: S['ZabbixHostItem']; suggested?: { id: string; name: string } | null }>()
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ attached: [sensor: S['ZabbixAttached']] }>()
const { run } = useActions()

type Pick = { id: string; name: string; detail: string | null }
const mode = ref<'existing' | 'new'>('existing')
const target = ref<Pick | string>('')
const newName = ref('')
const minSeverity = ref<S['ZabbixSeverity'] | null>(null)
const suggestions = ref<Pick[]>([])
watch(
  visible,
  (open) => {
    if (!open) return
    mode.value = 'existing'
    target.value = props.suggested ? { ...props.suggested, detail: 'suggested' } : ''
    newName.value = props.host.technicalName.split('.')[0] ?? props.host.name
    minSeverity.value = null
  },
  { immediate: true },
)
async function complete(event: { query: string }) {
  const q = event.query.trim()
  suggestions.value = q
    ? (await api.search(q, 10)).hosts.map((h) => ({ id: h.id, name: h.name, detail: h.location ?? null }))
    : []
}
const ready = computed(() =>
  mode.value === 'new' ? newName.value.trim().length >= 2 : typeof target.value !== 'string',
)

async function attach() {
  const item: S['ZabbixAttachItem'] = {
    zabbixHostId: props.host.id,
    hostId: mode.value === 'existing' && typeof target.value !== 'string' ? target.value.id : null,
    newHostName: mode.value === 'new' ? newName.value.trim() : null,
    minSeverity: minSeverity.value,
  }
  const done = await run(() => api.zabbix.attach([item]), {
    success: `${props.host.name} attached`,
    failure: 'Not attached',
    invalidate: [keys.zabbix, keys.hosts],
  })
  if (done?.[0]) {
    visible.value = false
    emit('attached', done[0])
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Attach ${host.name} to a PNMT host`"
    modal
    :style="{ width: 'min(32rem, 100vw)' }"
  >
    <form id="zabbix-attach" class="form" novalidate @submit.prevent="attach">
      <SelectButton
        v-model="mode"
        :options="[
          { value: 'existing', label: 'A PNMT host' },
          { value: 'new', label: 'A new host' },
        ]"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        aria-label="Attach to"
      />
      <div v-if="mode === 'existing'" class="field">
        <label for="za-host">PNMT host</label>
        <AutoComplete
          v-model="target"
          input-id="za-host"
          :suggestions="suggestions"
          option-label="name"
          placeholder="A host's name…"
          :delay="200"
          fluid
          @complete="complete"
        >
          <template #option="{ option }">
            {{ option.name }} <span v-if="option.detail" class="muted">· {{ option.detail }}</span>
          </template>
        </AutoComplete>
      </div>
      <div v-else class="field">
        <label for="za-new">Name of the new host</label>
        <InputText id="za-new" v-model="newName" maxlength="64" />
      </div>
      <div class="field">
        <label for="za-severity">Fails from</label>
        <Select
          v-model="minSeverity"
          input-id="za-severity"
          :options="severityOptions"
          option-label="label"
          option-value="value"
          placeholder="The default (Zabbix__MinSeverity)"
          show-clear
        />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="zabbix-attach" label="Attach" :disabled="!ready" />
    </template>
  </Dialog>
</template>
