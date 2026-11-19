<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import { fileSize } from '@/components/markdown/files'
import InlineField from '@/components/InlineField.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useCopy } from '@/composables/useCopy'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import RedmineTextView from './RedmineTextView.vue'
import TargetPicker, { type Target } from './TargetPicker.vue'

/**
 * A ticket's page (docs/audit-redmine-zabbix-plan.md §6.4), a full page: its description and history as Redmine shows
 * them, its details, what it is linked to in PNMT, its related tickets and attachments (links to Redmine). *Link to…* a
 * knowledge base page, a location, a host or a sensor; *Open in Redmine*; *Copy link*. Always asked of Redmine.
 */
const route = useRoute()
const session = useSession()
const queryClient = useQueryClient()
const { run, done, ask } = useActions()
const copy = useCopy()

const id = computed(() => Number(route.params.id))
const query = useQuery({
  queryKey: computed(() => [...keys.redmine, 'issue', id.value]),
  queryFn: () => api.redmine.issue(id.value),
  retry: (count, e) => !(e instanceof ApiError && e.status < 500) && count < 2,
})
const issue = computed(() => query.data.value)
const error = computed(() => (query.error.value instanceof ApiError ? query.error.value : null))
const canLink = computed(() => session.can('Redmine', 'Link'))

const linkIcon: Record<S['RedmineLinkKind'], string> = {
  Page: 'pi pi-book',
  Location: 'pi pi-map-marker',
  Host: 'pi pi-server',
  Sensor: 'pi pi-wave-pulse',
}
function linkRoute(l: S['RedmineLinkItem']) {
  switch (l.kind) {
    case 'Page': {
      const n = /^KB-(\d+)/.exec(l.targetName)
      return n ? { name: 'kb-page', params: { number: n[1] } } : null
    }
    case 'Location':
      return { name: 'location', params: { id: l.targetId } }
    case 'Host':
      return { name: 'host', params: { id: l.targetId } }
    default:
      return { name: 'sensor', params: { id: l.targetId } }
  }
}
const kindName: Record<S['RedmineLinkKind'], string> = {
  Page: 'page',
  Location: 'location',
  Host: 'host',
  Sensor: 'sensor',
}

async function refresh() {
  await queryClient.invalidateQueries({ queryKey: keys.redmine })
}
async function link(target: Target) {
  const linked = await run(() => api.redmine.link({ issueId: id.value, [target.key]: target.id }), {
    success: `#${id.value} linked to ${target.name}`,
    failure: 'Not linked',
  })
  if (linked) await refresh()
}
async function unlink(l: S['RedmineLinkItem']) {
  if (!(await ask(`Unlink ${l.targetName}?`, `#${l.issueId} stays in Redmine.`, 'Unlink'))) return
  if (await done(() => api.redmine.unlink(l.id), { success: `${l.targetName} unlinked` })) await refresh()
}
async function saveNote(l: S['RedmineLinkItem'], value: unknown) {
  await api.redmine.note(l.id, typeof value === 'string' && value.trim() ? value.trim() : null)
  await refresh()
}

const details = computed(() => {
  const i = issue.value
  if (!i) return []
  return [
    ['Project', i.project.name],
    ['Author', i.author],
    ['Assignee', i.assignedTo ?? '–'],
    ['Start', i.startDate],
    ['Due', i.dueDate],
    ['Done', i.doneRatio != null ? `${i.doneRatio} %` : null],
    ['Estimated', i.estimatedHours != null ? `${i.estimatedHours} h` : null],
    ...i.fields.map((f) => [f.name, f.value]),
  ].filter((d): d is [string, string] => !!d[1])
})
const journals = computed(() => (issue.value?.journals ?? []).filter((j) => j.notes || j.changes.length))
const ownUrl = computed(() => `${location.origin}/pnmt-admin/redmine/issues/${id.value}`)
</script>

