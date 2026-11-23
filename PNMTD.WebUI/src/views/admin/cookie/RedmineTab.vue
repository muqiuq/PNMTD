<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useSession } from '@/stores/session'
import { withUnknown, type Option } from './cookieAdmin'
import RedmineAwayList from './RedmineAwayList.vue'
import RedmineLinksPanel from './RedmineLinksPanel.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useGroupOptions, useUserOptions } from './usePickers'
import { useRedmineDefaults, useRedmineLookups, useRedmineSection } from './useSection'

/**
 * Redmine (docs/cookie-plan.md §7.2, §9.3): the watched projects, each with its deadlines, the roles that count as
 * staff, the statuses that stop the clock and its escalation group; and where the tickets for unresolved problems go
 * (§7.1). Writing to Redmine needs Redmine__WriteApiKey. Redmine: Manage's, through its own endpoints
 * (docs/cookie-redmine-tab-plan.md §3.2); the duties these deadlines drive are switched on Actions, and shown here.
 */
const s = useRedmineSection()
const lookups = useRedmineLookups()
const l = computed(() => lookups.data.value)
const groups = useGroupOptions()
const defaults = useRedmineDefaults()
const session = useSession()

/** The ticket duties' switches, read only: Actions → PNMT's duties, under Cookie: Manage. */
const ticketDuties = computed(() => {
  const d = s.duties.value
  if (!d) return []
  return [
    { label: 'First-answer reminder', on: d.firstAnswerReminder },
    { label: 'First answer missed', on: d.firstAnswerMissed },
    { label: 'Update reminder', on: d.updateReminder },
    { label: 'Update missed', on: d.updateMissed },
  ]
})
const canSwitch = computed(() => session.can('Cookie', 'Manage'))

/** The away watch (docs/cookie-redmine-tab-plan.md §6): switched on Actions, set up here. */
const users = useUserOptions()
const awayOn = computed(() => s.duties.value?.awayComments ?? false)
const noticeOff = computed(() => s.awayNotice.value?.level === 'Off')
const personOptions = computed(() => withUnknown(users.options.value, [s.draft.value?.away?.personId], users.unknown))
const absences: { value: S['AvailabilityFlag']; label: string }[] = [
  { value: 'Vacation', label: 'Vacation' },
  { value: 'Holiday', label: 'Holiday' },
  { value: 'Compensation', label: 'Compensation' },
]
const recipientOptions: { label: string; value: S['AwayRecipient'] }[] = [
  { label: 'The responsible of the day', value: 'Responsible' },
  { label: 'A chosen person', value: 'Person' },
]
const verdictOptions: { label: string; value: S['AwayWithoutVerdict'] }[] = [
  { label: 'Tell, marked not judged', value: 'Tell' },
  { label: 'Record only', value: 'Record' },
]
/** The links panel's check, shared by its query key: whether anyone in the plan can be found as an assignee at all. */
const links = useQuery({
  queryKey: [...keys.cookie, 'admin', 'redmine', 'links'],
  queryFn: api.cookie.redmineAdmin.links,
})
const nobodyLinked = computed(
  () =>
    !!links.data.value &&
    !links.data.value.rows.some((r) => r.inPlan && (r.state === 'Linked' || r.state === 'Matches')),
)

const named = (list: { id: number; name: string }[] | undefined, chosen: number[], what: string): Option<number>[] =>
  withUnknown(
    (list ?? []).map((x) => ({ label: x.name, value: x.id })),
    chosen,
    (id) => `${what} #${id}`,
  )
const projectName = (id: number) => l.value?.projects.find((p) => p.id === id)?.name ?? `Project #${id}`
const roleOptions = computed(() =>
  named(
    l.value?.roles,
    (s.draft.value?.projects ?? []).flatMap((p) => p.staffRoleIds),
    'Role',
  ),
)
const statusOptions = computed(() =>
  named(
    l.value?.statuses,
    (s.draft.value?.projects ?? []).flatMap((p) => p.stopStatusIds),
    'Status',
  ),
)
const groupOptions = computed(() =>
  withUnknown(
    groups.options.value,
    (s.draft.value?.projects ?? []).map((p) => p.escalationGroupId),
    groups.unknown,
  ),
)
const ticketProjects = computed(() =>
  named(l.value?.projects, s.draft.value?.ticketProjectId ? [s.draft.value.ticketProjectId] : [], 'Project'),
)
const trackers = computed(() =>
  named(l.value?.trackers, s.draft.value?.ticketTrackerId ? [s.draft.value.ticketTrackerId] : [], 'Tracker'),
)

