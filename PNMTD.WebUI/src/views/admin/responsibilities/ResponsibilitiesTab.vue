<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  personLabel,
  responsibilitiesApi,
  responsibilityKeys,
  standsInLabel,
  type ResponsibilityItem,
} from '@/api/responsibilities'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import GlobalPanel from './GlobalPanel.vue'
import NewResponsibilityDialog from './NewResponsibilityDialog.vue'

/**
 * The list (docs/responsibilities-plan.md §9.2): the global people on top, then every responsibility by name, with a search
 * over names, descriptions and people, and *Needs attention* for those with nobody set or a person inactive.
 */
defineProps<{ edit: boolean; manage: boolean }>()
const router = useRouter()

const search = ref('')
const q = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, (value) => {
  clearTimeout(timer)
  timer = setTimeout(() => (q.value = value), 250)
})
onBeforeUnmount(() => clearTimeout(timer))
const attention = ref(false)
const query = computed(() => ({ q: q.value.trim() || undefined, attention: attention.value || undefined }))
const list = useQuery({
  queryKey: computed(() => responsibilityKeys.list(query.value)),
  queryFn: () => responsibilitiesApi.list(query.value),
  placeholderData: keepPreviousData,
})
const items = computed(() => list.data.value?.items ?? [])
const filtered = computed(() => !!query.value.q || !!query.value.attention)

const creating = ref(false)
const open = (r: ResponsibilityItem) => router.push({ name: 'responsibility', params: { id: r.id } })
</script>

<template>
  <ErrorState v-if="list.isError.value" :error="list.error.value" what="responsibilities" @retry="list.refetch()" />
  <template v-else>
    <GlobalPanel v-if="list.data.value" :global="list.data.value.global" :manage="manage" />
    <p class="muted intro">
      Responsibilities say who owns what. Alerts and Cookie's reminders still follow the notification rules and the
      availability plan.
    </p>
    <div class="toolbar">
      <IconField class="search">
        <InputIcon class="pi pi-search" />
        <InputText
          v-model="search"
          placeholder="Name, description, people…"
          aria-label="Search responsibilities"
          fluid
        />
      </IconField>
      <label class="check">
        <Checkbox v-model="attention" binary input-id="attention" />
        <span>Needs attention</span>
      </label>
      <Button
        v-if="edit"
        class="new"
        label="New responsibility"
        icon="pi pi-plus"
        size="small"
        data-test="new-responsibility"
        @click="creating = true"
      />
    </div>
    <DataTable
      :value="items"
      :loading="list.isLoading.value"
      size="small"
      class="panel"
      data-key="id"
      row-hover
      @row-click="(e) => open(e.data)"
    >
      <Column header="Name">
        <template #body="{ data }">
          <RouterLink :to="{ name: 'responsibility', params: { id: data.id } }" class="strong">{{
            data.name
          }}</RouterLink>
          <i
            v-if="data.attention"
            class="pi pi-exclamation-triangle warn"
            title="Needs attention"
            aria-label="Needs attention"
          />
        </template>
      </Column>
      <Column header="Short description">
        <template #body="{ data }"
          ><span class="muted">{{ data.summary }}</span></template
        >
      </Column>
      <Column header="Responsible">
        <template #body="{ data }">
          <span v-if="data.globalStandsIn && list.data.value" class="muted">{{
            standsInLabel(list.data.value.global)
          }}</span>
          <span v-else :class="{ muted: !data.responsible?.active }">{{ personLabel(data.responsible) }}</span>
        </template>
      </Column>
      <Column header="Deputy">
        <template #body="{ data }">
          <span v-if="!data.globalStandsIn" :class="{ muted: !data.deputy?.active }">{{
            personLabel(data.deputy)
          }}</span>
        </template>
      </Column>
      <Column header="Links" style="width: 5rem">
        <template #body="{ data }">{{ data.links }}</template>
      </Column>
      <Column v-if="list.data.value?.redmineConfigured" header="Redmine" style="width: 6rem">
        <template #body="{ data }">{{ data.combinations }}</template>
      </Column>
      <template #empty>
        <EmptyState
          v-if="filtered"
          icon="pi pi-search"
          title="No responsibility matches."
          text="Try other words, or clear Needs attention."
        />
        <EmptyState
          v-else
          icon="pi pi-briefcase"
          title="No responsibilities yet."
          text="Create one for each area of work, such as Network or Backup, and link the hosts, locations and pages it covers."
        />
      </template>
    </DataTable>
    <NewResponsibilityDialog v-model:visible="creating" />
  </template>
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

.search {
  flex: 1 1 18rem;
  max-width: 28rem;
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.new {
  margin-left: auto;
}

.warn {
  margin-left: var(--pnmt-space-2);
  color: var(--pnmt-warn);
}
</style>
