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

/**
 * A page (docs/locations-knowledge-plan.md §6.1): its title, status, tags and a lock if sensitive; the content as read;
 * at the side its links, attachments, the pages linking here, and what PNMT knows. Users edit, archive and restore it,
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
const canWrite = computed(() => session.can('User'))
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
    { label: 'Download .md', icon: 'pi pi-download', command: download },
    { label: 'Print', icon: 'pi pi-print', command: () => window.print() },
    ...(canWrite.value
      ? [
          archived.value
            ? { label: 'Restore from the archive', icon: 'pi pi-replay', command: () => archive(false) }
            : { label: 'Archive', icon: 'pi pi-inbox', command: () => archive(true) },
        ]
      : []),
    ...(session.can('Admin') && archived.value
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
          <i v-if="page.sensitive" class="pi pi-lock muted" title="Sensitive: never shown to AI or MCP" />
        </template>
        <template #actions>
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
              <p v-if="!page.links.length" class="muted none">Not linked to a location, host or sensor.</p>
              <ul v-else class="list">
                <li v-for="l in page.links" :key="`${l.kind}:${l.id}`">
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
              <LinkPicker v-if="writable" class="picker" :exclude="page.links" @pick="addLink" />
            </div>
          </section>

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

          <section v-if="page.linkedFrom.length" class="panel" aria-labelledby="kb-from-title">
            <div id="kb-from-title" class="panel-header">Linked from</div>
            <ul class="panel-body list">
              <li v-for="r in page.linkedFrom" :key="r.number">
                <span class="mono muted">{{ kbNumber(r.number) }}</span>
                <RouterLink :to="{ name: 'kb-page', params: { number: r.number } }">{{ r.title }}</RouterLink>
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
            </dl>
          </section>
        </aside>
      </div>

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

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
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
