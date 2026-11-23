<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import {
  kbAiWrittenIcon,
  kbApi,
  kbKeys,
  kbLinkIcon,
  kbLinkRoute,
  kbNumber,
  kbRequest,
  kbStatusLabel,
  kbStatusOptions,
  kbStatusSeverity,
  type KbPage,
  type KbPageRequest,
  type KbReactions,
} from '@/api/kb'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import { fileIcon, fileSize } from '@/components/markdown/files'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute, formatTime } from '@/format'
import { downloadText, fileSlug } from '@/format/markdown'
import { useSession } from '@/stores/session'
import AiTagsDialog from './AiTagsDialog.vue'
import LinkPicker, { type LinkTarget } from './LinkPicker.vue'
import RedminePanel from '@/views/redmine/RedminePanel.vue'
import ResponsibilitiesPanel from '@/views/responsibilities/ResponsibilitiesPanel.vue'

/**
 * A page (docs/locations-knowledge-plan.md §6.1): its title, status, tags and a lock if sensitive; the content as read;
 * at the side its links, its related pages (mutual links, then links in the texts either way; docs/knowledge-base-extensions-plan.md
 * §7), attachments, and what PNMT knows. Users edit, archive and restore it,
 * mark it sensitive, run AI tags, set its status and links; an Admin deletes it once archived. Opening it counts a view.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run, done, notify } = useActions()

const number = computed(() => Number(route.params.number))
const query = useQuery({
  queryKey: computed(() => kbKeys.page(number.value)),
  queryFn: () => kbApi.get(number.value),
  refetchInterval: 30_000,
})
const page = computed(() => query.data.value)
const canWrite = computed(() => session.can('KnowledgeBase', 'Edit'))
const archived = computed(() => !!page.value?.archivedAt)
const writable = computed(() => canWrite.value && !archived.value)

// One view per opening (§6.6): the page's refreshes don't count.
const viewed = new Set<string>()
watch(
  () => page.value?.id,
  (id) => {
    if (id && !viewed.has(id)) {
      viewed.add(id)
      void kbApi.view(id).catch(() => undefined)
    }
  },
  { immediate: true },
)

async function store(updated: KbPage) {
  queryClient.setQueryData(kbKeys.page(updated.number), updated)
  await queryClient.invalidateQueries({ queryKey: kbKeys.all })
}

/** A change through the page's save: on a conflict the page is read again, and nothing is lost but this click. */
async function save(patch: Partial<KbPageRequest>, success: string) {
  const p = page.value!
  try {
    const saved = await kbApi.update(p.id, kbRequest(p, { acknowledgeCredentials: true, ...patch }))
    await store(saved.page)
    notify.success(success)
  } catch (e) {
    if (e instanceof ApiError && e.code === 'edit_conflict') {
      await query.refetch()
      notify.info('The page changed meanwhile', 'It has been read again; try once more.')
      return
    }
    notify.error(e, 'Not saved')
  }
}

// --- likes and uses (docs/knowledge-base-extensions-plan.md §9): every reader's, as views are ------------------------
const reactions = computed(() => page.value?.reactions)
const reacting = ref(false)
function keepReactions(r: KbReactions) {
  const p = page.value!
  queryClient.setQueryData(kbKeys.page(p.number), { ...p, reactions: r })
  // The lists' counts and orders (most liked, most used) follow.
  void queryClient.invalidateQueries({ queryKey: ['kb', 'list'] })
}
async function react(action: () => Promise<KbReactions>, success?: string) {
  reacting.value = true
  try {
    const r = await run(action, { success })
    if (r) keepReactions(r)
  } finally {
    reacting.value = false
  }
}
const toggleLike = () => react(() => kbApi.like(page.value!.id, !reactions.value!.likedByMe))
const recordUse = () => react(() => kbApi.use(page.value!.id), 'Recorded: you used it to solve a problem')
/** The person's last use while it can still be undone: the bar under the header, for its note and Undo. */
const justUsed = computed(() => (reactions.value?.myLastUse?.canUndo ? reactions.value.myLastUse : null))
const note = ref('')
watch(
  () => justUsed.value?.id,
  () => (note.value = justUsed.value?.note ?? ''),
  { immediate: true },
)
const saveNote = () =>
  react(() => kbApi.noteUse(page.value!.id, justUsed.value!.id, note.value.trim() || null), 'Note saved')
