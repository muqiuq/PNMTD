<script setup lang="ts">
import Button from 'primevue/button'
import { useQueryClient } from '@tanstack/vue-query'
import { computed, nextTick, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import type { MarkdownFile } from '@/components/markdown/types'
import { useNotify } from '@/composables/useNotify'
import FieldValueEditor from './FieldValueEditor.vue'
import { formatValue, hostId, isEmpty, isShort, valueHref, type FieldDefinition } from './fieldValues'

/**
 * A field of a location as a card (docs/locations-knowledge-plan.md §3.4): Markdown rendered, the other types formatted (a
 * link opens, an e-mail is mailto:, a host links to its page, a user shows their name). An inherited value says where it
 * comes from; *Override* gives the location its own, *Clear* goes back to the ancestor's. Users edit short fields in
 * place and Markdown on the editing page (§5.9). Empty fields say *Not filled in*; the page hides them from Viewers.
 * <code>preview</code> shows a value as it will look, without links or editing (§10.5).
 */
const props = withDefaults(
  defineProps<{
    field: FieldDefinition
    value?: S['LocationValueItem'] | null
    locationId?: string | null
    canWrite?: boolean
    users?: readonly S['Ref'][] | null
    files?: readonly MarkdownFile[] | null
    preview?: boolean
  }>(),
  { value: null, locationId: null, canWrite: false, users: null, files: null, preview: false },
)
const emit = defineEmits<{ saved: [value: S['LocationValueItem']] }>()

const queryClient = useQueryClient()
const notify = useNotify()
const router = useRouter()

const shown = computed(() => props.value?.value ?? null)
const empty = computed(() => isEmpty(shown.value))
const inherited = computed(() => props.value?.inheritedFrom ?? null)
const ownValue = computed(() => !inherited.value && !empty.value)
const text = computed(() => formatValue(props.field, shown.value, props.value?.target))
const href = computed(() => (props.preview ? null : valueHref(props.field, shown.value)))
const host = computed(() => (props.preview ? null : hostId(props.field, shown.value)))
// Markdown is edited on the editing page (§5.9), once that route exists.
const editable = computed(
  () =>
    props.canWrite &&
    !props.preview &&
    props.locationId !== null &&
    (isShort(props.field.type) || router.hasRoute('location-field-edit')),
)
const editRoute = computed(() => ({
  name: 'location-field-edit',
  params: { id: props.locationId!, key: props.field.key },
}))

const editing = ref(false)
const draft = ref<unknown>(null)
const saving = ref(false)
const error = ref<string | null>(null)
// The credentials warning (§5.7): it doesn't block, *Save anyway* goes through.
const warned = ref(false)
const card = ref<HTMLElement | null>(null)

async function begin(from: unknown = shown.value) {
  draft.value = from === undefined ? null : structuredClone(from)
  error.value = null
  warned.value = false
  editing.value = true
  await nextTick()
  card.value?.querySelector<HTMLElement>('.field-value-editor input, .field-value-editor [tabindex="0"]')?.focus()
}

async function store(value: unknown, acknowledgeCredentials = false) {
  saving.value = true
  error.value = null
  warned.value = false
  try {
    const saved = await api.locations.setValue(props.locationId!, props.field.id, {
      value: value as S['LocationValueRequest']['value'],
      revision: props.value?.revision ?? 0,
      acknowledgeCredentials,
    })
    editing.value = false
    emit('saved', saved)
    await queryClient.invalidateQueries({ queryKey: keys.locations })
  } catch (e) {
    if (e instanceof ApiError && e.code === 'edit_conflict') {
      error.value = `${e.message} Their value is shown now; yours is still here to save again.`
      await queryClient.invalidateQueries({ queryKey: keys.location(props.locationId!) })
    } else if (e instanceof ApiError && e.code === 'looks_like_credential') {
      error.value = 'This looks like a password, a key or a token. PNMT is not a password manager.'
      warned.value = true
    } else if (e instanceof ApiError && e.field('value')) {
      error.value = e.field('value')!
    } else {
      notify.error(e, `${props.field.label} not saved`)
    }
  } finally {
    saving.value = false
  }
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    editing.value = false
  } else if (
    event.key === 'Enter' &&
    !(event.target as HTMLElement | null)?.closest('button, .p-multiselect, .p-select')
  ) {
    event.preventDefault()
    void store(draft.value)
  }
}
</script>

