<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import {
  credentialKinds,
  credentialLines,
  kbApi,
  kbKeys,
  kbLinkIcon,
  kbNumber,
  kbStatusOptions,
  type KbLinkKind,
  type KbPage,
  type KbPageRequest,
  type KbStatus,
} from '@/api/kb'
import { api } from '@/api/resources'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import type { CredentialsWarning, EditConflict, MarkdownSave, PageLink } from '@/components/markdown/types'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'
import { formatTime } from '@/format'
import LinkPicker, { type LinkTarget } from './LinkPicker.vue'
import { normalizeTag } from './listState'

/**
 * Editing a page (docs/locations-knowledge-plan.md §5.9, §6.1): `/kb/new` and `/kb/42/edit`, on the editing page with
 * the Markdown editor (mode *page*). The title and status in the header, the details under it (summary, tags with
 * completion, links, the note for the history), folded, and open for a new page. Drafts, conflicts (409 edit_conflict
 * with their changes), the credentials warning (422 looks_like_credential) and who else is editing come with it. After
 * Save the reading view opens, with the AI question when the server's answer asks for it (§7.2).
 */
const route = useRoute()
const router = useRouter()
const notify = useNotify()
const queryClient = useQueryClient()

const number = computed(() => (route.params.number ? Number(route.params.number) : null))
const isNew = computed(() => number.value === null)
const page = ref<KbPage | null>(null)
const loadError = ref<unknown>(null)
const loaded = ref(false)

const content = ref('')
const title = ref('')
const status = ref<KbStatus>('Published')
const summary = ref('')
const tags = ref<{ name: string; ai: boolean }[]>([])
const links = ref<LinkTarget[]>([])
const note = ref('')
const detailsOpen = ref(false)
const saving = ref(false)
const dirty = ref(false)
const titleError = ref<string | null>(null)
const fieldErrors = ref<string[]>([])
const conflict = ref<EditConflict | null>(null)
const credentials = ref<CredentialsWarning | null>(null)
const editor = ref<{ saved(): void; insertMarkdown(text: string): void } | null>(null)
/** The fields as loaded (or last saved), to know whether the details have unsaved changes. */
const initial = ref('')
const snapshot = () =>
  JSON.stringify([
    title.value,
    status.value,
    summary.value,
    tags.value,
    links.value.map((l) => `${l.kind}:${l.id}`),
    note.value,
  ])

async function linkFromQuery(): Promise<LinkTarget | null> {
  const raw = typeof route.query.link === 'string' ? route.query.link : ''
  const [kind, id] = raw.split(':')
  if (!id) return null
  try {
    if (kind === 'host') {
      const h = await api.hosts.get(id)
      return { kind: 'Host', id, name: h.name, detail: h.location ?? null }
    }
    if (kind === 'sensor') {
      const s = await api.sensors.get(id)
      return { kind: 'Sensor', id, name: s.name, detail: s.host.name }
    }
    if (kind === 'location') {
      const l = await api.locations.get(id, true)
      return { kind: 'Location', id, name: l.name, detail: l.path }
    }
  } catch {
    // A link to something gone: the new page starts without it.
  }
  return null
}

onMounted(async () => {
  try {
    if (number.value !== null) {
      const p = await kbApi.get(number.value)
      if (p.archivedAt) {
        notify.info(`${kbNumber(p.number)} is archived`, 'Restore it to edit it.')
        await router.replace({ name: 'kb-page', params: { number: p.number } })
        return
      }
      page.value = p
      content.value = p.content
      title.value = p.title
      status.value = p.status
      summary.value = p.summary ?? ''
      tags.value = p.tags.map((t) => ({ name: t.name, ai: t.source === 'Ai' }))
      links.value = p.links
        .filter((l) => l.exists)
        .map((l) => ({ kind: l.kind, id: l.id, name: l.name, detail: l.detail ?? null }))
      startPresence(p.id)
    } else {
      const link = await linkFromQuery()
      if (link) links.value = [link]
      detailsOpen.value = true
    }
    initial.value = snapshot()
    loaded.value = true
  } catch (e) {
    loadError.value = e
  }
})

// --- who else is editing (§5.7) ------------------------------------------------------------------
const others = ref<{ name: string; since: string }[]>([])
let presence: ReturnType<typeof setInterval> | undefined
function startPresence(id: string) {
  const report = async () => {
    try {
      others.value = await kbApi.editing(id)
    } catch {
      // Presence blocks nothing; a failed report is only a missed line.
    }
  }
  void report()
  presence = setInterval(report, 30_000)
}
onBeforeUnmount(() => {
  clearInterval(presence)
  if (page.value) void kbApi.editing(page.value.id, true).catch(() => undefined)
})

// --- the details --------------------------------------------------------------------------------
const detailsDirty = computed(() => loaded.value && snapshot() !== initial.value)
const detailsSummary = computed(() => {
  const parts = [summary.value.trim() ? 'summary' : 'no summary']
  parts.push(`${tags.value.length} tag${tags.value.length === 1 ? '' : 's'}`)
  parts.push(`${links.value.length} link${links.value.length === 1 ? '' : 's'}`)
  if (note.value.trim()) parts.push('note')
  return parts.join(' · ')
})

