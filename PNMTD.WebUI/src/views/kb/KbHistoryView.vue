<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import RadioButton from 'primevue/radiobutton'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { kbApi, kbChangeLabel, kbKeys, kbNumber, kbStatusLabel, type KbChange } from '@/api/kb'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import MonacoDiff from '@/components/markdown/MonacoDiff.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'

/**
 * A page's history (docs/locations-knowledge-plan.md §6.3): every revision, newest first, with who, when, the note and
 * what changed. Any two compare (?from&to; by default the newest against the one before), the meta changes above the
 * content's diff in Monaco's diff editor (an HTML table where it doesn't run). A revision can be read as it was and restored (Users); an Admin redacts a leaked text from all of them.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run, ask, notify } = useActions()

const number = computed(() => Number(route.params.number))
const pageQuery = useQuery({
  queryKey: computed(() => kbKeys.page(number.value)),
  queryFn: () => kbApi.get(number.value),
})
const page = computed(() => pageQuery.data.value)
const id = computed(() => page.value?.id)
const revisions = useQuery({
  queryKey: computed(() => kbKeys.revisions(id.value ?? '')),
  queryFn: () => kbApi.revisions(id.value!),
  enabled: computed(() => !!id.value),
})
const list = computed(() => revisions.data.value ?? [])

function param(name: string): number | undefined {
  const v = Number(route.query[name])
  return Number.isInteger(v) && v >= 0 ? v : undefined
}
const to = computed(() => param('to') ?? list.value[0]?.number ?? 0)
const from = computed(() => param('from') ?? Math.max(0, to.value - 1))
function pick(which: 'from' | 'to', value: number) {
  const next = { from: from.value, to: to.value, [which]: value }
  if (next.from > next.to) [next.from, next.to] = [next.to, next.from]
  void router.replace({ query: { from: String(next.from), to: String(next.to) } })
}

const diff = useQuery({
  queryKey: computed(() => [...kbKeys.revisions(id.value ?? ''), 'diff', from.value, to.value]),
  queryFn: () => kbApi.diff(id.value!, from.value, to.value),
  enabled: computed(() => !!id.value && to.value > 0),
})

function changeText(c: KbChange) {
  return c.text ? `${kbChangeLabel[c.kind] ?? c.kind} ${c.text}` : (kbChangeLabel[c.kind] ?? c.kind)
}

// Reading a revision as it was.
const shown = ref<number | null>(null)
const revision = useQuery({
  queryKey: computed(() => [...kbKeys.revisions(id.value ?? ''), 'revision', shown.value]),
  queryFn: () => kbApi.revision(id.value!, shown.value!),
  enabled: computed(() => !!id.value && shown.value !== null),
})

async function restore(n: number) {
  const p = page.value!
  if (
    !(await ask(
      `Restore revision ${n}?`,
      `A new revision takes revision ${n}'s title, summary, content and tags. The status and links stay as they are; nothing is lost from the history.`,
      'Restore',
    ))
  ) {
    return
  }
  const updated = await run(() => kbApi.restoreRevision(p.id, n), {
    success: `Revision ${n} restored`,
    invalidate: [kbKeys.all],
  })
  if (updated) {
    queryClient.setQueryData(kbKeys.page(updated.number), updated)
    void router.replace({ query: {} })
  }
}

// Redacting (Admins): the exact text, from every revision.
const redacting = ref(false)
const secret = ref('')
async function redact() {
  const p = page.value!
  const result = await run(() => kbApi.redact(p.id, secret.value), { invalidate: [kbKeys.all] })
  if (result) {
    redacting.value = false
    secret.value = ''
    queryClient.setQueryData(kbKeys.page(result.page.number), result.page)
    void router.replace({ query: {} })
    const requests = result.aiRequests
      ? ` and ${result.aiRequests} AI request${result.aiRequests === 1 ? '' : 's'}`
      : ''
    notify.success(
      `Redacted: ${result.occurrences} time${result.occurrences === 1 ? '' : 's'} in ${result.revisions} revision${result.revisions === 1 ? '' : 's'}${requests}`,
    )
  }
}
const writable = computed(() => session.can('KnowledgeBase', 'Edit') && !page.value?.archivedAt)
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="pageQuery.isError.value && !page"
      :error="pageQuery.error.value"
      what="the page"
      @retry="pageQuery.refetch()"
    />
    <div v-else-if="!page" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader
        title="History"
        :crumbs="[
          { label: 'Knowledge base', to: { name: 'kb' } },
          { label: `${kbNumber(page.number)} ${page.title}`, to: { name: 'kb-page', params: { number: page.number } } },
        ]"
      >
        <template #status>
          <span class="muted">{{ list.length }} revisions</span>
        </template>
        <template #actions>
          <Button
            v-if="session.can('KnowledgeBase', 'Manage')"
            label="Redact a text…"
            icon="pi pi-eraser"
            size="small"
            severity="secondary"
            outlined
            @click="redacting = true"
          />
        </template>
      </PageHeader>

      <div class="layout">
        <section class="panel" aria-labelledby="kb-revisions-title">
          <div id="kb-revisions-title" class="panel-header">
            Revisions
            <span class="muted pick-hint">From · To</span>
          </div>
          <ErrorState
            v-if="revisions.isError.value"
            :error="revisions.error.value"
            what="the revisions"
            @retry="revisions.refetch()"
          />
          <ul v-else class="revisions">
            <li v-for="r in list" :key="r.number" :class="{ selected: r.number === from || r.number === to }">
              <div class="pickers">
                <RadioButton
                  :model-value="from"
                  :value="r.number"
                  :input-id="`from-${r.number}`"
                  :aria-label="`Compare from revision ${r.number}`"
                  @update:model-value="(v: number) => pick('from', v)"
                />
                <RadioButton
                  :model-value="to"
                  :value="r.number"
                  :input-id="`to-${r.number}`"
                  :aria-label="`Compare to revision ${r.number}`"
                  @update:model-value="(v: number) => pick('to', v)"
                />
              </div>
              <div class="body">
                <div class="line">
                  <span class="mono strong">#{{ r.number }}</span>
                  <span :title="formatAbsolute(r.at)"><RelativeTime :value="r.at" /></span>
                  <span class="muted">{{ r.byName }}</span>
                  <span v-if="r.added || r.removed" class="counts">
                    <span class="added">+{{ r.added }}</span> <span class="removed">−{{ r.removed }}</span>
                  </span>
                  <Tag
                    v-if="r.declaration"
                    value="Statements"
                    severity="info"
                    class="small"
                    :title="`${r.declaration.byName} confirmed: ${r.declaration.statements.join(', ')} (revision ${r.declaration.revision})`"
                  />
                </div>
                <div v-if="r.note" class="note">{{ r.note }}</div>
                <div class="changes muted">{{ r.changes.map(changeText).join(' · ') }}</div>
                <div class="actions">
                  <Button label="View" text size="small" @click="shown = r.number" />
                  <Button
                    v-if="writable && r.number !== page.revision"
                    label="Restore…"
                    text
                    size="small"
                    @click="restore(r.number)"
                  />
                </div>
              </div>
            </li>
          </ul>
        </section>

        <section class="panel compare" aria-labelledby="kb-compare-title">
          <div id="kb-compare-title" class="panel-header">
            {{ from === 0 ? `Revision ${to}, as created` : `Revision ${from} → ${to}` }}
          </div>
          <div class="panel-body">
            <div v-if="!diff.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
            <template v-else>
              <ul v-if="diff.data.value.changes.length" class="meta">
                <li v-for="(c, i) in diff.data.value.changes" :key="i">{{ changeText(c) }}</li>
              </ul>
              <p class="muted counts-line">
                Content: <span class="added">+{{ diff.data.value.added }}</span>
                <span class="removed">−{{ diff.data.value.removed }}</span> lines
              </p>
              <MonacoDiff
                :key="`${from}-${to}`"
                :original="diff.data.value.fromContent"
                :modified="diff.data.value.toContent"
                :original-label="from === 0 ? 'Nothing' : `Revision ${from}`"
                :modified-label="`Revision ${to}`"
                height="32rem"
              />
            </template>
          </div>
        </section>
      </div>

      <Dialog
        :visible="shown !== null"
        modal
        :header="
          revision.data.value ? `Revision ${revision.data.value.number}: ${revision.data.value.title}` : 'Revision'
        "
        :style="{ width: '60rem' }"
        :breakpoints="{ '900px': '95vw' }"
        @update:visible="(v: boolean) => !v && (shown = null)"
      >
        <div v-if="!revision.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
        <template v-else>
          <p class="muted">
            {{ formatAbsolute(revision.data.value.at) }} · {{ revision.data.value.byName }} ·
            {{ kbStatusLabel[revision.data.value.status] }}
            <template v-if="revision.data.value.tags.length">
              · {{ revision.data.value.tags.map((t) => '#' + t).join(' ') }}</template
            >
          </p>
          <p v-if="revision.data.value.summary" class="muted">{{ revision.data.value.summary }}</p>
          <MarkdownView :source="revision.data.value.content" :files="revision.data.value.files" />
        </template>
      </Dialog>

      <Dialog v-model:visible="redacting" modal header="Redact a text" :style="{ width: '30rem' }">
        <Message severity="warn" :closable="false">
          The exact text is replaced with [redacted] in every revision, in the page, and in the AI request log of its
          tag scans. The history and the auth log say how many revisions held it, never the text. A file that shows a
          secret is deleted instead.
        </Message>
        <div class="field redact-field">
          <label for="kb-redact">The exact text, at least 4 characters</label>
          <InputText id="kb-redact" v-model="secret" autocomplete="off" spellcheck="false" />
        </div>
        <template #footer>
          <Button label="Cancel" text severity="secondary" @click="redacting = false" />
          <Button label="Redact" severity="danger" :disabled="secret.trim().length < 4" @click="redact" />
        </template>
      </Dialog>
    </template>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 999px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }
}

.pick-hint {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.857rem;
}

.revisions {
  list-style: none;
  margin: 0;
  padding: 0;
}

.revisions li {
  display: flex;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.revisions li.selected {
  background: var(--pnmt-bg);
}

.pickers {
  display: flex;
  gap: var(--pnmt-space-2);
  padding-top: 2px;
}

.body {
  min-width: 0;
  flex: 1;
}

.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.strong {
  font-weight: 600;
}

.note {
  font-style: italic;
}

.changes {
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.actions {
  display: flex;
  gap: var(--pnmt-space-1);
  margin-left: -0.5rem;
}

.added {
  color: var(--pnmt-ok);
}

.removed {
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.75rem;
}

.meta {
  margin: 0 0 var(--pnmt-space-2);
  padding-left: 1.2rem;
}

.counts-line {
  margin: 0 0 var(--pnmt-space-2);
}

.redact-field {
  margin-top: var(--pnmt-space-3);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}
</style>