const undoUse = () => react(() => kbApi.undoUse(page.value!.id, justUsed.value!.id), 'Use undone')
const useTitle = computed(() => {
  const again = reactions.value?.canUseAgainAt
  return again
    ? `You used it already; again after ${formatTime(again)}`
    : 'Record that you just used this page to solve a problem'
})
const likesDialog = ref(false)
const likers = useQuery({
  queryKey: computed(() => ['kb', 'likes', page.value?.id]),
  queryFn: () => kbApi.likes(page.value!.id),
  enabled: likesDialog,
})
const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`

const attachments = computed(() => (page.value?.files ?? []).filter((f) => f.kind === 'Attachment'))
const aiTags = computed(() => page.value?.tags.filter((t) => t.source === 'Ai').map((t) => t.name) ?? [])
const canAi = computed(() => !!page.value?.aiTags && writable.value && !page.value?.sensitive)

function addLink(target: LinkTarget) {
  const p = page.value!
  void save(
    { links: [...(kbRequest(p).links ?? []), { kind: target.kind, id: target.id }] },
    `Linked to ${target.name}`,
  )
}
/** Links to locations, hosts and sensors; pages are related pages (docs/knowledge-base-extensions-plan.md §7). */
const objectLinks = computed(() => (page.value?.links ?? []).filter((l) => l.kind !== 'Page'))
const related = computed(() => (page.value?.links ?? []).filter((l) => l.kind === 'Page' && l.exists))
/** Links in the text, either way, without the related pages; a page linked both ways shows once. */
const inText = computed(() => {
  const p = page.value
  if (!p) return []
  const skip = new Set(related.value.map((r) => r.number))
  const rows = new Map<number, { number: number; title: string | null; to: boolean; from: boolean }>()
  for (const r of p.linksTo) {
    if (!skip.has(r.number) && !r.archived)
      rows.set(r.number, { number: r.number, title: r.title ?? null, to: true, from: false })
  }
  for (const r of p.linkedFrom) {
    if (skip.has(r.number)) continue
    const row = rows.get(r.number)
    if (row) row.from = true
    else rows.set(r.number, { number: r.number, title: r.title ?? null, to: false, from: true })
  }
  return [...rows.values()].sort((a, b) => a.number - b.number)
})
function direction(r: { to: boolean; from: boolean }) {
  return r.to && r.from
    ? { icon: 'pi pi-arrows-h', title: 'Each text links to the other' }
    : r.to
      ? { icon: 'pi pi-arrow-right', title: "This page's text links to it" }
      : { icon: 'pi pi-arrow-left', title: 'Its text links here' }
}
function removeLink(kind: string, id: string, name: string) {
  const p = page.value!
  void save({ links: (kbRequest(p).links ?? []).filter((l) => !(l.kind === kind && l.id === id)) }, `${name} unlinked`)
}
function confirmTag(tag: string) {
  void save({ aiTags: aiTags.value.filter((t) => t !== tag) }, `#${tag} confirmed`)
}
function removeTag(tag: string) {
  const p = page.value!
  void save({ tags: p.tags.map((t) => t.name).filter((t) => t !== tag) }, `#${tag} removed`)
}

const aiDialog = ref(false)
// After a save whose answer asks the AI question (§7.2): ?ask=ai-tags, once.
watch(
  () => [page.value?.id, route.query.ask] as const,
  ([id, ask]) => {
    if (id && ask === 'ai-tags') {
      if (canAi.value) aiDialog.value = true
      void router.replace({ query: { ...route.query, ask: undefined } })
    }
  },
  { immediate: true },
)
const unmarkDialog = ref(false)
const deleting = ref(false)
const typed = ref('')

async function markSensitive() {
  const p = page.value!
  const updated = await run(() => kbApi.sensitive(p.id, { sensitive: true }), {
    success: `${kbNumber(p.number)} is sensitive: never sent to AI or MCP`,
  })
  if (updated) await store(updated)
}

async function archive(on: boolean) {
  const p = page.value!
  const updated = await run(() => (on ? kbApi.archive(p.id) : kbApi.unarchive(p.id)), {
    success: on ? `${kbNumber(p.number)} archived` : `${kbNumber(p.number)} restored`,
  })
  if (updated) await store(updated)
}

async function remove() {
  const p = page.value!
  if (await done(() => kbApi.remove(p.id), { success: `${kbNumber(p.number)} deleted` })) {
    deleting.value = false
    await router.replace({ name: 'kb' })
    // Once the page is left, so its own query isn't asked again for a page that is gone.
    queryClient.removeQueries({ queryKey: kbKeys.page(p.number) })
    await queryClient.invalidateQueries({ queryKey: kbKeys.all })
  }
}
const typedOk = computed(() => {
  const t = typed.value.trim().toUpperCase()
  return !!page.value && (t === kbNumber(page.value.number) || t === String(page.value.number))
})

