<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { computed, ref } from 'vue'
import { kbApi, kbKeys, type KbTag } from '@/api/kb'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import FilterBar from '@/components/FilterBar.vue'
import { useActions } from '@/composables/useActions'
import { normalizeTag } from '@/views/kb/listState'

/**
 * The tags (§6.4): each with its pages. Renaming to an existing tag's name merges the two; deleting removes the tag from
 * every page, as a revision of each, and goes to the auth log.
 */
const { run, done, ask, notify } = useActions()
const search = ref('')
const tags = useQuery({ queryKey: kbKeys.tags, queryFn: () => kbApi.tags() })
const shown = computed(() => {
  const q = search.value.trim().replace(/^#/, '').toLowerCase()
  return (tags.data.value ?? []).filter((t) => !q || t.name.includes(q))
})

const renaming = ref<KbTag | null>(null)
const name = ref('')
const target = computed(() => {
  const n = normalizeTag(name.value)
  return (tags.data.value ?? []).find((t) => t.name === n && t.id !== renaming.value?.id) ?? null
})
function startRename(tag: KbTag) {
  renaming.value = tag
  name.value = tag.name
}
async function rename() {
  const tag = renaming.value!
  const result = await run(() => kbApi.renameTag(tag.id, name.value), { invalidate: [kbKeys.all] })
  if (result) {
    renaming.value = null
    notify.success(
      result.merged ? `#${tag.name} merged into #${result.tag.name}` : `#${tag.name} is now #${result.tag.name}`,
    )
  }
}

async function remove(tag: KbTag) {
  if (
    await ask(
      `Delete #${tag.name}?`,
      tag.pages
        ? `It is removed from ${tag.pages} page${tag.pages === 1 ? '' : 's'}, as a revision of each.`
        : 'No page uses it.',
    )
  ) {
    await done(() => kbApi.removeTag(tag.id), { success: `#${tag.name} deleted`, invalidate: [kbKeys.all] })
  }
}
</script>

<template>
  <FilterBar v-model:search="search" placeholder="Search tags" />
  <ErrorState v-if="tags.isError.value" :error="tags.error.value" what="the tags" @retry="tags.refetch()" />
  <section v-else class="panel">
    <EmptyState v-if="tags.isSuccess.value && !shown.length" icon="pi pi-tags" title="No tags." />
    <DataTable v-else :value="shown" size="small" data-key="id" :loading="tags.isPending.value" paginator :rows="100">
      <Column field="name" header="Tag" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'kb', query: { tags: data.name } }">#{{ data.name }}</RouterLink>
        </template>
      </Column>
      <Column field="pages" header="Pages" sortable style="width: 7rem" />
      <Column header="" style="width: 7rem">
        <template #body="{ data }">
          <span class="actions">
            <Button
              icon="pi pi-pencil"
              text
              rounded
              size="small"
              :aria-label="`Rename ${data.name}`"
              @click="startRename(data)"
            />
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :aria-label="`Delete ${data.name}`"
              @click="remove(data)"
            />
          </span>
        </template>
      </Column>
    </DataTable>
  </section>

  <Dialog
    :visible="renaming !== null"
    modal
    :header="`Rename #${renaming?.name}`"
    :style="{ width: '26rem' }"
    @update:visible="(v: boolean) => !v && (renaming = null)"
  >
    <div class="field">
      <label for="kb-tag-name">New name</label>
      <InputText id="kb-tag-name" v-model="name" autocomplete="off" @keydown.enter="rename" />
      <small class="field-hint">2–40 letters, digits and dashes, in lower case.</small>
      <small v-if="target" class="field-hint">
        #{{ target.name }} exists: the two are merged, and its {{ target.pages }} page{{
          target.pages === 1 ? '' : 's'
        }}
        keep it.
      </small>
    </div>
    <template #footer>
      <Button label="Cancel" text severity="secondary" @click="renaming = null" />
      <Button
        :label="target ? 'Merge' : 'Rename'"
        :disabled="!normalizeTag(name) || normalizeTag(name) === renaming?.name"
        @click="rename"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.actions {
  display: inline-flex;
  gap: var(--pnmt-space-1);
}
</style>