const newProject = ref<number | null>(null)
const addable = computed(() =>
  (l.value?.projects ?? [])
    .filter((p) => !(s.draft.value?.projects ?? []).some((w) => w.projectId === p.id))
    .map((p) => ({ label: p.name, value: p.id })),
)
/** A project newly watched starts with the server's default deadlines (WatchedProject.Default). */
function addProject(projectId: number | null) {
  const fresh = defaults.data.value
  if (projectId === null || !s.draft.value || !fresh) return
  s.draft.value.projects.push({
    ...fresh,
    projectId,
    staffRoleIds: [...fresh.staffRoleIds],
    stopStatusIds: [...fresh.stopStatusIds],
  })
  newProject.value = null
}
function removeProject(projectId: number) {
  if (s.draft.value) s.draft.value.projects = s.draft.value.projects.filter((p) => p.projectId !== projectId)
}
const placed = (f: string) => f.startsWith('projects.') || f.startsWith('away.')
</script>

<template>
  <div class="tab">
    <ErrorState
      v-if="s.settings.isError.value && !s.draft.value"
      :error="s.settings.error.value"
      what="the settings"
      @retry="s.settings.refetch()"
    />
    <template v-else-if="s.draft.value">
      <div v-if="lookups.isPending.value" class="loading"><ProgressSpinner style="width: 32px; height: 32px" /></div>
      <template v-else-if="l">
        <Message v-if="!l.configured" severity="warn" :closable="false" size="small">
          Redmine is not configured: Redmine__Url, Redmine__ApiKey and Redmine__Projects in .env. Without it, no ticket
          is watched.
        </Message>
        <Message v-if="l.error" severity="error" :closable="false" size="small">
          Redmine did not answer: {{ l.error }}
        </Message>
        <Message v-if="l.rolesError" severity="warn" :closable="false" size="small">
          Redmine's roles could not be read, so staff roles can't be chosen: {{ l.rolesError }}
        </Message>
        <Message v-if="l.configured && !l.writable" severity="warn" :closable="false" size="small">
          Tickets and notes need Redmine__WriteApiKey. Until it is set, the reminders go by e-mail and Pushover only,
          and no ticket is opened.
        </Message>
      </template>
      <ErrorState
        v-else-if="lookups.isError.value"
        :error="lookups.error.value"
        what="Redmine's projects"
        @retry="lookups.refetch()"
      />

      <section class="panel" aria-labelledby="redmine-projects-title">
        <div id="redmine-projects-title" class="panel-header">
          Watched projects
          <Select
            v-model="newProject"
            :options="addable"
            option-label="label"
            option-value="value"
            filter
            placeholder="Watch a project…"
            size="small"
            class="end"
            aria-label="Watch a project"
            :disabled="!addable.length || !defaults.data.value"
            @update:model-value="addProject"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            Every ticket in a watched project needs a first answer by staff within its working hours, and an update
            every so many days. With no staff roles chosen, everyone but the ticket's author counts as staff. The
            working hours and holidays are the availability plan's settings; Cookie is told each ticket's own project's
            deadlines.
          </p>
          <p v-if="ticketDuties.length" class="muted small intro duties">
            The reminders and escalations at these deadlines:
            <template v-for="(d, i) in ticketDuties" :key="d.label"
              >{{ i ? ', ' : '' }}{{ d.label.toLowerCase() }} <strong>{{ d.on ? 'on' : 'off' }}</strong></template
            >. They are switched on
            <RouterLink v-if="canSwitch" :to="{ name: 'cookie-admin', params: { tab: 'actions' } }"
              >under Actions → PNMT's duties</RouterLink
            ><template v-else>under Actions → PNMT's duties, by someone with Cookie: Manage</template>.
          </p>
          <p v-if="!s.draft.value.projects.length" class="muted">No project is watched.</p>
          <div v-for="p in s.draft.value.projects" :key="p.projectId" class="project">
            <div class="project-head">
              <h3>{{ projectName(p.projectId) }}</h3>
              <Button
                label="Stop watching"
                icon="pi pi-trash"
                size="small"
                severity="secondary"
                text
                class="end"
                @click="removeProject(p.projectId)"
              />
            </div>
            <div class="form-grid">
              <div class="field">
                <label :for="`p${p.projectId}-first`">First answer within</label>
                <div class="inline">
                  <InputNumber
                    v-model="p.firstAnswerHours"
                    :input-id="`p${p.projectId}-first`"
                    :min="0.5"
                    :max="200"
                    :max-fraction-digits="1"
                    suffix=" working hours"
                    size="small"
                  />
                  <span class="muted">reminded at</span>
                  <InputNumber
                    v-model="p.firstAnswerReminderHours"
                    :min="0.5"
                    :max="200"
                    :max-fraction-digits="1"
                    suffix=" h"
                    size="small"
                    aria-label="First answer: reminded after working hours"
                    class="short"
                  />
                </div>
              </div>
              <div class="field">
                <label :for="`p${p.projectId}-update`">An update every</label>
                <div class="inline">
                  <InputNumber
                    v-model="p.updateDays"
                    :input-id="`p${p.projectId}-update`"
                    :min="1"
                    :max="365"
                    suffix=" days"
                    size="small"
                  />
                  <span class="muted">reminded at</span>
                  <InputNumber
                    v-model="p.updateReminderDays"
                    :min="1"
                    :max="365"
                    suffix=" d"
                    size="small"
                    aria-label="Update: reminded after days"
                    class="short"
                  />
                </div>
              </div>
              <div class="field">
                <label :for="`p${p.projectId}-roles`">Staff roles</label>
                <MultiSelect
                  v-model="p.staffRoleIds"
                  :input-id="`p${p.projectId}-roles`"
                  :options="roleOptions"
                  option-label="label"
                  option-value="value"
                  filter
                  display="chip"
                  size="small"
                  placeholder="Everyone but the author"
                />
              </div>
              <div class="field">
                <label :for="`p${p.projectId}-stop`">Statuses that stop the clock</label>
                <MultiSelect
                  v-model="p.stopStatusIds"
                  :input-id="`p${p.projectId}-stop`"
                  :options="statusOptions"
                  option-label="label"
                  option-value="value"
                  filter
                  display="chip"
                  size="small"
                  placeholder="None"
                />
                <small class="field-hint">Such as Feedback or On hold: the ticket waits for its requester.</small>
              </div>
              <div class="field">
                <label :for="`p${p.projectId}-group`">Escalation group</label>
                <Select
                  v-model="p.escalationGroupId"
                  :input-id="`p${p.projectId}-group`"
                  :options="groupOptions"
                  option-label="label"
                  option-value="value"
                  filter
                  show-clear
                  size="small"
                  placeholder="None"
                />
                <small class="field-hint"
                  >Told when a deadline is missed, and reminded for tickets nobody is assigned to.</small
                >
              </div>
            </div>
            <small v-if="s.error(`projects.${p.projectId}`)" class="field-error">{{
              (s.errors.value[`projects.${p.projectId}`] ?? []).join(' ')
            }}</small>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="redmine-own-title">
        <div id="redmine-own-title" class="panel-header">Cookie's tickets</div>
        <div class="panel-body form-grid">
          <p class="muted small intro wide">
            Where the ticket for a problem nobody resolves goes, with the case's timeline, linked to its host.
          </p>
          <div class="field">
            <label for="redmine-ticket-project">Project</label>
            <Select
              v-model="s.draft.value.ticketProjectId"
              input-id="redmine-ticket-project"
              :options="ticketProjects"
              option-label="label"
              option-value="value"
              filter
              show-clear
              placeholder="None: no tickets"
              :invalid="!!s.error('ticketProjectId')"
            />
            <small v-if="s.error('ticketProjectId')" class="field-error">{{ s.error('ticketProjectId') }}</small>
          </div>
          <div class="field">
            <label for="redmine-ticket-tracker">Tracker</label>
            <Select
              v-model="s.draft.value.ticketTrackerId"
              input-id="redmine-ticket-tracker"
              :options="trackers"
              option-label="label"
              option-value="value"
              show-clear
              placeholder="The project's default"
              :invalid="!!s.error('ticketTrackerId')"
            />
            <small v-if="s.error('ticketTrackerId')" class="field-error">{{ s.error('ticketTrackerId') }}</small>
          </div>
        </div>
      </section>

      <section v-if="s.draft.value.away" class="panel" aria-labelledby="redmine-away-title">
        <div id="redmine-away-title" class="panel-header">
          While someone is away
          <Tag :value="awayOn ? 'On' : 'Off'" :severity="awayOn ? 'success' : 'secondary'" class="end" />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            When someone comments on a ticket whose assignee is away in the availability plan, Cookie judges from the
            comment whether someone should act before they are back. If so, code tells whom you choose here, in working
            hours, at most once per ticket in 4 hours; nobody is told once the assignee is back. Cookie's verdicts need
            Cookie running; the case book reads the tickets every 5 minutes.
          </p>
          <Message v-if="!awayOn" severity="info" :closable="false" size="small">
            Off: it is switched on
            <RouterLink v-if="canSwitch" :to="{ name: 'cookie-admin', params: { tab: 'actions' } }"
              >under Actions → PNMT's duties</RouterLink
            ><template v-else>under Actions → PNMT's duties, by someone with Cookie: Manage</template>. What is set here
            can be saved while it is off.
          </Message>
          <Message v-if="noticeOff" severity="warn" :closable="false" size="small">
            Cookie's away notice is Off (Actions): Cookie judges nothing, and every comment is handled as without a
            verdict.
          </Message>
          <Message v-if="nobodyLinked" severity="warn" :closable="false" size="small">
            Nobody in the availability plan is linked to a Redmine user (below), so no assignee can be found away.
          </Message>
          <div class="form-grid">
            <div class="field">
              <span class="label">Away means</span>
              <div class="inline">
                <span v-for="a in absences" :key="a.value" class="check">
                  <Checkbox v-model="s.draft.value.away.awayFlags" :input-id="`away-${a.value}`" :value="a.value" />
                  <label :for="`away-${a.value}`">{{ a.label }}</label>
                </span>
              </div>
              <small v-if="s.error('away.awayFlags')" class="field-error">{{ s.error('away.awayFlags') }}</small>
              <small v-else class="field-hint"
                >The checkboxes of a day's type in the plan. A default day off and a closed day never count.</small
              >
            </div>
            <div class="field">
              <span class="label">Who is told</span>
              <SelectButton
                v-model="s.draft.value.away.recipient"
                :options="recipientOptions"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                aria-label="Who is told"
              />
              <small class="field-hint">
                {{
                  s.draft.value.away.recipient === 'Person'
                    ? 'The chosen person, if they work today; otherwise the responsible of the day.'
                    : 'Whoever the plan makes responsible now; when that is nobody, the chosen person, if they work today.'
                }}
                Never the away assignee, nor whoever wrote the comment.
              </small>
            </div>
            <div class="field">
              <label for="away-person">The chosen person</label>
              <Select
                v-model="s.draft.value.away.personId"
                input-id="away-person"
                :options="personOptions"
                option-label="label"
                option-value="value"
                filter
                show-clear
                size="small"
                placeholder="Nobody"
                :invalid="!!s.error('away.personId')"
              />
              <small v-if="s.error('away.personId')" class="field-error">{{ s.error('away.personId') }}</small>
            </div>
            <div class="field">
              <span class="label">Without Cookie's verdict</span>
              <SelectButton
                v-model="s.draft.value.away.withoutVerdict"
                :options="verdictOptions"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                aria-label="Without Cookie's verdict"
              />
              <small class="field-hint"
                >Cookie stopped or paused, no verdict within 30 minutes, or a comment that looks like it tries to
                instruct an AI.</small
              >
            </div>
            <div class="field switch-field">
              <ToggleSwitch v-model="s.draft.value.away.privateNotes" input-id="away-private" />
              <label for="away-private">Private notes too</label>
            </div>
            <div class="field switch-field">
              <ToggleSwitch v-model="s.draft.value.away.pushover" input-id="away-pushover" />
              <label for="away-pushover">Pushover too</label>
            </div>
          </div>
        </div>
      </section>

      <SectionActions
        :busy="s.busy.value"
        :dirty="s.dirty.value"
        :errors="s.errors.value"
        :placed="(f) => placed(f) || f === 'ticketProjectId' || f === 'ticketTrackerId'"
        @save="s.save()"
        @reset="s.reset()"
      />
      <SettingsHistory section="redmine" title="the Redmine settings" @rolled-back="s.reload()" />

      <RedmineAwayList />
      <RedmineLinksPanel />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.loading {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-3);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
}

.project {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding-bottom: var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.project:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.project-head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.project-head h3 {
  font-size: 1rem;
}

.inline {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.short :deep(input) {
  width: 5rem;
}

.end {
  margin-left: auto;
}

.small {
  font-size: 0.857rem;
}

.check {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.switch-field {
  flex-direction: row;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.label {
  font-weight: 600;
}
</style>