function download() {
  const p = page.value!
  downloadText(`${kbNumber(p.number)}-${fileSlug(p.title) || 'page'}.md`, `# ${p.title}\n\n${p.content}`)
}

const more = computed(() => {
  const p = page.value
  if (!p) return []
  return [
    ...(canAi.value
      ? [{ label: 'Find tags with AI', icon: 'pi pi-sparkles', command: () => (aiDialog.value = true) }]
      : []),
    ...(canWrite.value && !p.sensitive
      ? [{ label: 'Mark as sensitive', icon: 'pi pi-lock', command: markSensitive }]
      : []),
    ...(canWrite.value && p.sensitive
      ? [{ label: 'Unmark as sensitive…', icon: 'pi pi-lock-open', command: () => (unmarkDialog.value = true) }]
      : []),
    ...(writable.value
      ? [
          {
            label: 'Status',
            icon: 'pi pi-flag',
            items: kbStatusOptions.map((o) => ({
              label: o.label,
              icon: o.value === p.status ? 'pi pi-check' : undefined,
              disabled: o.value === p.status,
              command: () => save({ status: o.value }, `Status: ${o.label}`),
            })),
          },
        ]
      : []),
    {
      label: 'History',
      icon: 'pi pi-history',
      command: () => router.push({ name: 'kb-history', params: { number: p.number } }),
    },
    {
      label: 'Show on the knowledge map',
      icon: 'pi pi-sitemap',
      command: () => router.push({ name: 'kb-map', query: { focus: `page:${p.number}` } }),
    },
    { label: 'Download .md', icon: 'pi pi-download', command: download },
    { label: 'Print', icon: 'pi pi-print', command: () => window.print() },
    ...(canWrite.value
      ? [
          archived.value
            ? { label: 'Restore from the archive', icon: 'pi pi-replay', command: () => archive(false) }
            : { label: 'Archive', icon: 'pi pi-inbox', command: () => archive(true) },
        ]
      : []),
    ...(session.can('KnowledgeBase', 'Manage') && archived.value
      ? [
          {
            label: 'Delete…',
            icon: 'pi pi-trash',
            class: 'danger',
            command: () => ((typed.value = ''), (deleting.value = true)),
          },
        ]
      : []),
  ]
})
</script>

