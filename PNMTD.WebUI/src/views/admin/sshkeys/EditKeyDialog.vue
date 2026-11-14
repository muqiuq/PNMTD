<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import OwnerField from './OwnerField.vue'

/** Edit a registered key (docs/linux-sensor-plan.md §6.4): its name, owner, kind and note. Admin, audited. */
const item = defineModel<S['SshKeyItem'] | null>('item', { required: true })
const emit = defineEmits<{ saved: [detail: S['SshKeyDetail']] }>()
const visible = computed({ get: () => item.value !== null, set: (v) => !v && (item.value = null) })
const name = ref('')
const userId = ref<string | null>(null)
const owner = ref('')
const kind = ref<S['SshKeyKind']>('Person')
const note = ref('')
watch(
  item,
  (k) => {
    if (!k) return
    name.value = k.name
    userId.value = k.owner.userId
    owner.value = k.owner.userId ? '' : (k.owner.text ?? '')
    kind.value = k.kind
    note.value = k.note ?? ''
  },
  { immediate: true },
)
const kinds = [
  { label: 'Person', value: 'Person' },
  { label: 'Service', value: 'Service' },
]
const ready = computed(() => !!name.value.trim() && (!!userId.value || !!owner.value.trim()))
const { run, busy } = useActions()
async function save() {
  const k = item.value!
  const detail = await run(
    () =>
      api.sshKeys.update(k.id, {
        name: name.value.trim(),
        userId: userId.value,
        owner: userId.value ? null : owner.value.trim(),
        kind: kind.value,
        note: note.value.trim() || null,
      }),
    { success: 'Saved', failure: 'Not saved', invalidate: [keys.sshKeys, keys.agents] },
  )
  if (detail) {
    emit('saved', detail)
    item.value = null
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Edit ${item?.name ?? ''}`" modal :style="{ width: '32rem' }">
    <form id="edit-key" class="form" @submit.prevent="save">
      <div class="field">
        <label for="key-name">Name</label>
        <InputText id="key-name" v-model="name" maxlength="200" />
      </div>
      <OwnerField v-model:user-id="userId" v-model:owner="owner" />
      <div class="field">
        <label id="key-kind">Kind</label>
        <SelectButton
          v-model="kind"
          :options="kinds"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-labelledby="key-kind"
        />
      </div>
      <div class="field">
        <label for="key-note">Note</label>
        <InputText id="key-note" v-model="note" maxlength="2000" />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="edit-key" label="Save" :loading="busy" :disabled="!ready" />
    </template>
  </Dialog>
</template>
