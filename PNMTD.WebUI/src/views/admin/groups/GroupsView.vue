<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'

/**
 * *Admin → Groups* (docs/audit-redmine-zabbix-plan.md §5.5): every group with its members and a summary of its rights.
 * A new group starts empty or as a copy of another. Only admins see this page; admins have every right, whatever their groups.
 */
const router = useRouter()
const { notify } = useActions()
const groups = useQuery({ queryKey: keys.groups, queryFn: api.groups.list })
const catalog = useQuery({ queryKey: keys.rights, queryFn: api.rights.catalog, staleTime: Infinity })
const labels = computed(() => new Map((catalog.data.value ?? []).map((a) => [a.area, a.label])))

function summary(g: S['GroupItem']) {
  const parts = (catalog.data.value ?? [])
    .filter((a) => g.rights[a.area])
    .map((a) => `${labels.value.get(a.area)} ${g.rights[a.area]}`)
  return parts.length === 0
    ? 'No rights'
    : parts.length <= 3
      ? parts.join(' · ')
      : `${parts.slice(0, 3).join(' · ')} · +${parts.length - 3}`
}

const creating = ref(false)
const draft = ref<{ name: string; description: string; copyOf: string | null }>({
  name: '',
  description: '',
  copyOf: null,
})
const error = ref<string | undefined>()
async function create() {
  error.value = draft.value.name.trim() ? undefined : 'A name is needed.'
  if (error.value) return
  const source = groups.data.value?.find((g) => g.id === draft.value.copyOf)
  try {
    const group = await api.groups.create({
      name: draft.value.name.trim(),
      description: draft.value.description.trim() || null,
      rights: source ? { ...source.rights } : {},
    })
    creating.value = false
    draft.value = { name: '', description: '', copyOf: null }
    await router.push({ name: 'group', params: { id: group.id } })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      error.value = e.field('name') ?? e.message
    } else {
      notify.error(e, 'Not created')
    }
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Groups">
      <template #actions>
        <Button label="New group" icon="pi pi-plus" size="small" @click="creating = true" />
      </template>
    </PageHeader>
    <p class="muted intro">
      A group gives its members a level in each area; a member of several has the highest of them. Admins have every
      right, whatever their groups, and alone manage users and groups.
    </p>
    <ErrorState v-if="groups.isError.value" :error="groups.error.value" what="groups" @retry="groups.refetch()" />
    <DataTable
      v-else
      :value="groups.data.value ?? []"
      :loading="groups.isLoading.value"
      size="small"
      class="panel"
      data-key="id"
      row-hover
      @row-click="(e) => router.push({ name: 'group', params: { id: e.data.id } })"
    >
      <Column header="Group">
        <template #body="{ data }">
          <RouterLink :to="{ name: 'group', params: { id: data.id } }" class="strong">{{ data.name }}</RouterLink>
          <div v-if="data.description" class="muted small">{{ data.description }}</div>
        </template>
      </Column>
      <Column header="Members" style="width: 7rem">
        <template #body="{ data }">{{ data.members }}</template>
      </Column>
      <Column header="Rights">
        <template #body="{ data }"
          ><span class="muted">{{ summary(data) }}</span></template
        >
      </Column>
      <template #empty><EmptyState title="No groups yet." /></template>
    </DataTable>

    <Dialog v-model:visible="creating" header="New group" modal :style="{ width: '28rem' }">
      <form id="new-group" class="form" novalidate @submit.prevent="create">
        <div class="field">
          <label for="ng-name">Name</label>
          <InputText id="ng-name" v-model="draft.name" autofocus :invalid="!!error" />
          <small v-if="error" class="field-error">{{ error }}</small>
        </div>
        <div class="field">
          <label for="ng-description">Description</label>
          <InputText id="ng-description" v-model="draft.description" />
        </div>
        <div class="field">
          <label for="ng-copy">Rights</label>
          <Select
            v-model="draft.copyOf"
            input-id="ng-copy"
            :options="groups.data.value ?? []"
            option-label="name"
            option-value="id"
            placeholder="None: set them on the next page"
            show-clear
          />
          <small class="field-hint">Copy another group's rights, or start with none.</small>
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="creating = false" />
        <Button type="submit" form="new-group" label="Create group" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.intro {
  margin: 0 0 var(--pnmt-space-3);
  max-width: 60rem;
}

.strong {
  font-weight: 600;
}

.small {
  font-size: var(--pnmt-font-size-sm, 0.875rem);
}
</style>
