<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { licenseKeys, licensesApi, typePlurals, type LicenseCategory } from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'
import CategoryDialog from './CategoryDialog.vue'
import { moneyList } from './licenceList'
import { moved, usesText } from './templates'

/**
 * The categories (docs/licences-plan.md §3.1, §8.6): in their order (the lists' and the overview's legend), each with its
 * description, its items and templates, and its running cost per year per currency. Manage adds, changes, moves and deletes them;
 * one in use is deleted with its items and templates moved to another, and the last one stays.
 */
const props = defineProps<{ manage: boolean }>()
const { run, done, ask, busy } = useActions()

const categories = useQuery({ queryKey: licenseKeys.categories, queryFn: licensesApi.categories })
const list = computed(() => categories.data.value ?? [])

const editing = ref(false)
const opened = ref<LicenseCategory | null>(null)
function open(c: LicenseCategory | null) {
  if (!props.manage) return
  opened.value = c
  editing.value = true
}

const move = (c: LicenseCategory, by: -1 | 1) =>
  run(
    () =>
      licensesApi.orderCategories(
        moved(
          list.value.map((x) => x.id),
          c.id,
          by,
        ),
      ),
    { failure: 'Not moved', invalidate: [licenseKeys.all] },
  )

// Deleting: one in use asks where its items and templates go.
const deleting = ref<LicenseCategory | null>(null)
const moveTo = ref<string | null>(null)
const moveError = ref<string | undefined>()
const others = computed(() =>
  list.value.filter((c) => c.id !== deleting.value?.id).map((c) => ({ value: c.id, label: c.name })),
)
const deleteOpen = computed({
  get: () => !!deleting.value,
  set: (open: boolean) => {
    if (!open) deleting.value = null
  },
})
async function remove(c: LicenseCategory) {
  if (c.items + c.templates > 0) {
    deleting.value = c
    moveTo.value = null
    moveError.value = undefined
    return
  }
  if (!(await ask(`Delete the category ${c.name}?`, 'Nothing is in it.'))) return
  await done(() => licensesApi.deleteCategory(c.id), {
    success: `Category ${c.name} deleted`,
    failure: 'Not deleted',
    invalidate: [licenseKeys.all],
  })
}
async function removeMoving() {
  const c = deleting.value
  if (!c) return
  if (!moveTo.value) {
    moveError.value = 'The category they move to.'
    return
  }
  const target = list.value.find((x) => x.id === moveTo.value)?.name ?? 'the other category'
  const ok = await done(
    async () => {
      try {
        await licensesApi.deleteCategory(c.id, moveTo.value ?? undefined)
      } catch (e) {
        if (e instanceof ApiError && e.field('moveTo')) moveError.value = e.field('moveTo')
        throw e
      }
    },
    {
      success: `Category ${c.name} deleted; ${usesText(c.items, c.templates)} moved to ${target}`,
      failure: 'Not deleted',
      invalidate: [licenseKeys.all],
    },
  )
  if (ok) deleting.value = null
}
</script>

<template>
  <ErrorState
    v-if="categories.isError.value && !categories.data.value"
    :error="categories.error.value"
    what="the categories"
    @retry="categories.refetch()"
  />
  <div v-else class="categories">
    <div class="toolbar">
      <p class="muted intro">
        What the money is for: the first axis of the costs. The order is the lists' and the overview's.
      </p>
      <Button
        v-if="manage"
        label="New category"
        icon="pi pi-plus"
        size="small"
        data-test="new-category"
        @click="open(null)"
      />
    </div>
    <DataTable
      :value="list"
      :loading="categories.isLoading.value"
      size="small"
      class="panel table"
      :class="{ clickable: manage }"
      data-key="id"
      :row-hover="manage"
      data-test="categories-table"
      @row-click="(e) => open(e.data)"
    >
      <Column header="Category">
        <template #body="{ data }">
          <span class="name">
            <span class="strong">{{ data.name }}</span>
            <Tag
              v-if="data.defaultFor"
              :value="`default for ${typePlurals[data.defaultFor as keyof typeof typePlurals].toLowerCase()}`"
              severity="secondary"
            />
          </span>
        </template>
      </Column>
      <Column header="Description">
        <template #body="{ data }"
          ><span class="muted">{{ data.description }}</span></template
        >
      </Column>
      <Column header="Items" style="width: 6rem">
        <template #body="{ data }">{{ data.items }}</template>
      </Column>
      <Column header="Templates" style="width: 7rem">
        <template #body="{ data }">{{ data.templates }}</template>
      </Column>
      <Column header="Per year">
        <template #body="{ data }"
          ><span class="nowrap">{{ data.perYear.length ? moneyList(data.perYear) : '–' }}</span></template
        >
      </Column>
      <Column v-if="manage" style="width: 11rem">
        <template #body="{ data, index }">
          <span class="actions">
            <Button
              icon="pi pi-arrow-up"
              text
              rounded
              size="small"
              severity="secondary"
              :disabled="index === 0 || busy"
              :aria-label="`Move ${data.name} up`"
              @click.stop="move(data, -1)"
            />
            <Button
              icon="pi pi-arrow-down"
              text
              rounded
              size="small"
              severity="secondary"
              :disabled="index === list.length - 1 || busy"
              :aria-label="`Move ${data.name} down`"
              @click.stop="move(data, 1)"
            />
            <Button
              icon="pi pi-pencil"
              text
              rounded
              size="small"
              severity="secondary"
              :aria-label="`Change ${data.name}`"
              @click.stop="open(data)"
            />
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :disabled="list.length <= 1"
              :title="list.length <= 1 ? 'The last category stays: everything is in one.' : 'Delete'"
              :aria-label="`Delete ${data.name}`"
              @click.stop="remove(data)"
            />
          </span>
        </template>
      </Column>
      <template #empty>
        <EmptyState title="No categories." />
      </template>
    </DataTable>
    <CategoryDialog v-model:visible="editing" :category="opened" :categories="list" />
    <Dialog
      v-model:visible="deleteOpen"
      :header="deleting ? `Delete the category ${deleting.name}` : 'Delete'"
      modal
      :style="{ width: '28rem' }"
    >
      <form v-if="deleting" id="category-delete" class="form" novalidate @submit.prevent="removeMoving">
        <p>
          {{ usesText(deleting.items, deleting.templates) }}
          {{ deleting.items + deleting.templates === 1 ? 'is' : 'are' }}
          in it. They move to another category first, together.
        </p>
        <div class="field">
          <label for="lcat-move">Move them to</label>
          <Select
            v-model="moveTo"
            input-id="lcat-move"
            :options="others"
            option-label="label"
            option-value="value"
            placeholder="A category"
            :invalid="!!moveError"
            fluid
          />
          <small v-if="moveError" class="field-error">{{ moveError }}</small>
        </div>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="deleting = null" />
        <Button
          type="submit"
          form="category-delete"
          label="Move and delete"
          severity="danger"
          :loading="busy"
          data-test="category-delete"
        />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.categories {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
  flex: 1 1 20rem;
}

.table {
  overflow: hidden;
}

.clickable :deep(tbody tr) {
  cursor: pointer;
}

.name {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.strong {
  font-weight: 500;
}

.actions {
  display: inline-flex;
}
</style>
