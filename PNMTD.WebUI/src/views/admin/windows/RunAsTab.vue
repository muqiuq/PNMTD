<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'
import { runAsLabel } from './scriptFormat'

/**
 * Run-as accounts (docs/windows-sensor-plan.md §7.4): names only. A password stored on a server is entered there, in the agent's
 * window; one typed per run is asked for at each run. PNMT never stores a password.
 */
const list = useQuery({ queryKey: [...keys.windows, 'run-as'], queryFn: api.windows.runAs })
const editing = ref(false)
const current = ref<S['WindowsRunAsItem'] | null>(null)
const form = ref<S['WindowsRunAsRequest']>({ name: '', kind: 'TypedPerRun', account: '', description: null })
const kinds: S['RunAsKind'][] = ['Service', 'Stored', 'TypedPerRun', 'Gmsa']
function edit(item: S['WindowsRunAsItem'] | null) {
  current.value = item
  form.value = item
    ? { name: item.name, kind: item.kind, account: item.account, description: item.description }
    : { name: '', kind: 'TypedPerRun', account: '', description: null }
  editing.value = true
}
const { run, ask, done, busy } = useActions()
async function save() {
  const body = { ...form.value, account: form.value.kind === 'Service' ? null : form.value.account }
  const saved = await run(
    () => (current.value ? api.windows.updateRunAs(current.value.id, body) : api.windows.createRunAs(body)),
    {
      success: 'Saved',
      invalidate: [keys.windows],
    },
  )
  if (saved) editing.value = false
}
async function remove(item: S['WindowsRunAsItem']) {
  if (!(await ask('Delete the account?', item.name))) return
  await done(() => api.windows.deleteRunAs(item.id), { success: 'Deleted', invalidate: [keys.windows] })
}
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <span class="muted small">
        Stored accounts need their password entered on each server, in the agent's window. The service runs as
        LocalSystem, as an admin, and only where the server allows "service".
      </span>
      <span class="spacer" />
      <Button label="New account" icon="pi pi-plus" size="small" @click="edit(null)" />
    </div>
    <section class="panel">
      <DataTable :value="list.data.value ?? []" size="small">
        <Column header="Name">
          <template #body="{ data }"
            ><a href="#" @click.prevent="edit(data)">{{ data.name }}</a></template
          >
        </Column>
        <Column header="Account">
          <template #body="{ data }"
            ><span class="mono">{{ data.account ?? 'LocalSystem' }}</span></template
          >
        </Column>
        <Column header="Password">
          <template #body="{ data }">{{ runAsLabel(data.kind) }}</template>
        </Column>
        <Column field="description" header="Description" />
        <Column style="width: 4rem">
          <template #body="{ data }">
            <Button icon="pi pi-trash" size="small" text severity="danger" aria-label="Delete" @click="remove(data)" />
          </template>
        </Column>
      </DataTable>
    </section>
    <Dialog
      v-model:visible="editing"
      :header="current ? `Edit ${current.name}` : 'New run-as account'"
      modal
      :style="{ width: '30rem' }"
    >
      <form class="form" @submit.prevent="save">
        <div class="field">
          <label for="runas-name">Name</label>
          <InputText id="runas-name" v-model="form.name" maxlength="200" />
        </div>
        <div class="field">
          <label for="runas-kind">Password</label>
          <Select
            v-model="form.kind"
            input-id="runas-kind"
            :options="kinds"
            :option-label="(k: S['RunAsKind']) => runAsLabel(k)"
          />
        </div>
        <div v-if="form.kind !== 'Service'" class="field">
          <label for="runas-account">Account</label>
          <InputText
            id="runas-account"
            v-model="form.account"
            :placeholder="form.kind === 'Gmsa' ? 'CORP\\svc-web$' : 'CORP\\deploy'"
          />
        </div>
        <div class="field">
          <label for="runas-description">Description</label>
          <InputText id="runas-description" v-model="form.description" maxlength="1000" />
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" text severity="secondary" @click="editing = false" />
        <Button label="Save" :loading="busy" @click="save" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.spacer {
  flex: 1;
}

.small {
  font-size: 0.857rem;
}
</style>
