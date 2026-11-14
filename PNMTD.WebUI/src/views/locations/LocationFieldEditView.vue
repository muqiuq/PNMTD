<script setup lang="ts">
import ProgressSpinner from 'primevue/progressspinner'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import type { CredentialsWarning, EditConflict, MarkdownSave } from '@/components/markdown/types'
import { useNotify } from '@/composables/useNotify'
import { useLocationFields } from './useLocations'

/**
 * A location's Markdown field on the editing page (docs/locations-knowledge-plan.md §5.9): the location's path and the
 * field's label in the header, the editor in mode 'field', and back to the location's page after *Save*. An inherited
 * value starts the override; saving an empty text clears the location's own value, so it inherits again (§3.3). A save
 * from an older revision gets the conflict's diffs (§5.7), and lines that look like credentials the warning.
 */
const route = useRoute()
const router = useRouter()
const queryClient = useQueryClient()
const notify = useNotify()

const id = computed(() => String(route.params.id))
const key = computed(() => String(route.params.key))
const location = useQuery({
  queryKey: computed(() => keys.location(id.value)),
  queryFn: () => api.locations.get(id.value),
})
const { query: fieldsQuery, active } = useLocationFields()
const field = computed(() => active.value.find((f) => f.key === key.value) ?? null)
const value = computed(() => location.data.value?.values.find((v) => v.fieldId === field.value?.id) ?? null)

// Loaded once: later refetches of the page don't reset the text being edited.
const content = ref('')
const baseRevision = ref(0)
const loaded = ref(false)
watch(
  [() => location.data.value, field],
  ([detail, f]) => {
    if (loaded.value || !detail || !f) return
    const v = detail.values.find((x) => x.fieldId === f.id)
    content.value = typeof v?.value === 'string' ? v.value : ''
    baseRevision.value = v?.revision ?? 0
    loaded.value = true
  },
  { immediate: true },
)
const notMarkdown = computed(() => fieldsQuery.data.value && (!field.value || field.value.type !== 'Markdown'))
const notMarkdownError = computed(
  () => new Error(`${location.data.value?.name ?? 'This location'} has no Markdown field ${key.value}.`),
)

const editor = ref<InstanceType<typeof MarkdownEditor> | null>(null)
const saving = ref(false)
const conflict = ref<EditConflict | null>(null)
const credentials = ref<CredentialsWarning | null>(null)

const back = () => router.push({ name: 'location', params: { id: id.value } })

/** A 422's line numbers and kinds, where the error carries its body. */
function credentialsOf(error: ApiError): CredentialsWarning {
  const body = (error as unknown as { body?: Record<string, unknown> }).body ?? {}
  const lines = Array.isArray(body.lines) ? body.lines.filter((l): l is number => typeof l === 'number') : []
  const kinds = Array.isArray(body.kinds) ? body.kinds.filter((k): k is string => typeof k === 'string') : undefined
  return { lines, kinds }
}

async function save(event: MarkdownSave) {
  if (!field.value) return
  saving.value = true
  try {
    await api.locations.setValue(id.value, field.value.id, {
      value: event.markdown.trim() ? event.markdown : null,
      revision: Number(event.revision ?? baseRevision.value),
      acknowledgeCredentials: event.acknowledgeCredentials,
    })
    editor.value?.saved()
    await queryClient.invalidateQueries({ queryKey: keys.locations })
    notify.success(`${field.value.label} saved`)
    await back()
  } catch (e) {
    if (e instanceof ApiError && e.code === 'edit_conflict') {
      // Their text and revision, as stored now.
      const detail = await queryClient.fetchQuery({
        queryKey: keys.location(id.value),
        queryFn: () => api.locations.get(id.value),
      })
      const theirs = detail.values.find((v) => v.fieldId === field.value!.id)
      conflict.value = {
        by: theirs?.updatedByName ?? 'Someone',
        at: theirs?.updatedAt ?? new Date().toISOString(),
        revision: theirs?.revision ?? 0,
        theirs: !theirs?.inheritedFrom && typeof theirs?.value === 'string' ? theirs.value : '',
      }
    } else if (e instanceof ApiError && e.code === 'looks_like_credential') {
      credentials.value = credentialsOf(e)
    } else {
      notify.error(e, `${field.value.label} not saved`)
    }
  } finally {
    saving.value = false
  }
}

const inheritedFrom = computed<S['Ref'] | null>(() => value.value?.inheritedFrom ?? null)
</script>

<template>
  <div class="location-field-edit">
    <ErrorState
      v-if="location.isError.value && !location.data.value"
      :error="location.error.value"
      what="the location"
      @retry="location.refetch()"
    />
    <ErrorState v-else-if="notMarkdown" :error="notMarkdownError" what="the field" @retry="back()" />
    <div v-else-if="!loaded || !field || !location.data.value" class="center">
      <ProgressSpinner style="width: 40px; height: 40px" />
    </div>
    <MarkdownEditor
      v-else
      ref="editor"
      v-model="content"
      v-model:conflict="conflict"
      v-model:credentials="credentials"
      mode="field"
      :base-revision="baseRevision"
      :draft-key="`location:${location.data.value.id}:${field.id}`"
      :back-label="location.data.value.name"
      :saving="saving"
      :files="location.data.value.files"
      :placeholder="field.help ?? `Write about ${location.data.value.name}: text, images, files, diagrams.`"
      @save="save"
      @cancel="back"
    >
      <template #title>
        <span class="title">
          <span class="path muted">{{ location.data.value.path }}</span>
          <i class="pi pi-angle-right muted" aria-hidden="true" />
          <span class="label">{{ field.label }}</span>
          <i
            v-if="field.sensitive"
            v-tooltip.bottom="'Sensitive: never shown to AI or MCP'"
            class="pi pi-lock muted"
            aria-label="Sensitive"
          />
          <span v-if="inheritedFrom && !baseRevision" class="muted note">overriding {{ inheritedFrom.name }}'s</span>
        </span>
      </template>
    </MarkdownEditor>
  </div>
</template>

<style scoped>
.location-field-edit {
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

.path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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