const tagText = ref('')
const tagSuggestions = ref<{ name: string; pages: number }[]>([])
async function completeTags(event: { query: string }) {
  const found = await kbApi.tags(normalizeTag(event.query) || undefined)
  tagSuggestions.value = found.filter((t) => !tags.value.some((x) => x.name === t.name)).slice(0, 12)
}
function addTag(raw: string) {
  const name = normalizeTag(raw)
  tagText.value = ''
  if (!name) return
  if (!/^[\p{L}\p{N}]+(-[\p{L}\p{N}]+)*$/u.test(name) || name.length < 2 || name.length > 40) {
    notify.info(`“${raw}” is not a tag`, '2–40 letters, digits and dashes.')
    return
  }
  if (!tags.value.some((t) => t.name === name)) tags.value.push({ name, ai: false })
}
function onTagKey(event: KeyboardEvent) {
  if ((event.key === 'Enter' || event.key === ',') && typeof tagText.value === 'string' && tagText.value.trim()) {
    event.preventDefault()
    addTag(tagText.value)
  }
}
function pickLink(target: LinkTarget) {
  if (!links.value.some((l) => l.kind === target.kind && l.id === target.id)) links.value.push(target)
}

// --- Link to a page… -----------------------------------------------------------------------------
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
  const result = q ? await api.search(q, 10) : null
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

// --- saving -------------------------------------------------------------------------------------
function request(s: MarkdownSave): KbPageRequest {
  return {
    title: title.value,
    summary: summary.value.trim() || null,
    content: s.markdown,
    tags: tags.value.map((t) => t.name),
    aiTags: tags.value.filter((t) => t.ai).map((t) => t.name),
    status: status.value,
    links: links.value.map((l) => ({ kind: l.kind as KbLinkKind, id: l.id })),
    revision: typeof s.revision === 'number' ? s.revision : null,
    note: note.value.trim() || null,
    acknowledgeCredentials: s.acknowledgeCredentials,
    markSensitive: s.markSensitive,
  }
}

async function save(s: MarkdownSave) {
  if (title.value.trim().length < 3) {
    titleError.value = 'A title of 3–160 characters.'
    return
  }
  saving.value = true
  titleError.value = null
  fieldErrors.value = []
  try {
    const result = page.value ? await kbApi.update(page.value.id, request(s)) : await kbApi.create(request(s))
    editor.value?.saved()
    initial.value = snapshot()
    queryClient.setQueryData(kbKeys.page(result.page.number), result.page)
    await queryClient.invalidateQueries({ queryKey: kbKeys.all })
    for (const same of result.sameTitle) {
      notify.info(`${kbNumber(same.number)} has the same title`, same.title ?? undefined)
    }
    notify.success(`${kbNumber(result.page.number)} saved`)
    await router.push({
      name: 'kb-page',
      params: { number: result.page.number },
      query: result.askAiTags ? { ask: 'ai-tags' } : {},
    })
  } catch (e) {
    await failed(e, s)
  } finally {
    saving.value = false
  }
}

async function failed(e: unknown, s: MarkdownSave) {
  if (e instanceof ApiError && e.code === 'edit_conflict' && page.value) {
    // Theirs is the page as it is now (§5.7).
    const current = await kbApi.get(page.value.number)
    conflict.value = {
      by: typeof e.body.by === 'string' ? e.body.by : (current.updatedByName ?? 'Someone'),
      at: typeof e.body.at === 'string' ? e.body.at : current.updatedAt,
      revision: typeof e.body.revision === 'number' ? e.body.revision : current.revision,
      theirs: current.content,
      base: page.value.content,
    }
    return
  }
  const lines = credentialLines(e)
  if (lines) {
    credentials.value = { lines, kinds: credentialKinds(e) }
    return
  }
  if (e instanceof ApiError && e.code === 'validation_failed') {
    titleError.value = e.field('title') ?? null
    fieldErrors.value = Object.entries(e.errors)
      .filter(([field]) => field !== 'title')
      .flatMap(([field, messages]) => messages.map((m) => `${field}: ${m}`))
    if (fieldErrors.value.length) detailsOpen.value = true
    return
  }
  notify.error(e, s.overwrite ? 'Not saved over theirs' : 'Not saved')
}

function leave() {
  if (page.value) void router.push({ name: 'kb-page', params: { number: page.value.number } })
  else void router.push({ name: 'kb' })
}

const backLabel = computed(() => (page.value ? kbNumber(page.value.number) : 'Knowledge base'))
const draftKey = computed(() => (page.value ? `kb:${page.value.id}` : 'kb:new'))
</script>