<template>
  <div class="page kb-page">
    <ErrorState
      v-if="query.isError.value && !page"
      :error="query.error.value"
      what="the page"
      @retry="query.refetch()"
    />
    <div v-else-if="!page" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader
        :title="page.title"
        :crumbs="[{ label: 'Knowledge base', to: { name: 'kb' } }, { label: kbNumber(page.number) }]"
      >
        <template #status>
          <Tag
            v-if="page.status !== 'Published'"
            :value="kbStatusLabel[page.status]"
            :severity="kbStatusSeverity(page.status)"
          />
          <Tag
            v-if="page.sensitive"
            icon="pi pi-lock"
            value="Sensitive"
            severity="secondary"
            title="Sensitive: never sent to AI, never shown to MCP"
          />
          <Tag
            v-if="page.writtenWithAi"
            :icon="kbAiWrittenIcon"
            value="Written with AI"
            severity="secondary"
            title="Written mostly with AI"
          />
        </template>
        <template #actions>
          <span class="reactions no-print">
            <Button
              :icon="reactions?.likedByMe ? 'pi pi-heart-fill' : 'pi pi-heart'"
              :label="String(reactions?.likes ?? 0)"
              size="small"
              severity="secondary"
              :outlined="!reactions?.likedByMe"
              :disabled="archived || reacting"
              :aria-label="reactions?.likedByMe ? 'Unlike' : 'Like'"
              :aria-pressed="reactions?.likedByMe ?? false"
              :title="reactions?.likedByMe ? 'You like it; click to undo' : 'Like it'"
              @click="toggleLike"
            />
            <Button
              icon="pi pi-check-circle"
              :label="`Used to solve a problem · ${reactions?.uses ?? 0}`"
              size="small"
              severity="secondary"
              outlined
              :disabled="archived || reacting || !!reactions?.canUseAgainAt"
              :title="useTitle"
              @click="recordUse"
            />
          </span>
          <Button
            v-if="writable"
            label="Edit"
            icon="pi pi-pencil"
            size="small"
            @click="router.push({ name: 'kb-edit', params: { number: page.number } })"
          />
          <MoreMenu :items="more" label="More actions for the page" />
        </template>
      </PageHeader>

      <Message v-if="archived" severity="secondary" class="no-print">
        Archived {{ formatAbsolute(page.archivedAt)
        }}<template v-if="page.archivedByName"> by {{ page.archivedByName }}</template
        >: read-only, and left out of lists and panels.
        <Button v-if="canWrite" label="Restore" size="small" text @click="archive(false)" />
      </Message>
      <Message v-if="page.sensitive" severity="info" icon="pi pi-lock" class="no-print">
        Sensitive: never shown to AI or MCP.
      </Message>
      <Message v-if="justUsed" severity="success" icon="pi pi-check-circle" class="no-print used-bar">
        <span class="used-line">
          <span>You used it to solve a problem at {{ formatTime(justUsed.at) }}.</span>
          <InputText
            v-model="note"
            size="small"
            maxlength="200"
            placeholder="What did it solve? (optional)"
            aria-label="What it solved"
            class="note-input"
            @keydown.enter="saveNote"
          />
          <Button
            label="Save note"
            size="small"
            text
            :disabled="reacting || (note.trim() || null) === (justUsed.note ?? null)"
            @click="saveNote"
          />
          <Button label="Undo" size="small" text severity="secondary" :disabled="reacting" @click="undoUse" />
        </span>
      </Message>
      <Message v-for="e in page.editing" :key="e.name" severity="warn" icon="pi pi-users" class="no-print">
        {{ e.name }} is editing this page (since {{ formatTime(e.since) }}).
      </Message>

      <div v-if="page.tags.length" class="tags">
        <span v-for="t in page.tags" :key="t.name" class="tag" :class="{ ai: t.source === 'Ai' }">
          <i v-if="t.source === 'Ai'" class="pi pi-sparkles" title="Suggested by AI, not yet confirmed" />
          <RouterLink :to="{ name: 'kb', query: { tags: t.name } }">#{{ t.name }}</RouterLink>
          <template v-if="t.source === 'Ai' && writable">
            <button
              type="button"
              class="tag-action no-print"
              :aria-label="`Confirm ${t.name}`"
              title="Confirm"
              @click="confirmTag(t.name)"
            >
              <i class="pi pi-check" />
            </button>
            <button
              type="button"
              class="tag-action no-print"
              :aria-label="`Remove ${t.name}`"
              title="Remove"
              @click="removeTag(t.name)"
            >
              <i class="pi pi-times" />
            </button>
          </template>
        </span>
      </div>

      <div class="layout">
        <article class="panel content">
          <div class="panel-body">
            <p v-if="page.summary" class="summary muted">{{ page.summary }}</p>
            <MarkdownView v-if="page.content.trim()" :source="page.content" :files="page.files" />
            <p v-else class="muted">This page has no content yet.</p>
          </div>
        </article>

        <aside class="side no-print">
          <section class="panel" aria-labelledby="kb-links-title">
            <div id="kb-links-title" class="panel-header">Links</div>
            <div class="panel-body">
              <p v-if="!objectLinks.length" class="muted none">Not linked to a location, host or sensor.</p>
              <ul v-else class="list">
                <li v-for="l in objectLinks" :key="`${l.kind}:${l.id}`">
                  <i :class="kbLinkIcon[l.kind]" class="muted" aria-hidden="true" />
                  <span class="link-text">
                    <RouterLink v-if="l.exists" :to="kbLinkRoute(l)">{{ l.name }}</RouterLink>
                    <span v-else class="muted">{{ l.name }}</span>
                    <span v-if="l.detail" class="muted detail">{{ l.detail }}</span>
                  </span>
                  <Button
                    v-if="writable"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Unlink ${l.name}`"
                    @click="removeLink(l.kind, l.id, l.name)"
                  />
                </li>
              </ul>
              <LinkPicker
                v-if="writable"
                class="picker"
                :kinds="['Location', 'Host', 'Sensor']"
                :exclude="page.links"
                @pick="addLink"
              />
            </div>
          </section>

          <!-- Responsibilities that link this page, only when one does (docs/responsibilities-plan.md §8). -->
          <ResponsibilitiesPanel :page-id="page.id" />

          <!-- Related pages (docs/knowledge-base-extensions-plan.md §7.1): mutual links, then links in the texts either way. -->
          <section v-if="related.length || inText.length || writable" class="panel" aria-labelledby="kb-related-title">
            <div id="kb-related-title" class="panel-header">Related pages</div>
            <div class="panel-body">
              <p v-if="!related.length" class="muted none">Not linked with another page.</p>
              <ul v-else class="list" aria-label="Related">
                <li v-for="r in related" :key="r.id" :class="{ 'row-muted': r.archived }">
                  <i class="pi pi-book muted" aria-hidden="true" />
                  <span class="link-text">
                    <RouterLink :to="kbLinkRoute(r)">{{ r.name }}</RouterLink>
                    <span class="muted detail">{{ r.detail }}<template v-if="r.archived"> · archived</template></span>
                  </span>
                  <Button
                    v-if="writable && !r.archived"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Unlink ${r.name}`"
                    @click="removeLink('Page', r.id, r.name)"
                  />
                </li>
              </ul>
              <LinkPicker
                v-if="writable"
                class="picker"
                :kinds="['Page']"
                :exclude="[...page.links, { kind: 'Page', id: page.id }]"
                @pick="addLink"
              />
              <template v-if="inText.length">
                <div class="sub-title muted">In the text</div>
                <ul class="list" aria-label="In the text">
                  <li v-for="r in inText" :key="r.number">
                    <i
                      :class="direction(r).icon"
                      class="muted"
                      :title="direction(r).title"
                      :aria-label="direction(r).title"
                    />
                    <span class="mono muted">{{ kbNumber(r.number) }}</span>
                    <RouterLink v-if="r.title" :to="{ name: 'kb-page', params: { number: r.number } }">{{
                      r.title
                    }}</RouterLink>
                    <span v-else class="muted">missing page</span>
                  </li>
                </ul>
              </template>
            </div>
          </section>

          <!-- Its tickets (docs/audit-redmine-zabbix-plan.md §6.6), next to its links. -->
          <RedminePanel
            :page-id="page.id"
            :name="`${kbNumber(page.number)} ${page.title}`"
            :path="`/kb/${page.number}`"
            own-only
          />

          <section v-if="attachments.length" class="panel" aria-labelledby="kb-files-title">
            <div id="kb-files-title" class="panel-header">Attachments</div>
            <ul class="panel-body list">
              <li v-for="f in attachments" :key="f.id">
                <i :class="fileIcon(f.name, f.contentType)" class="muted" aria-hidden="true" />
                <span class="link-text">
                  <a :href="f.url" download>{{ f.name }}</a>
                  <span class="muted detail">{{ fileSize(f.size) }}</span>
                </span>
              </li>
            </ul>
          </section>

          <section v-if="page.reactions.recentUses.length" class="panel" aria-labelledby="kb-uses-title">
            <div id="kb-uses-title" class="panel-header">Used to solve a problem</div>
            <ul class="panel-body list" aria-label="Recent uses">
              <li v-for="u in page.reactions.recentUses" :key="u.id" class="use">
                <i class="pi pi-check-circle muted" aria-hidden="true" />
                <span class="link-text">
                  <span
                    >{{ u.byName }} <span class="muted">· <RelativeTime :value="u.at" /></span
                  ></span>
                  <span v-if="u.note" class="muted detail">{{ u.note }}</span>
                </span>
              </li>
            </ul>
          </section>

          <section class="panel" aria-labelledby="kb-meta-title">
            <div id="kb-meta-title" class="panel-header">About this page</div>
            <dl class="panel-body facts">
              <dt>Number</dt>
              <dd class="mono">{{ kbNumber(page.number) }}</dd>
              <dt>Created</dt>
              <dd>
                <RelativeTime :value="page.createdAt" /><span v-if="page.createdByName" class="muted">
                  · {{ page.createdByName }}</span
                >
              </dd>
              <dt>Updated</dt>
              <dd>
                <RelativeTime :value="page.updatedAt" /><span v-if="page.updatedByName" class="muted">
                  · {{ page.updatedByName }}</span
                >
              </dd>
              <dt>Revisions</dt>
              <dd>
                <RouterLink :to="{ name: 'kb-history', params: { number: page.number } }">{{
                  page.revision
                }}</RouterLink>
              </dd>
              <dt>Views</dt>
              <dd>{{ page.views.toLocaleString() }}</dd>
              <dt>Liked by</dt>
              <dd>
                <a v-if="page.reactions.likes" href="#" @click.prevent="likesDialog = true">{{
                  plural(page.reactions.likes, 'person', 'people')
                }}</a>
                <span v-else class="muted">Nobody yet</span>
              </dd>
              <dt>Used to solve</dt>
              <dd>
                <template v-if="page.reactions.uses"
                  >{{ plural(page.reactions.uses, 'time', 'times') }} by
                  {{ plural(page.reactions.people, 'person', 'people')
                  }}<span v-if="page.reactions.lastUsedAt" class="muted">
                    · last <RelativeTime :value="page.reactions.lastUsedAt" /></span
                ></template>
                <span v-else class="muted">Not yet</span>
              </dd>
              <dt>Sensitive</dt>
              <dd class="with-action">
                <span :title="page.sensitive ? 'Never sent to AI, never shown to MCP' : undefined">{{
                  page.sensitive ? 'Yes' : 'No'
                }}</span>
                <Button
                  v-if="canWrite && !page.sensitive"
                  label="Mark…"
                  size="small"
                  text
                  class="no-print"
                  @click="markSensitive"
                />
                <Button
                  v-if="canWrite && page.sensitive"
                  label="Unmark…"
                  size="small"
                  text
                  class="no-print"
                  @click="unmarkDialog = true"
                />
              </dd>
              <dt>Written with AI</dt>
              <dd>{{ page.writtenWithAi ? 'Yes, mostly' : 'No' }}</dd>
            </dl>
          </section>
        </aside>
      </div>

      <Dialog v-model:visible="likesDialog" modal header="Liked by" :style="{ width: '24rem' }">
        <ul v-if="likers.data.value" class="list">
          <li v-for="(l, i) in likers.data.value" :key="i">
            <i class="pi pi-heart-fill muted" aria-hidden="true" />
            <span>{{ l.byName }}</span>
            <span class="muted detail"><RelativeTime :value="l.at" /></span>
          </li>
        </ul>
        <ProgressSpinner v-else style="width: 24px; height: 24px" />
      </Dialog>
      <AiTagsDialog v-model:visible="aiDialog" :page="page" @changed="(p) => store(p)" />
      <AiTagsDialog v-model:visible="unmarkDialog" :page="page" mode="unmark" @changed="(p) => store(p)" />
      <Dialog v-model:visible="deleting" modal :header="`Delete ${kbNumber(page.number)}?`" :style="{ width: '28rem' }">
        <p>
          The page, its {{ page.revision }} revisions, tags, links and views are deleted. Files no other page or
          location uses leave the disk 7 days later. This cannot be undone.
        </p>
        <div class="field">
          <label for="kb-delete-number">Type {{ kbNumber(page.number) }} to confirm</label>
          <InputText id="kb-delete-number" v-model="typed" autocomplete="off" />
        </div>
        <template #footer>
          <Button label="Cancel" text severity="secondary" @click="deleting = false" />
          <Button label="Delete" severity="danger" :disabled="!typedOk" @click="remove" />
        </template>
      </Dialog>
    </template>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 19rem;
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 999px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }
}

