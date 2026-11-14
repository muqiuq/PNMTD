<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import ErrorState from '@/components/ErrorState.vue'
import { withUnknown, type Option } from './cookieAdmin'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useGroupOptions } from './usePickers'
import { useCookieDefaults, useRedmineLookups, useSection } from './useSection'

/**
 * Redmine (docs/cookie-plan.md §7.2, §9.3): the watched projects, each with its deadlines, the roles that count as
 * staff, the statuses that stop the clock and its escalation group; and where the tickets for unresolved problems go
 * (§7.1). Writing to Redmine needs Redmine__WriteApiKey.
 */
const s = useSection('redmine')
const lookups = useRedmineLookups()
const l = computed(() => lookups.data.value)
const groups = useGroupOptions()
const defaults = useCookieDefaults()

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
  const fresh = defaults.data.value?.newProject
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
const placed = (f: string) => f.startsWith('projects.')
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
            every so many days. With no staff roles chosen, everyone but the ticket's author counts as staff.
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

      <SectionActions
        :busy="s.busy.value"
        :dirty="s.dirty.value"
        :errors="s.errors.value"
        :placed="(f) => placed(f) || f === 'ticketProjectId' || f === 'ticketTrackerId'"
        @save="s.save()"
        @reset="s.reset()"
      />
      <SettingsHistory section="redmine" title="the Redmine settings" @rolled-back="s.reload()" />
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
</style>
