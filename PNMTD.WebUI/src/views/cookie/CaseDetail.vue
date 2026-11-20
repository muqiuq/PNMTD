<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import { importanceSeverity } from '@/views/notifications/importance'
import ZabbixSeverityTag from '@/views/zabbix/ZabbixSeverityTag.vue'
import AccessCase from './AccessCase.vue'
import {
  actorLabel,
  caseKindName,
  dutyName,
  dutyStateName,
  dutyStateSeverity,
  entryKindLabel,
  entryKindSeverity,
  personKinds,
} from './cookieFormat'
import DecisionLog from './DecisionLog.vue'

/**
 * A case (docs/cookie-plan.md §4.3, §7, §15, §21.5): what is bad in it (a host's bad sensors and its Zabbix problems),
 * its timeline with people's comments, its duties with what came of them, Cookie's decisions on it, the big brother's
 * last solution, and for a ticket the clock of §7.2. Refreshed with the list. Cookie: Use comments (Cookie reads it in
 * its next round); Cookie: Instruct stops a case (Don't go on with this, saying why), lets Cookie go on with it, and asks
 * the big brother, which the next round calls. A stopped case stays open and visible, marked, with who, when and why.
 * An access case (docs/cookie-access-plan.md §10.4) adds its logins, verdict, notices and answers.
 */
const props = defineProps<{ id: string }>()
const session = useSession()
const detail = useQuery({
  queryKey: computed(() => [...keys.cookie, 'case', props.id]),
  queryFn: () => api.cookie.case(props.id),
  refetchInterval: 30_000,
})
const d = computed(() => detail.data.value)
const c = computed(() => d.value?.case)
const canMonitoring = computed(() => session.can('Monitoring', 'View'))
const canLocations = computed(() => session.can('Locations', 'View'))
const canRedmine = computed(() => session.can('Redmine', 'View'))

const zabbixSeverity = (m: S['CaseMember']) => m.severity as S['ZabbixSeverity']
const byPerson = (e: S['CookieEntryItem']) => (personKinds as readonly string[]).includes(e.kind)
/** A raised case's why (docs/lounge-plan.md §15.4): the voice that opened it in the lounge, and its reason. */
const raised = computed(() =>
  c.value?.kind === 'Raised' ? d.value?.entries.find((e) => e.kind === 'raised') : undefined,
)

// --- comments, Stop and Go on (§4.3) --------------------------------------------------------------------------------
const queryClient = useQueryClient()
const notify = useNotify()
const busy = ref(false)
const comment = ref('')
const commentError = ref<string | undefined>()
const dialog = ref<'stop' | 'go-on' | null>(null)
const dialogText = ref('')
const dialogError = ref<string | undefined>()
/** Why the big brother wasn't asked (it is off, or Cookie is stopped). */
const bigBrotherProblem = ref<string | undefined>()
const dialogOpen = computed({ get: () => dialog.value !== null, set: (v) => !v && (dialog.value = null) })
const dialogTitle = computed(() => (dialog.value === 'stop' ? "Don't go on with this" : 'Let Cookie go on'))
watch(
  () => props.id,
  () => {
    comment.value = ''
    commentError.value = undefined
    dialog.value = null
    bigBrotherProblem.value = undefined
  },
)

watch(dialogText, () => (dialogError.value = undefined))
watch(comment, () => (commentError.value = undefined))

async function postComment() {
  const text = comment.value.trim()
  commentError.value = text ? undefined : 'Write something first.'
  if (!text) return
  busy.value = true
  try {
    await api.cookie.comment(props.id, text)
    comment.value = ''
    notify.success('Comment added', 'Cookie reads it in its next round.')
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    if (e instanceof ApiError && e.field('text')) commentError.value = e.field('text')
    else notify.error(e, 'Comment not added')
  } finally {
    busy.value = false
  }
}

function ask(kind: 'stop' | 'go-on') {
  dialog.value = kind
  dialogText.value = ''
  dialogError.value = undefined
}