<template>
  <div class="page">
    <EmptyState
      v-if="error?.code === 'redmine_not_configured'"
      icon="pi pi-ticket"
      title="Redmine isn't configured"
      text="Set Redmine__Url, Redmine__ApiKey and Redmine__Projects in the deployment's .env."
    />
    <template v-else-if="error?.status === 404">
      <PageHeader :title="`#${id}`" :crumbs="[{ label: 'Redmine', to: { name: 'redmine' } }, { label: `#${id}` }]" />
      <EmptyState
        icon="pi pi-ticket"
        :title="error.code === 'redmine_other_project' ? 'Not in PNMT’s Redmine projects' : 'Not found in Redmine'"
        :text="error.message"
      />
    </template>
    <ErrorState
      v-else-if="query.isError.value && !issue"
      :error="query.error.value"
      what="the ticket"
      @retry="query.refetch()"
    />
    <div v-else-if="!issue" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader
        :title="`#${issue.id} ${issue.subject}`"
        :crumbs="[{ label: 'Redmine', to: { name: 'redmine' } }, { label: `#${issue.id}` }]"
      >
        <template #status>
          <Tag v-if="issue.tracker" :value="issue.tracker" severity="secondary" />
          <Tag :value="issue.status" :severity="issue.isClosed ? 'secondary' : 'info'" />
          <Tag v-if="issue.priority" :value="issue.priority" severity="contrast" />
          <i v-if="issue.isPrivate" class="pi pi-lock muted" title="Private in Redmine" />
        </template>
        <template #actions>
          <Button
            as="a"
            :href="issue.url"
            target="_blank"
            rel="noopener noreferrer"
            label="Open in Redmine"
            icon="pi pi-external-link"
            size="small"
          />
          <Button
            label="Copy link"
            icon="pi pi-copy"
            size="small"
            severity="secondary"
            outlined
            @click="copy(ownUrl, `PNMT's page of #${issue.id}`)"
          />
        </template>
      </PageHeader>
      <Message v-if="issue.isClosed && issue.closedOn" severity="secondary">
        Closed {{ formatAbsolute(issue.closedOn) }}.
      </Message>

      <div class="layout">
        <div class="main">
          <article class="panel" aria-label="Description">
            <div class="panel-body">
              <RedmineTextView v-if="issue.description?.text.trim()" :text="issue.description" />
              <p v-else class="muted none">No description.</p>
            </div>
          </article>

          <section class="panel" aria-labelledby="rm-history-title">
            <div id="rm-history-title" class="panel-header">History</div>
            <div class="panel-body">
              <p v-if="!journals.length" class="muted none">No comments or changes yet.</p>
              <ol v-else class="journals">
                <li v-for="j in journals" :key="j.id" class="journal">
                  <div class="who">
                    <strong>{{ j.user ?? 'Someone' }}</strong>
                    <span class="muted"><RelativeTime :value="j.createdOn" /></span>
                    <Tag v-if="j.private" value="Private" severity="warn" />
                  </div>
                  <ul v-if="j.changes.length" class="changes">
                    <li v-for="(c, k) in j.changes" :key="k">
                      <strong>{{ c.field }}</strong>: {{ c.old ?? '–' }} → {{ c.new ?? '–' }}
                    </li>
                  </ul>
                  <RedmineTextView v-if="j.notes?.text.trim()" :text="j.notes" class="notes" />
                </li>
              </ol>
            </div>
          </section>
        </div>

        <aside class="side">
          <section class="panel" aria-labelledby="rm-details-title">
            <div id="rm-details-title" class="panel-header">Details</div>
            <dl class="panel-body facts">
              <template v-for="[k, v] in details" :key="k">
                <dt>{{ k }}</dt>
                <dd>{{ v }}</dd>
              </template>
              <dt>Created</dt>
              <dd><RelativeTime :value="issue.createdOn" /></dd>
              <dt>Updated</dt>
              <dd><RelativeTime :value="issue.updatedOn" /></dd>
            </dl>
          </section>

          <section class="panel" aria-labelledby="rm-links-title">
            <div id="rm-links-title" class="panel-header">In PNMT</div>
            <div class="panel-body">
              <p v-if="!issue.links.length" class="muted none">Not linked to a page, location, host or sensor.</p>
              <ul v-else class="list">
                <li v-for="l in issue.links" :key="l.id" class="link">
                  <i :class="linkIcon[l.kind]" class="muted" :title="kindName[l.kind]" aria-hidden="true" />
                  <span class="link-text">
                    <RouterLink v-if="linkRoute(l)" :to="linkRoute(l)!">{{ l.targetName }}</RouterLink>
                    <span v-else>{{ l.targetName }}</span>
                    <InlineField
                      :value="l.note"
                      :save="(v) => saveNote(l, v)"
                      label="Note"
                      field="note"
                      placeholder="No note"
                      :readonly="!canLink"
                      class="note"
                    />
                  </span>
                  <Button
                    v-if="canLink"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Unlink ${l.targetName}`"
                    @click="unlink(l)"
                  />
                </li>
              </ul>
              <div v-if="canLink" class="picker">
                <label class="muted" for="rm-link-to">Link to…</label>
                <TargetPicker id="rm-link-to" @pick="link" />
              </div>
            </div>
          </section>

          <section v-if="issue.related.length" class="panel" aria-labelledby="rm-related-title">
            <div id="rm-related-title" class="panel-header">Related tickets</div>
            <div class="panel-body">
              <ul class="list">
                <li v-for="r in issue.related" :key="`${r.relation}-${r.id}`">
                  <span class="muted relation">{{ r.relation }}</span>
                  <RouterLink :to="{ name: 'redmine-issue', params: { id: r.id } }" :class="{ closed: r.isClosed }">{{
                    r.subject ? `#${r.id} ${r.subject}` : `#${r.id}`
                  }}</RouterLink>
                  <span v-if="r.status" class="muted"> · {{ r.status }}</span>
                </li>
              </ul>
            </div>
          </section>

          <section v-if="issue.attachments.length" class="panel" aria-labelledby="rm-files-title">
            <div id="rm-files-title" class="panel-header">Attachments</div>
            <div class="panel-body">
              <ul class="list">
                <li v-for="a in issue.attachments" :key="a.id">
                  <i class="pi pi-paperclip muted" aria-hidden="true" />
                  <span class="link-text">
                    <a :href="a.url" target="_blank" rel="noopener noreferrer">{{ a.filename }}</a>
                    <span class="muted detail"
                      >{{ fileSize(a.size) }}<template v-if="a.author"> · {{ a.author }}</template></span
                    >
                  </span>
                </li>
              </ul>
            </div>
          </section>
        </aside>
      </div>
    </template>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 999px) {
  .layout {
    grid-template-columns: minmax(0, 1fr);
  }
}

.main,
.side {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.none {
  margin: 0;
}

.journals {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.journal + .journal {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-3);
}

.who {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.changes {
  margin: var(--pnmt-space-1) 0;
  padding-left: var(--pnmt-space-4);
}

.notes {
  margin-top: var(--pnmt-space-2);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.list li {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
}

.link-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.detail {
  font-size: 0.85em;
}

.note {
  font-size: 0.9em;
}

.picker {
  margin-top: var(--pnmt-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.relation {
  white-space: nowrap;
}

.closed {
  text-decoration: line-through;
}
</style>
