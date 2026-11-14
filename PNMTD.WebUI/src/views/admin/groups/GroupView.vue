<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import type { Area, Level } from '@/api/rights'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RightsTable from '@/components/RightsTable.vue'
import { useActions } from '@/composables/useActions'

/**
 * A group's page (docs/audit-redmine-zabbix-plan.md §5.5): its name and description, its rights as one table (a level per
 * area), and its members. Saving applies to the members from their next request; nobody is signed out.
 */
const props = defineProps<{ id: string }>()
const router = useRouter()
const queryClient = useQueryClient()
const { run, done, ask, notify } = useActions()

const group = useQuery({
  queryKey: computed(() => [...keys.groups, props.id]),
  queryFn: () => api.groups.get(props.id),
})
const catalog = useQuery({ queryKey: keys.rights, queryFn: api.rights.catalog, staleTime: Infinity })
const users = useQuery({ queryKey: [...keys.users, 'all'], queryFn: () => api.users.list({ pageSize: 500 }) })

const form = ref<{ name: string; description: string; rights: Partial<Record<Area, Level>> }>({
  name: '',
  description: '',
  rights: {},
})
const errors = ref<Record<string, string | undefined>>({})
watch(
  () => group.data.value,
  (g) => {
    if (g) form.value = { name: g.name, description: g.description ?? '', rights: { ...g.rights } }
  },
  { immediate: true },
)
const dirty = computed(() => {
  const g = group.data.value
  return (
    !!g &&
    (g.name !== form.value.name ||
      (g.description ?? '') !== form.value.description ||
      JSON.stringify(Object.entries(g.rights).sort()) !== JSON.stringify(Object.entries(form.value.rights).sort()))
  )
})

async function save() {
  errors.value = {}
  try {
    await api.groups.update(props.id, {
      name: form.value.name.trim(),
      description: form.value.description.trim() || null,
      rights: form.value.rights,
    })
    notify.success(`${form.value.name} saved`, 'Its members have the new rights from their next request.')
    await queryClient.invalidateQueries({ queryKey: keys.groups })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      errors.value = {
        name: e.field('name') ?? (e.code === 'duplicate_key' ? e.message : undefined),
        rights: e.field('rights'),
      }
    } else {
      notify.error(e, 'Not saved')
    }
  }
}

const memberIds = computed(() => (group.data.value?.members ?? []).map((m) => m.id))
const candidates = computed(() =>
  (users.data.value?.items ?? [])
    .filter((u) => !memberIds.value.includes(u.id))
    .map((u) => ({ id: u.id, label: `${u.username} (${u.displayName})` })),
)
const adding = ref<string | null>(null)
const setMembers = (ids: string[], success: string) =>
  run(() => api.groups.members(props.id, ids), {
    success,
    failure: 'Members not saved',
    invalidate: [keys.groups, keys.users],
  })
async function add() {
  if (!adding.value) return
  const name = candidates.value.find((c) => c.id === adding.value)?.label ?? 'Member'
  if (await setMembers([...memberIds.value, adding.value], `${name} added`)) adding.value = null
}
const removeMember = (id: string, name: string) =>
  setMembers(
    memberIds.value.filter((m) => m !== id),
    `${name} removed`,
  )

async function remove() {
  const g = group.data.value!
  const members =
    g.members.length === 0
      ? 'It has no members.'
      : `Its ${g.members.length} members lose what it gave them: ${g.members.map((m) => m.name).join(', ')}.`
  if (!(await ask(`Delete ${g.name}?`, `${members} Their accounts stay.`, 'Delete group'))) return
  if (
    await done(() => api.groups.remove(props.id), {
      success: `${g.name} deleted`,
      invalidate: [keys.groups, keys.users],
    })
  ) {
    await router.push({ name: 'groups' })
  }
}
</script>

<template>
  <div class="page">
    <ErrorState v-if="group.isError.value" :error="group.error.value" what="the group" @retry="group.refetch()" />
    <div v-else-if="!group.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <PageHeader :title="group.data.value.name" :crumbs="[{ label: 'Groups', to: { name: 'groups' } }]">
        <template #actions>
          <Button label="Delete…" icon="pi pi-trash" size="small" severity="danger" text @click="remove" />
          <Button label="Save" icon="pi pi-check" size="small" :disabled="!dirty" @click="save" />
        </template>
      </PageHeader>
      <div class="columns">
        <section class="panel" aria-labelledby="rights-title">
          <div id="rights-title" class="panel-header">Rights</div>
          <div class="panel-body">
            <small v-if="errors.rights" class="field-error">{{ errors.rights }}</small>
            <RightsTable v-if="catalog.data.value" v-model="form.rights" :catalog="catalog.data.value" />
          </div>
        </section>
        <div class="side">
          <section class="panel" aria-labelledby="group-title">
            <div id="group-title" class="panel-header">Group</div>
            <form class="panel-body form" novalidate @submit.prevent="save">
              <div class="field">
                <label for="g-name">Name</label>
                <InputText id="g-name" v-model="form.name" :invalid="!!errors.name" />
                <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
              </div>
              <div class="field">
                <label for="g-description">Description</label>
                <InputText id="g-description" v-model="form.description" />
              </div>
            </form>
          </section>
          <section class="panel" aria-labelledby="members-title">
            <div id="members-title" class="panel-header">Members ({{ group.data.value.members.length }})</div>
            <div class="panel-body">
              <ul class="members">
                <li v-for="m in group.data.value.members" :key="m.id">
                  <span class="mono">{{ m.name }}</span>
                  <Button
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Remove ${m.name}`"
                    @click="removeMember(m.id, m.name)"
                  />
                </li>
                <li v-if="!group.data.value.members.length" class="muted">No members yet.</li>
              </ul>
              <div class="add">
                <Select
                  v-model="adding"
                  :options="candidates"
                  option-label="label"
                  option-value="id"
                  placeholder="Add a user…"
                  filter
                  size="small"
                  aria-label="User to add"
                />
                <Button label="Add" size="small" :disabled="!adding" @click="add" />
              </div>
            </div>
          </section>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.columns {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(16rem, 1fr);
  gap: var(--pnmt-space-4);
  align-items: start;
}

.side {
  display: grid;
  gap: var(--pnmt-space-4);
}

.members {
  list-style: none;
  margin: 0 0 var(--pnmt-space-3);
  padding: 0;
}

.members li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--pnmt-space-1) 0;
}

.add {
  display: flex;
  gap: var(--pnmt-space-2);
}

.add :deep(.p-select) {
  flex: 1;
  min-width: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6, 2rem);
}

@media (max-width: 899px) {
  .columns {
    grid-template-columns: 1fr;
  }
}
</style>