// --- the big brother (§15.2) -----------------------------------------------------------------------------------------
/** Why the server won't ask it (409), in plain words. */
function bigBrotherRefusal(e: ApiError): string {
  if (e.code === 'cookie_big_brother_off')
    return 'The big brother is off: an admin turns it on under Admin → Cookie → Actions.'
  if (e.code === 'cookie_stopped') return 'Cookie is stopped: an admin starts it on Admin → Cookie.'
  return e.message
}

async function askBigBrother() {
  busy.value = true
  bigBrotherProblem.value = undefined
  try {
    await api.cookie.askBigBrother(props.id)
    notify.success('Asked the big brother', 'The next round calls it.')
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) bigBrotherProblem.value = bigBrotherRefusal(e)
    else notify.error(e, 'Not asked')
  } finally {
    busy.value = false
  }
}

async function confirm() {
  const kind = dialog.value
  if (!kind) return
  const text = dialogText.value.trim()
  dialogError.value = kind === 'stop' && !text ? 'Say why.' : undefined
  if (dialogError.value) return
  busy.value = true
  try {
    if (kind === 'stop') await api.cookie.stopCase(props.id, text)
    else await api.cookie.goOn(props.id, text || null)
    dialog.value = null
    notify.success(
      kind === 'stop' ? 'Stopped' : 'Cookie goes on with it',
      kind === 'stop' ? 'Cookie leaves the case alone, and its duties stop.' : undefined,
    )
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    if (e instanceof ApiError && e.field('text')) dialogError.value = e.field('text')
    else notify.error(e, kind === 'stop' ? 'Not stopped' : 'Not done')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ErrorState v-if="detail.isError.value && !d" :error="detail.error.value" what="the case" @retry="detail.refetch()" />
  <div v-else-if="!d || !c" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <template v-else>
    <div class="panel-header">
      <Tag :value="caseKindName(c.kind)" severity="secondary" />
      <Tag :value="c.open ? 'open' : 'closed'" :severity="c.open ? 'info' : 'secondary'" />
      <Tag :value="c.importance" :severity="importanceSeverity(c.importance)" />
      <Tag v-if="c.stopped" value="stopped" severity="warn" />
      <RouterLink :to="{ name: 'cookie', params: { tab: 'trace' }, query: { case: c.id } }" class="trace-link"
        >Trace of this case <i class="pi pi-angle-right" aria-hidden="true"
      /></RouterLink>
    </div>
    <div class="panel-body body">
      <h2 class="title">{{ c.title }}</h2>
      <Message v-if="c.stopped" severity="warn" :closable="false" class="stopped">
        <div>
          <strong>Stopped</strong> by {{ c.stopped.by ?? 'someone' }}, {{ formatAbsolute(c.stopped.at)
          }}<template v-if="c.stopped.comment">: “{{ c.stopped.comment }}”</template>
        </div>
        <div class="small">
          Cookie leaves the case alone and its duties stop. It goes on when someone lets it, when the case closes, or
          when something new fails.
        </div>
        <Button
          v-if="d.canStop"
          label="Go on…"
          icon="pi pi-play"
          size="small"
          severity="secondary"
          class="go-on"
          :disabled="busy"
          @click="ask('go-on')"
        />
      </Message>
      <Message v-if="c.kind === 'Raised'" severity="secondary" :closable="false" class="stopped">
        <div>
          <strong>Raised in the lounge</strong
          ><template v-if="raised"> by {{ raised.actorName ?? actorLabel(raised) }}: “{{ raised.text }}”</template>
        </div>
        <RouterLink :to="{ name: 'lounge' }" class="small">The conversation in the lounge</RouterLink>
      </Message>
      <p v-if="c.cookieNote" class="note">{{ c.cookieNote }}</p>
      <dl class="facts">
        <dt>Opened</dt>
        <dd>{{ formatAbsolute(c.openedAt) }}</dd>
        <template v-if="c.closedAt">
          <dt>Closed</dt>
          <dd>{{ formatAbsolute(c.closedAt) }}</dd>
        </template>
        <dt>Last change</dt>
        <dd><RelativeTime :value="c.changedAt" /></dd>
        <dt>Last progress</dt>
        <dd><RelativeTime :value="c.progressAt" /></dd>
        <template v-if="c.next?.dueAt">
          <dt>Next deadline</dt>
          <dd>{{ dutyName(c.next?.kind) }}, {{ formatAbsolute(c.next?.dueAt) }}</dd>
        </template>
        <template v-if="c.hostId">
          <dt>Host</dt>
          <dd>
            <RouterLink v-if="canMonitoring" :to="{ name: 'host', params: { id: c.hostId } }">{{
              c.hostName ?? "The host's page"
            }}</RouterLink>
            <span v-else>{{ c.hostName ?? '–' }}</span>
          </dd>
        </template>
        <template v-if="c.locationId">
          <dt>Location</dt>
          <dd>
            <RouterLink v-if="canLocations" :to="{ name: 'location', params: { id: c.locationId } }">{{
              c.locationName ?? "The location's page"
            }}</RouterLink>
            <span v-else>{{ c.locationName ?? '–' }}</span>
          </dd>
        </template>
        <template v-if="d.ownIssueUrl">
          <dt>Cookie's ticket</dt>
          <dd>
            <a :href="d.ownIssueUrl" target="_blank" rel="noopener">{{ d.ownIssueUrl }}</a>
          </dd>
        </template>
      </dl>
      <AccessCase v-if="c.kind === 'Access'" :id="c.id" :location-id="c.locationId ?? null" />
      <div v-if="d.canStop && !c.stopped" class="case-actions">
        <Button
          label="Stop…"
          icon="pi pi-ban"
          size="small"
          severity="secondary"
          outlined
          :disabled="busy"
          @click="ask('stop')"
        />
        <span class="muted small">Don't go on with this: Cookie leaves it alone, and its duties stop.</span>
      </div>
      <div v-if="d.canAskBigBrother && !d.bigBrotherAskedAt" class="case-actions">
        <Button
          label="Ask the big brother"
          icon="pi pi-lightbulb"
          size="small"
          severity="secondary"
          outlined
          :disabled="busy"
          @click="askBigBrother"
        />
        <span class="muted small"
          >A strong model looks for the cause and a fix, for a person to carry out. It costs.</span
        >
      </div>
      <Message v-if="bigBrotherProblem" severity="warn" :closable="false" size="small">{{ bigBrotherProblem }}</Message>
      <Message v-if="d.bigBrotherAskedAt" severity="info" :closable="false" size="small">
        Asked the big brother <RelativeTime :value="d.bigBrotherAskedAt" />: the next round calls it.
      </Message>

      <section v-if="d.solution" class="solution" aria-labelledby="case-solution-title">
        <h3 id="case-solution-title" class="sub">The big brother's solution</h3>
        <div class="muted small">
          {{ formatAbsolute(d.solution.at)
          }}<template v-if="d.solution.model">
            · <span class="mono">{{ d.solution.model }}</span></template
          >
          · nothing was run
        </div>
        <pre class="solution-text">{{ d.solution.text }}</pre>
      </section>

      <template v-if="d.ticket">
        <h3 class="sub">The ticket's clock</h3>
        <dl class="facts">
          <dt>Ticket</dt>
          <dd class="links">
            <RouterLink v-if="canRedmine" :to="{ name: 'redmine-issue', params: { id: d.ticket.issueId } }"
              >#{{ d.ticket.issueId }}</RouterLink
            >
            <span v-else>#{{ d.ticket.issueId }}</span>
            <a v-if="d.ticket.issueUrl" :href="d.ticket.issueUrl" target="_blank" rel="noopener"
              >in Redmine <i class="pi pi-external-link" aria-hidden="true"
            /></a>
          </dd>
          <dt>Author</dt>
          <dd>
            {{ d.ticket.author ?? '–' }}
            <Tag v-if="d.ticket.authorIsStaff" value="staff" severity="secondary" />
          </dd>
          <dt>Assignee</dt>
          <dd>{{ d.ticket.assignee ?? 'nobody' }}</dd>
          <dt>Status</dt>
          <dd>{{ d.ticket.status ?? '–' }}</dd>
          <dt>Created</dt>
          <dd>{{ formatAbsolute(d.ticket.createdAt) }}</dd>
          <dt>First answer</dt>
          <dd>
            <template v-if="d.ticket.firstAnswerAt">{{ formatAbsolute(d.ticket.firstAnswerAt) }}</template>
            <span v-else-if="d.ticket.authorIsStaff" class="muted">not needed: written by staff</span>
            <span v-else class="muted">none yet</span>
          </dd>
          <dt>Last staff update</dt>
          <dd>{{ formatAbsolute(d.ticket.lastStaffUpdateAt) }}</dd>
        </dl>
      </template>

      <template v-if="d.members.length">
        <h3 class="sub">What is bad</h3>
        <DataTable :value="d.members" size="small" class="table">
          <Column header="What">
            <template #body="{ data }">
              <span class="what">
                <i
                  :class="data.type === 'zabbix' ? 'pi pi-chart-bar' : 'pi pi-bolt'"
                  :title="data.type === 'zabbix' ? 'Zabbix problem' : 'Sensor'"
                  aria-hidden="true"
                />
                <RouterLink
                  v-if="data.type === 'sensor' && canMonitoring"
                  :to="{ name: 'sensor', params: { id: data.id } }"
                  >{{ data.name }}</RouterLink
                >
                <span v-else>{{ data.name }}</span>
              </span>
              <div v-if="data.message" class="muted small wrap">{{ data.message }}</div>
            </template>
          </Column>
          <Column header="Code / severity" style="width: 9rem">
            <template #body="{ data }">
              <ZabbixSeverityTag v-if="data.type === 'zabbix' && data.severity" :severity="zabbixSeverity(data)" />
              <CodeTag v-else-if="data.code !== null && data.code !== undefined" :code="data.code" />
              <span v-else class="muted">–</span>
            </template>
          </Column>
          <Column header="Since" style="width: 6.5rem">
            <template #body="{ data }"><RelativeTime :value="data.since" /></template>
          </Column>
          <Column header="Acknowledged" style="width: 7.5rem">
            <template #body="{ data }">
              <template v-if="data.type === 'zabbix'">{{ data.acknowledged ? 'yes' : 'no' }}</template>
              <span v-else class="muted">–</span>
            </template>
          </Column>
        </DataTable>
      </template>

      <template v-if="d.duties.length">
        <h3 class="sub">Duties</h3>
        <DataTable :value="d.duties" size="small" data-key="id" class="table">
          <Column header="Duty">
            <template #body="{ data }">{{ dutyName(data.kind) }}</template>
          </Column>
          <Column header="Due" style="width: 9.5rem">
            <template #body="{ data }">{{ formatAbsolute(data.dueAt) }}</template>
          </Column>
          <Column header="State" style="width: 7rem">
            <template #body="{ data }">
              <Tag :value="dutyStateName(data.state)" :severity="dutyStateSeverity(data.state)" />
            </template>
          </Column>
          <Column header="Outcome">
            <template #body="{ data }">
              <span class="small wrap">{{ data.outcome ?? '' }}</span>
              <div v-if="data.doneAt" class="muted small"><RelativeTime :value="data.doneAt" /></div>
            </template>
          </Column>
        </DataTable>
      </template>

      <h3 class="sub">Cookie's decisions</h3>
      <DecisionLog :case-id="c.id" :page-size="10" />

      <h3 class="sub">Timeline</h3>
      <form v-if="d.canComment" class="comment" novalidate @submit.prevent="postComment">
        <Textarea
          v-model="comment"
          rows="2"
          auto-resize
          maxlength="1000"
          placeholder="A comment for the people on this case, and for Cookie"
          aria-label="Comment"
          :invalid="!!commentError"
        />
        <div class="comment-actions">
          <Button type="submit" label="Comment" icon="pi pi-comment" size="small" :loading="busy" />
          <small v-if="commentError" class="field-error">{{ commentError }}</small>
          <small v-else class="muted">Cookie reads it in its next round.</small>
        </div>
      </form>
      <p v-if="!d.entries.length" class="muted">Nothing yet.</p>
      <ol v-else class="timeline">
        <li v-for="(e, i) in d.entries" :key="i" :class="{ person: byPerson(e) }">
          <div class="entry-head small">
            <span class="nowrap">{{ formatAbsolute(e.at) }}</span>
            <Tag :value="entryKindLabel(e.kind)" :severity="entryKindSeverity(e.kind)" />
            <span :class="byPerson(e) ? 'actor' : 'muted'">{{ actorLabel(e) }}</span>
          </div>
          <div class="wrap">{{ e.text }}</div>
        </li>
      </ol>

      <Dialog v-model:visible="dialogOpen" :header="dialogTitle" modal :style="{ width: 'min(30rem, 95vw)' }">
        <form id="case-dialog" class="form" novalidate @submit.prevent="confirm">
          <p class="text">
            <template v-if="dialog === 'stop'"
              >Cookie leaves “{{ c.title }}” alone: no triage, no work, no notes. What is due closes as stopped, and
              nothing new falls due. The case stays open, marked stopped, and PNMT's notification rules still
              fire.</template
            >
            <template v-else>Cookie works on “{{ c.title }}” again, and its duties are planned again.</template>
          </p>
          <div class="field">
            <label for="case-dialog-text">{{ dialog === 'stop' ? 'Why' : 'A comment (if you like)' }}</label>
            <Textarea
              id="case-dialog-text"
              v-model="dialogText"
              rows="3"
              auto-resize
              maxlength="1000"
              autofocus
              :invalid="!!dialogError"
            />
            <small v-if="dialogError" class="field-error">{{ dialogError }}</small>
          </div>
        </form>
        <template #footer>
          <Button label="Cancel" severity="secondary" text @click="dialog = null" />
          <Button
            type="submit"
            form="case-dialog"
            :label="dialog === 'stop' ? 'Stop' : 'Go on'"
            :severity="dialog === 'stop' ? 'warn' : undefined"
            :loading="busy"
          />
        </template>
      </Dialog>
    </div>
  </template>
</template>

<style scoped>
.trace-link {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.title {
  font-size: 1.1rem;
  margin: 0;
}

.note {
  margin: 0;
}

.sub {
  font-size: 0.93rem;
  margin: var(--pnmt-space-3) 0 0;
}

.links {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  align-items: center;
}

.table {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.what {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.what i {
  color: var(--pnmt-muted);
  font-size: 0.85rem;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.timeline {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.timeline li {
  border-left: 2px solid var(--pnmt-border);
  padding-left: var(--pnmt-space-2);
}

.timeline li.person {
  border-left-color: var(--p-primary-color);
}

.actor {
  font-weight: 600;
}

.stopped :deep(.p-message-text) {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--pnmt-space-1);
}

.go-on {
  margin-top: var(--pnmt-space-1);
}

.case-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.comment {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.comment-actions {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.text {
  margin-top: 0;
}

.solution {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.solution-text {
  margin: 0;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  max-height: 50vh;
  overflow: auto;
  border: 1px solid var(--pnmt-border);
  border-left: 3px solid var(--p-primary-color);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-none-soft);
}

.entry-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  margin-bottom: 2px;
}

.small {
  font-size: 0.857rem;
}
</style>