.side {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.summary {
  margin-top: 0;
  font-size: 1.05rem;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.tag {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 0.929rem;
}

.tag.ai {
  border: 1px dashed var(--pnmt-border);
  border-radius: 999px;
  padding: 0 0.4rem;
}

.tag .pi-sparkles {
  font-size: 0.75rem;
  color: var(--p-primary-color);
}

.tag-action {
  border: none;
  background: none;
  padding: 0 2px;
  cursor: pointer;
  color: var(--pnmt-muted);
}

.tag-action .pi {
  font-size: 0.7rem;
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.panel-body.list {
  padding: var(--pnmt-space-3);
}

.list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.link-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.detail {
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.none {
  margin: 0 0 var(--pnmt-space-2);
}

.picker {
  margin-top: var(--pnmt-space-2);
}

.sub-title {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
  font-size: 0.857rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.reactions {
  display: inline-flex;
  gap: var(--pnmt-space-2);
  margin-right: var(--pnmt-space-2);
}

.used-line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.note-input {
  min-width: 16rem;
  flex: 1;
}

.with-action {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  flex-wrap: wrap;
}

.with-action :deep(.p-button) {
  padding-block: 0;
}

@media print {
  .no-print,
  .kb-page :deep(.page-header .actions) {
    display: none !important;
  }

  .layout {
    grid-template-columns: minmax(0, 1fr);
  }

  .content {
    border: none;
  }
}
</style>
