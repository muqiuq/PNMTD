<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { personLabel, responsibilitiesApi, responsibilityKeys, type RedmineCombination } from '@/api/responsibilities'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import { useActions } from '@/composables/useActions'
import CombinationDialog from './CombinationDialog.vue'

/**
 * Every Redmine combination (docs/responsibilities-plan.md §9.5): which project and tracker each responsibility lists, and who
 * handles it there. Information only: no ticket is matched or changed.
 */
defineProps<{ edit: boolean }>()
const { run, ask } = useActions()

const combinations = useQuery({
  queryKey: computed(() => responsibilityKeys.combinations({})),
  queryFn: () => responsibilitiesApi.combinations(),
})
const items = computed(() => combinations.data.value?.items ?? [])

const project = ref<number | null>(null)
const responsibility = ref<string | null>(null)
const user = ref<string | null>(null)
const unique = <T,>(values: T[], key: (v: T) => string | number) => [
  ...new Map(values.map((v) => [key(v), v])).values(),
]
const projectOptions = computed(() =>
  unique(items.value, (i) => i.projectId).map((i) => ({ id: i.projectId, label: i.projectPath })),
)
const responsibilityOptions = computed(() =>
  unique(items.value, (i) => i.responsibilityId)
    .map((i) => ({ id: i.responsibilityId, label: i.responsibilityName }))
    .sort((a, b) => a.label.localeCompare(b.label)),
)
const userOptions = computed(() =>
  unique(
    items.value.filter((i) => i.user),
    (i) => i.user!.id,
  )
    .map((i) => ({ id: i.user!.id, label: i.user!.name }))
    .sort((a, b) => a.label.localeCompare(b.label)),
)
const shown = computed(() =>
  items.value.filter(
    (i) =>
      (project.value == null || i.projectId === project.value) &&
      (responsibility.value == null || i.responsibilityId === responsibility.value) &&
      (user.value == null || i.user?.id === user.value),
  ),
)
/** A single tracker is listed under its project's every tracker: the project's name only on its first row. */
const firstOfProject = (index: number) =>
  index === 0 || shown.value[index - 1]!.projectId !== shown.value[index]!.projectId

const editing = ref(false)
const current = ref<RedmineCombination | null>(null)
function open(c: RedmineCombination | null) {
  current.value = c
  editing.value = true
}
async function remove(c: RedmineCombination) {
  const what = `${c.projectPath} · ${c.trackerId == null ? 'every tracker' : c.trackerName}`
  if (!(await ask(`Remove ${what}?`, `It is removed from ${c.responsibilityName}.`, 'Remove'))) return
  await run(() => responsibilitiesApi.removeCombination(c.responsibilityId, c.id), {
    success: `${what} removed`,
    failure: 'Not removed',
    invalidate: [responsibilityKeys.all],
  })
}
</script>

<template>
  <p class="muted intro">
    Who handles which Redmine project and tracker. A single tracker is listed under its project's "every tracker".
    Subprojects are projects of their own. Nothing changes in Redmine.
  </p>
  <div class="toolbar">
    <Select
      v-model="project"
      :options="projectOptions"
      option-label="label"
      option-value="id"
      placeholder="Every project"
      show-clear
      size="small"
      aria-label="Project"
    />
    <Select
      v-model="responsibility"
      :options="responsibilityOptions"
      option-label="label"
      option-value="id"
      placeholder="Every responsibility"
      show-clear
      size="small"
      aria-label="Responsibility"
    />
    <Select
      v-model="user"
      :options="userOptions"
      option-label="label"
      option-value="id"
      placeholder="Everyone"
      show-clear
      size="small"
      aria-label="User"
    />
    <Button
      v-if="edit"
      class="new"
      label="Add a combination"
      icon="pi pi-plus"
      size="small"
      data-test="add-combination"
      @click="open(null)"
    />
  </div>
  <ErrorState
    v-if="combinations.isError.value"
    :error="combinations.error.value"
    what="the combinations"
    @retry="combinations.refetch()"
  />
  <DataTable
    v-else
    :value="shown"
    :loading="combinations.isLoading.value"
    size="small"
    class="panel"
    data-key="id"
    :row-hover="edit"
    @row-click="(e) => edit && open(e.data)"
  >
    <Column header="Project">
      <template #body="{ data, index }">
        <span v-if="firstOfProject(index)">{{ data.projectPath }}</span>
      </template>
    </Column>
    <Column header="Tracker">
      <template #body="{ data }">
        <span :class="{ muted: data.trackerId == null }">{{
          data.trackerId == null ? 'every tracker' : data.trackerName
        }}</span>
      </template>
    </Column>
    <Column header="Responsibility">
      <template #body="{ data }">
        <RouterLink :to="{ name: 'responsibility', params: { id: data.responsibilityId } }" @click.stop>{{
          data.responsibilityName
        }}</RouterLink>
      </template>
    </Column>
    <Column header="User">
      <template #body="{ data }">
        <span v-if="!data.user" class="warn">Nobody (user removed)</span>
        <span v-else :class="{ warn: !data.user.active }">{{ personLabel(data.user) }}</span>
        <span v-if="data.user && !data.userRedmineLinked" class="muted small"> · no Redmine user linked</span>
      </template>
    </Column>
    <Column header="Note">
      <template #body="{ data }">
        <span class="muted">{{ data.note }}</span>
        <span v-if="data.warning" class="warn"
          ><i class="pi pi-exclamation-triangle" aria-hidden="true" /> {{ data.warning }}</span
        >
      </template>
    </Column>
    <Column v-if="edit" style="width: 3rem">
      <template #body="{ data }">
        <span @click.stop>
          <MoreMenu
            :items="[{ label: 'Remove', icon: 'pi pi-trash', command: () => remove(data) }]"
            :label="`More for ${data.projectPath}`"
          />
        </span>
      </template>
    </Column>
    <template #empty>
      <EmptyState
        icon="pi pi-ticket"
        title="No Redmine combinations yet."
        text="Add a project and a tracker to a responsibility, with the person who handles them."
      />
    </template>
  </DataTable>
  <CombinationDialog v-model:visible="editing" :combination="current" />
</template>

<style scoped>
.intro {
  margin: var(--pnmt-space-3) 0;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-3);
  margin-bottom: var(--pnmt-space-3);
}

.new {
  margin-left: auto;
}

.warn {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}
</style>