<template>
  <ErrorState v-if="loadError" :error="loadError" what="the page" class="page" @retry="router.go(0)" />
  <div v-else-if="!loaded" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
  <MarkdownEditor
    v-else
    ref="editor"
    v-model="content"
    v-model:details-open="detailsOpen"
    v-model:conflict="conflict"
    v-model:credentials="credentials"
    mode="page"
    :base-revision="page?.revision ?? null"
    :draft-key="draftKey"
    :back-label="backLabel"
    :saving="saving"
    :files="page?.files ?? null"
    :details-summary="detailsSummary"
    :details-dirty="detailsDirty"
    :pick-page="pickPage"
    can-mark-sensitive
    @save="save"
    @cancel="leave"
    @dirty="dirty = $event"
  >
    <template #title>
      <div class="title-field">
        <InputText
          v-model="title"
          :placeholder="isNew ? 'Title of the new page' : 'Title'"
          aria-label="Title"
          :invalid="!!titleError"
          maxlength="160"
          fluid
        />
        <small v-if="titleError" class="field-error">{{ titleError }}</small>
      </div>
    </template>
    <template #status>
      <Select
        v-model="status"
        :options="kbStatusOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Status"
      />
    </template>
    <template v-if="others.length" #others>
      <span class="others" :title="others.map((o) => `${o.name} since ${formatTime(o.since)}`).join(', ')">
        <i class="pi pi-users" aria-hidden="true" />
        {{
          others.length === 1
            ? `${others[0].name} is editing too (since ${formatTime(others[0].since)})`
            : `${others.length} others are editing`
        }}
      </span>
    </template>
    <template #details>
      <div class="details">
        <Message v-for="(m, i) in fieldErrors" :key="i" severity="error" :closable="false" class="wide">{{
          m
        }}</Message>
        <div class="field wide">
          <label for="kb-summary">Summary</label>
          <Textarea
            id="kb-summary"
            v-model="summary"
            rows="2"
            maxlength="300"
            auto-resize
            placeholder="One or two sentences, shown in lists and results"
          />
        </div>
        <div class="field">
          <label for="kb-tags">Tags</label>
          <div class="chips">
            <span v-for="t in tags" :key="t.name" class="chip" :class="{ ai: t.ai }">
              <i v-if="t.ai" class="pi pi-sparkles" title="Suggested by AI: saving with it here keeps it marked" />
              #{{ t.name }}
              <button
                v-if="t.ai"
                type="button"
                class="chip-action"
                :aria-label="`Confirm ${t.name}`"
                title="Confirm"
                @click="t.ai = false"
              >
                <i class="pi pi-check" />
              </button>
              <button
                type="button"
                class="chip-action"
                :aria-label="`Remove ${t.name}`"
                @click="tags = tags.filter((x) => x !== t)"
              >
                <i class="pi pi-times" />
              </button>
            </span>
          </div>
          <AutoComplete
            v-model="tagText"
            input-id="kb-tags"
            :suggestions="tagSuggestions"
            option-label="name"
            placeholder="Add a tag…"
            size="small"
            :delay="150"
            fluid
            @complete="completeTags"
            @option-select="(e) => addTag(e.value.name)"
            @keydown="onTagKey"
          >
            <template #option="{ option }">
              <span class="tag-option"
                >#{{ option.name }} <span class="muted">{{ option.pages }}</span></span
              >
            </template>
          </AutoComplete>
        </div>
        <div class="field">
          <label>Links</label>
          <ul v-if="links.length" class="links">
            <li v-for="l in links" :key="`${l.kind}:${l.id}`">
              <i :class="kbLinkIcon[l.kind]" class="muted" aria-hidden="true" />
              <span class="ellipsis">{{ l.name }}</span>
              <span v-if="l.detail" class="muted ellipsis">{{ l.detail }}</span>
              <Button
                icon="pi pi-times"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Unlink ${l.name}`"
                @click="links = links.filter((x) => x !== l)"
              />
            </li>
          </ul>
          <LinkPicker :exclude="links" @pick="pickLink" />
        </div>
        <div class="field wide">
          <label for="kb-note">Note for the history</label>
          <InputText
            id="kb-note"
            v-model="note"
            maxlength="200"
            placeholder="What changed, like a commit message (optional)"
          />
        </div>
      </div>
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
</template>

<style scoped>
.title-field {
  display: flex;
  flex-direction: column;
  min-width: 16rem;
  width: min(36rem, 50vw);
}

.others {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  color: var(--pnmt-warn);
  font-size: 0.929rem;
  white-space: nowrap;
}

.details {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.details .wide {
  grid-column: 1 / -1;
}

@media (max-width: 799px) {
  .details {
    grid-template-columns: minmax(0, 1fr);
  }

  .title-field {
    width: 100%;
  }
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  padding: 0 0.2rem 0 0.55rem;
  font-size: 0.857rem;
}

.chip.ai {
  border-style: dashed;
}

.chip .pi-sparkles {
  font-size: 0.75rem;
  color: var(--p-primary-color);
}

.chip-action {
  border: none;
  background: none;
  padding: 0 3px;
  cursor: pointer;
  color: var(--pnmt-muted);
}

.chip-action .pi {
  font-size: 0.7rem;
}

.tag-option {
  display: inline-flex;
  gap: var(--pnmt-space-2);
}

.links {
  list-style: none;
  margin: 0;
  padding: 0;
}

.links li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}
</style>
