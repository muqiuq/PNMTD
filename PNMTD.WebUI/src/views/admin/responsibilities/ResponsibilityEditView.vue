<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Dialog from 'primevue/dialog'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { kbNumber } from '@/api/kb'
import { responsibilitiesApi, responsibilityKeys } from '@/api/responsibilities'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import type { CredentialsWarning, EditConflict, MarkdownSave, PageLink } from '@/components/markdown/types'
import { useNotify } from '@/composables/useNotify'

/**
 * A responsibility's long description on the editing page (docs/responsibilities-plan.md §9.4), as a knowledge base page's: the
 * page editor and its remembered mode, uploads, paste and drop, *Link to a page…*, drafts, the conflict's diffs from an older
 * revision, and the warning for lines that look like credentials. Back to the responsibility after *Save*.
 */
const props = defineProps<{ id: string }>()
const router = useRouter()
const queryClient = useQueryClient()
const notify = useNotify()

const responsibility = useQuery({
  queryKey: computed(() => responsibilityKeys.one(props.id)),
  queryFn: () => responsibilitiesApi.get(props.id),
})

// Loaded once: later refetches don't reset the text being edited.
const content = ref('')
const baseRevision = ref(0)
const loaded = ref(false)
watch(
  () => responsibility.data.value,
  (r) => {
    if (loaded.value || !r) return
    content.value = r.description
    baseRevision.value = r.revision
    loaded.value = true
  },
  { immediate: true },
)

const editor = ref<InstanceType<typeof MarkdownEditor> | null>(null)
const saving = ref(false)
const conflict = ref<EditConflict | null>(null)
const credentials = ref<CredentialsWarning | null>(null)

const back = () => router.push({ name: 'responsibility', params: { id: props.id } })

/** A 422's line numbers and kinds, where the error carries its body. */
function credentialsOf(error: ApiError): CredentialsWarning {
  const body = (error as unknown as { body?: Record<string, unknown> }).body ?? {}
  const lines = Array.isArray(body.lines) ? body.lines.filter((l): l is number => typeof l === 'number') : []
  const kinds = Array.isArray(body.kinds) ? body.kinds.filter((k): k is string => typeof k === 'string') : undefined
  return { lines, kinds }
}

async function save(event: MarkdownSave) {
  const name = responsibility.data.value?.name ?? 'The description'
  saving.value = true
  try {
    await responsibilitiesApi.describe(props.id, {
      content: event.markdown,
      revision: Number(event.revision ?? baseRevision.value),
      acknowledgeCredentials: event.acknowledgeCredentials,
    })
    editor.value?.saved()
    await queryClient.invalidateQueries({ queryKey: responsibilityKeys.all })
    notify.success(`${name}'s description saved`)
    await back()
  } catch (e) {
    if (e instanceof ApiError && e.code === 'edit_conflict') {
      // Their text and revision, as stored now.
      const theirs = await queryClient.fetchQuery({
        queryKey: responsibilityKeys.one(props.id),
        queryFn: () => responsibilitiesApi.get(props.id),
      })
      conflict.value = {
        by: theirs.updatedByName ?? 'Someone',
        at: theirs.updatedAt,
        revision: theirs.revision,
        theirs: theirs.description,
      }
    } else if (e instanceof ApiError && e.code === 'looks_like_credential') {
      credentials.value = credentialsOf(e)
    } else {
      notify.error(e, `${name}'s description not saved`)
    }
  } finally {
    saving.value = false
  }
}

// --- Link to a page… -----------------------------------------------------------------------------
// Through the responsibilities' own search, which needs no Monitoring: View (§9.3).
const pagePicker = ref(false)
const pageText = ref<string | { number: number; title: string }>('')
const pageSuggestions = ref<{ number: number; title: string; label: string }[]>([])
let resolvePage: ((link: PageLink | null) => void) | null = null
function pickPage(): Promise<PageLink | null> {
  pageText.value = ''
  pagePicker.value = true
  return new Promise((resolve) => (resolvePage = resolve))
}
async function completePages(event: { query: string }) {
  const q = event.query.trim()
  const result = q ? await responsibilitiesApi.targets(q) : null
  pageSuggestions.value = (result?.pages ?? []).map((p) => ({
    number: p.number,
    title: p.title,
    label: `${kbNumber(p.number)} ${p.title}`,
  }))
}
function choosePage(event: { value: { number: number; title: string } }) {
  resolvePage?.({ label: event.value.title, href: `/pnmt-admin/kb/${event.value.number}` })
  resolvePage = null
  pagePicker.value = false
}
function closePagePicker(open: boolean) {
  if (!open) {
    resolvePage?.(null)
    resolvePage = null
    pagePicker.value = false
  }
}
</script>

<template>
  <div class="responsibility-edit">
    <ErrorState
      v-if="responsibility.isError.value && !responsibility.data.value"
      :error="responsibility.error.value"
      what="the responsibility"
      @retry="responsibility.refetch()"
    />
    <div v-else-if="!loaded || !responsibility.data.value" class="center">
      <ProgressSpinner style="width: 40px; height: 40px" />
    </div>
    <MarkdownEditor
      v-else
      ref="editor"
      v-model="content"
      v-model:conflict="conflict"
      v-model:credentials="credentials"
      mode="page"
      :base-revision="baseRevision"
      :draft-key="`responsibility:${responsibility.data.value.id}`"
      :back-label="responsibility.data.value.name"
      :saving="saving"
      :files="responsibility.data.value.files"
      :pick-page="pickPage"
      :placeholder="`What ${responsibility.data.value.name} covers, what it doesn't, and how to reach its people: text, images, files, diagrams.`"
      @save="save"
      @cancel="back"
    >
      <template #title>
        <span class="title">
          <span class="muted">Responsibilities</span>
          <i class="pi pi-angle-right muted" aria-hidden="true" />
          <span class="label">{{ responsibility.data.value.name }}</span>
          <span class="muted note">description</span>
        </span>
      </template>
    </MarkdownEditor>

    <Dialog
      :visible="pagePicker"
      modal
      header="Link to a page"
      :style="{ width: '30rem' }"
      @update:visible="closePagePicker"
    >
      <AutoComplete
        v-model="pageText"
        :suggestions="pageSuggestions"
        option-label="label"
        placeholder="Search pages by number, title or #tag"
        :delay="200"
        fluid
        autofocus
        @complete="completePages"
        @option-select="choosePage"
      />
    </Dialog>
  </div>
</template>

<style scoped>
.responsibility-edit {
  display: contents;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.title {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.label {
  font-weight: 600;
  white-space: nowrap;
}

.note {
  font-size: 0.857rem;
  white-space: nowrap;
}
</style>
