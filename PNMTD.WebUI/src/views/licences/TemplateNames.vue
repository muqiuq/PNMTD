<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import { computed, ref } from 'vue'
import { licenseKeys, licensesApi, type LicenseNames } from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import RenameDialog from './RenameDialog.vue'
import { nameLabels, type NameField } from './templates'

type NameUse = LicenseNames['manufacturers'][number]

/**
 * The names in use (docs/licences-plan.md §8.6): every manufacturer, vendor or provider, cost centre and seat unit, with how many
 * items and templates use it. They are words, not lists: *Rename…* (Manage) changes one everywhere, and merges it into a name in
 * use.
 */
defineProps<{ manage: boolean }>()
const names = useQuery({ queryKey: licenseKeys.names, queryFn: licensesApi.names })
const fields: NameField[] = ['manufacturers', 'vendors', 'costCenters', 'seatUnits']
const empty: NameUse[] = []

const renaming = ref(false)
const field = ref<NameField>('manufacturers')
const picked = ref<NameUse>({ name: '', items: 0, templates: 0 })
const uses = computed(() => names.data.value?.[field.value] ?? empty)
function rename(f: NameField, n: NameUse) {
  field.value = f
  picked.value = n
  renaming.value = true
}
</script>

<template>
  <ErrorState
    v-if="names.isError.value && !names.data.value"
    :error="names.error.value"
    what="the names"
    @retry="names.refetch()"
  />
  <div v-else class="names">
    <p class="muted intro">
      Typed as words, with the names in use as suggestions. Renaming one to a name in use merges the two.
    </p>
    <div class="two-columns">
      <section v-for="f in fields" :key="f" class="panel" :aria-labelledby="`names-${f}`">
        <h3 :id="`names-${f}`" class="panel-header">{{ nameLabels[f].plural }}</h3>
        <DataTable
          :value="names.data.value?.[f] ?? empty"
          :loading="names.isLoading.value"
          size="small"
          data-key="name"
          :data-test="`names-${f}`"
        >
          <Column header="Name">
            <template #body="{ data }">{{ data.name }}</template>
          </Column>
          <Column header="Items" style="width: 5rem">
            <template #body="{ data }">{{ data.items }}</template>
          </Column>
          <Column v-if="f !== 'costCenters'" header="Templates" style="width: 6rem">
            <template #body="{ data }">{{ data.templates }}</template>
          </Column>
          <Column v-if="manage" style="width: 7rem">
            <template #body="{ data }">
              <Button label="Rename…" size="small" text :aria-label="`Rename ${data.name}`" @click="rename(f, data)" />
            </template>
          </Column>
          <template #empty>
            <EmptyState :title="`No ${nameLabels[f].plural.toLowerCase()} in use.`" />
          </template>
        </DataTable>
      </section>
    </div>
    <RenameDialog v-model:visible="renaming" :field="field" :name="picked" :uses="uses" />
  </div>
</template>

<style scoped>
.names {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
}

.panel-header {
  margin: 0;
  font-size: 1rem;
}

.panel {
  overflow: hidden;
}
</style>