<template>
  <section ref="card" class="field-card" :class="{ empty: empty && !editing }" :aria-label="field.label">
    <header class="card-header">
      <span class="label">{{ field.label }}</span>
      <i
        v-if="field.sensitive"
        v-tooltip.top="'Sensitive: never shown to AI or MCP'"
        class="pi pi-lock muted"
        aria-label="Sensitive"
      />
      <span class="spacer" />
      <template v-if="editable && !editing">
        <RouterLink
          v-if="!isShort(field.type)"
          v-tooltip.top="inherited ? 'Override' : 'Edit'"
          :to="editRoute"
          class="icon-link"
          :aria-label="`${inherited ? 'Override' : 'Edit'} ${field.label}`"
          ><i class="pi pi-pencil" aria-hidden="true"
        /></RouterLink>
        <Button
          v-else
          v-tooltip.top="inherited ? 'Override' : 'Edit'"
          icon="pi pi-pencil"
          text
          rounded
          size="small"
          severity="secondary"
          :aria-label="`${inherited ? 'Override' : 'Edit'} ${field.label}`"
          @click="begin()"
        />
      </template>
    </header>

    <div v-if="editing" class="edit" @keydown="onKey">
      <FieldValueEditor v-model="draft" :field="field" :users="users" :target="value?.target" :disabled="saving" />
      <span class="actions">
        <Button
          icon="pi pi-check"
          size="small"
          :loading="saving"
          :aria-label="`Save ${field.label}`"
          @click="store(draft)"
        />
        <Button
          icon="pi pi-times"
          size="small"
          severity="secondary"
          text
          :disabled="saving"
          :aria-label="`Discard ${field.label}`"
          @click="editing = false"
        />
      </span>
    </div>
    <template v-else>
      <div v-if="empty" class="muted not-filled">
        Not filled in
        <template v-if="editable">
          ·
          <RouterLink v-if="!isShort(field.type)" :to="editRoute">Fill in</RouterLink>
          <button v-else type="button" class="link-button" @click="begin(null)">Fill in</button>
        </template>
      </div>
      <MarkdownView v-else-if="field.type === 'Markdown'" :source="String(shown)" :files="files" class="markdown" />
      <div v-else class="value">
        <a
          v-if="href"
          :href="href"
          :target="field.type === 'Link' ? '_blank' : undefined"
          rel="noopener noreferrer nofollow"
          >{{ text }}</a
        >
        <RouterLink v-else-if="host && value?.target" :to="{ name: 'host', params: { id: host } }">{{
          text
        }}</RouterLink>
        <span v-else>{{ text }}</span>
      </div>
    </template>
    <small v-if="editing && field.help" class="muted help">{{ field.help }}</small>
    <div v-if="error" class="field-error" role="alert">
      {{ error }}
      <button v-if="warned && editing" type="button" class="link-button" :disabled="saving" @click="store(draft, true)">
        Save anyway
      </button>
    </div>

    <footer v-if="!editing && (inherited || (editable && ownValue && field.inherited))" class="card-footer muted">
      <template v-if="inherited">
        <span>
          <i class="pi pi-arrow-down-left" aria-hidden="true" />
          from
          <RouterLink v-if="!preview" :to="{ name: 'location', params: { id: inherited.id } }">{{
            inherited.name
          }}</RouterLink>
          <span v-else>{{ inherited.name }}</span>
        </span>
        <template v-if="editable">
          <RouterLink v-if="!isShort(field.type)" :to="editRoute" class="footer-action">Override</RouterLink>
          <button v-else type="button" class="link-button footer-action" @click="begin()">Override</button>
        </template>
      </template>
      <button v-else type="button" class="link-button" :disabled="saving" @click="store(null)">
        Clear, to inherit again
      </button>
    </footer>
  </section>
</template>

<style scoped>
.field-card {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
  min-width: 0;
}

.field-card:last-child {
  border-bottom: none;
}

.card-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 28px;
}

.label {
  font-weight: 600;
}

.spacer {
  flex: 1;
}

.icon-link {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2rem;
  height: 2rem;
  border-radius: 50%;
  color: var(--pnmt-muted);
}

.icon-link:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.edit {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-1);
}

.actions {
  display: inline-flex;
  flex: none;
  gap: var(--pnmt-space-1);
}

.value {
  overflow-wrap: anywhere;
}

.markdown {
  min-width: 0;
}

.not-filled {
  font-style: italic;
}

.help {
  font-size: 0.857rem;
}

.card-footer {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  font-size: 0.857rem;
}

.card-footer i {
  font-size: 0.75rem;
}

.footer-action {
  margin-left: auto;
}

.link-button {
  all: unset;
  color: var(--p-primary-color);
  cursor: pointer;
}

.link-button:hover {
  text-decoration: underline;
}

.link-button:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>
