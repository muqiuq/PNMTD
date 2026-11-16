<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { dutyLabel, type DutyKind } from '@/views/cookie/cookieFormat'
import { actionDefaults, normalizeLevels, type ActionKey } from './cookieAdmin'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useCookieDefaults, useSection } from './useSection'

/**
 * Actions (docs/cookie-plan.md §6, §6.1, §7, §15, §19.1, §21.1): Cookie's actions, each Off, Propose (a person decides in
 * the decisions inbox, after Seraph) or Auto (Seraph's approval is enough), with the confidence below which it goes to a
 * person; the big brother, off until an admin turns it on; PNMT's duties, each off until an admin turns it on, and
 * whether Cookie writes their texts; and the output limits, which hold back whatever would go over them, Cookie's
 * messages and the duties' alike. The ticket actions say when Redmine can't take them now, whatever their level
 * (docs/cookie-fallback-plan.md §6.2).
 */
const s = useSection('actions', { normalize: (v) => ({ ...v, levels: normalizeLevels(v.levels) }) })
const defaults = useCookieDefaults()
const overview = useQuery({ queryKey: [...keys.cookie, 'admin', 'overview'], queryFn: api.cookie.overview })
const integrations = computed(() => overview.data.value?.acting?.integrations ?? null)
/** Why an action can't happen now, whatever its level: the level stays what Cookie does once Redmine is back. */
const unavailable = (key: ActionKey): string | null => {
  const i = integrations.value
  if (!i) return null
  if (key === 'ticket') {
    if (i.redmine.state !== 'writable') return i.redmine.reason
    return i.ticketProject ? null : "no project for Cookie's tickets (the Redmine tab)"
  }
  if (key === 'privateNote' || key === 'publicAnswer') return i.redmine.state !== 'writable' ? i.redmine.reason : null
  return null
}

const actions: { key: ActionKey; label: string; text: string }[] = [
  {
    key: 'pushover',
    label: 'Pushover',
    text: 'To a person, when very urgent: only PNMT users with a Pushover key, in working hours.',
  },
  { key: 'email', label: 'E-mail', text: "To a person, when urgent or important: only PNMT users' own addresses." },
  {
    key: 'ticket',
    label: 'Ticket',
    text: "A new Redmine ticket for a problem, never while one of Cookie's is open for it. Cookie's group needs Redmine: Link.",
  },
  {
    key: 'privateNote',
    label: 'Private note',
    text: "A note for staff on the case's ticket. Cookie's group needs Redmine: View.",
  },
  { key: 'publicAnswer', label: 'Public answer', text: "A note on the case's ticket that its author reads." },
  {
    key: 'objectNote',
    label: 'Note on a host or sensor',
    text: "Shown on the host's or sensor's page as Cookie's own note, with the date; stays inside PNMT, so Seraph doesn't review it. Cookie's group needs Monitoring: View.",
  },
  {
    key: 'mailEvent',
    label: 'Mail rule event',
    text: "For a mail an AI every time rule matched: the rule's OK or fail code on its sensor, reviewed by Seraph.",
  },
  {
    key: 'accessNotice',
    label: 'Access notice',
    text: "Tells the contacts set under Access about an unusual login, as its verdict's row says. Auto is the safe choice: Propose holds a break-in in the inbox until someone looks. Below the minimum confidence a verdict goes one lower. Code's notice for strong signals goes out whatever the level.",
  },
]
const levels: { label: string; value: S['ActionLevel'] }[] = [
  { label: 'Off', value: 'Off' },
  { label: 'Propose', value: 'Propose' },
  { label: 'Auto', value: 'Auto' },
]
/** A row's rule: every one is there once the draft is normalised. */
const rule = (key: ActionKey): S['ActionRule'] => s.draft.value!.levels[key]!

type Switch = keyof S['DutySwitches']
const duties: { key: Switch; kind: DutyKind; text: string }[] = [
  {
    key: 'firstAnswerReminder',
    kind: 'FirstAnswerReminder',
    text: "A ticket without a first answer by staff at its reminder (by default after 3 working hours) gets a private note, and its assignee a message, or the project's escalation group when nobody is assigned or the assignee doesn't work that day.",
  },
  {
    key: 'firstAnswerMissed',
    kind: 'FirstAnswerMissed',
    text: "When the first answer is overdue (by default after 4 working hours), the assignee, the escalation group and whoever is responsible in the availability plan are told, by Pushover too: it's urgent.",
  },
  {
    key: 'updateReminder',
    kind: 'UpdateReminder',
    text: 'A ticket without an update by staff at its reminder (by default after 12 days) gets a private note, and its assignee a reminder.',
  },
  {
    key: 'updateMissed',
    kind: 'UpdateMissed',
    text: 'When an update is overdue (by default after 14 days), the assignee, the escalation group and whoever is responsible are told.',
  },
  {
    key: 'problemTicket',
    kind: 'ProblemTicket',
    text: "A problem without progress for 2 days (or as set under Cases) gets a ticket in the project under Redmine → Cookie's tickets, with its timeline, linked to the host.",
  },
  {
    key: 'closingNote',
    kind: 'ClosingNote',
    text: 'When a problem with such a ticket is resolved, the ticket gets a note saying so. Nobody closes it but people.',
  },
]

type Limit = keyof S['OutputLimits']
const limits: { key: Limit; label: string }[] = [
  { key: 'pushoverPerPersonDay', label: 'Pushover per person, a day' },
  { key: 'pushoverPerPersonHour', label: 'Pushover per person, an hour' },
  { key: 'emailPerPersonDay', label: 'E-mail per person, a day' },
  { key: 'messagesPerDay', label: 'Pushover and e-mail in total, a day' },
  { key: 'ticketsPerDay', label: 'New Redmine tickets, a day' },
  { key: 'notesPerTicketDay', label: 'Notes on one ticket, a day' },
  { key: 'redmineNotesPerDay', label: 'Notes on Redmine in total, a day' },
  { key: 'publicAnswersPerDay', label: 'Public answers on tickets, a day' },
  { key: 'objectNotesPerDay', label: 'Notes on hosts and sensors, a day' },
  { key: 'maxNotesPerObject', label: 'Notes on one host or sensor' },
  { key: 'bigBrotherPerDay', label: 'Big brother calls, a day' },
  { key: 'bigBrotherPerCaseDay', label: 'Big brother calls per case, a day' },
]
const placed = (f: string) => f.startsWith('limits.') || f.startsWith('levels.')
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
      <section class="panel" aria-labelledby="actions-levels-title">
        <div id="actions-levels-title" class="panel-header">Cookie's actions</div>
        <div class="panel-body body">
          <p class="muted small intro">
            Off: Cookie never does it. Propose: a person decides in the decisions inbox, after Seraph. Auto: Seraph's
            approval is enough. Code checks every proposal first: the targets, Cookie's group's rights, mute and
            suppress, duplicates. Below its minimum confidence a proposal goes to a person, and so does anything Seraph
            can't review.
          </p>
          <div class="wide-table">
            <table class="rows">
              <thead>
                <tr>
                  <th scope="col">Action</th>
                  <th scope="col">Level</th>
                  <th scope="col">Minimum confidence</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="a in actions" :key="a.key">
                  <td>
                    <div class="label">{{ a.label }}</div>
                    <div class="muted small">{{ a.text }}</div>
                    <div v-if="unavailable(a.key)" class="small warn-text">
                      Unavailable now: {{ unavailable(a.key) }}
                    </div>
                    <small v-if="s.error(`levels.${a.key}`)" class="field-error">{{
                      s.error(`levels.${a.key}`)
                    }}</small>
                  </td>
                  <td class="nowrap">
                    <SelectButton
                      :model-value="rule(a.key).level"
                      :options="levels"
                      option-label="label"
                      option-value="value"
                      :allow-empty="false"
                      size="small"
                      :aria-label="`${a.label}, level`"
                      @update:model-value="(v: S['ActionLevel']) => (rule(a.key).level = v)"
                    />
                    <div class="muted small">Default {{ actionDefaults[a.key].level }}</div>
                  </td>
                  <td>
                    <InputNumber
                      :model-value="rule(a.key).minConfidence"
                      :min="0"
                      :max="1"
                      :step="0.05"
                      :min-fraction-digits="2"
                      :max-fraction-digits="2"
                      show-buttons
                      size="small"
                      class="confidence"
                      :disabled="rule(a.key).level === 'Off'"
                      :invalid="!!s.error(`levels.${a.key}`)"
                      :aria-label="`${a.label}, minimum confidence`"
                      @update:model-value="(v: number | null) => (rule(a.key).minConfidence = v ?? 0)"
                    />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="duty">
            <ToggleSwitch v-model="s.draft.value.bigBrother" input-id="actions-big-brother" />
            <div>
              <label for="actions-big-brother" class="label">The big brother</label>
              <div class="muted small">
                Two strong models that look for a cause and a fix when a case matters and nobody has taken care of it
                for a working day, or when someone with Cookie: Instruct asks on a case. They advise only: the solution
                goes to the case's ticket as a private note, or into the decisions inbox. Off by default: it costs
                money, from a budget of its own (Cookie__BigBrother__BudgetUsd) and within the limits below.
              </div>
            </div>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="actions-duties-title">
        <div id="actions-duties-title" class="panel-header">PNMT's duties</div>
        <div class="panel-body body">
          <Message severity="info" :closable="false" size="small">
            Every duty starts off: a new installation sends nothing until you switch a duty on. What fell due while a
            duty was off stays off; switching it on sends nothing old.
          </Message>
          <p class="muted small intro">
            The deadlines are each watched project's (Redmine); reminders and escalations wait for working hours, which
            are set in the availability plan's settings. They reach people by the e-mail address and Pushover key on
            their user. The notes and tickets need Redmine__WriteApiKey.
          </p>
          <div v-for="d in duties" :key="d.key" class="duty">
            <ToggleSwitch v-model="s.draft.value.duties[d.key]" :input-id="`duty-${d.key}`" />
            <div>
              <label :for="`duty-${d.key}`" class="label">{{ dutyLabel[d.kind] }}</label>
              <div class="muted small">{{ d.text }}</div>
            </div>
          </div>
          <div class="duty writes">
            <ToggleSwitch v-model="s.draft.value.cookieWritesDuties" input-id="actions-cookie-writes" />
            <div>
              <label for="actions-cookie-writes" class="label">Cookie writes the duties' texts</label>
              <div class="muted small">
                While Cookie runs, it writes each reminder's and escalation's text from the case, and Seraph reviews it.
                Code still decides when, to whom and through which limits. The template is used whenever Cookie is
                stopped, paused, slow or rejected.
              </div>
            </div>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="actions-limits-title">
        <div id="actions-limits-title" class="panel-header">Output limits</div>
        <div class="panel-body body">
          <p class="muted small intro">
            Counted before anything goes out, for Cookie and PNMT's duties alike. What would go over a limit isn't sent:
            it is held back in the outbox, and the Overview shows it, where an admin can send it. Each limit goes up to
            a ceiling only a new release raises.
          </p>
          <div class="limits">
            <div v-for="lim in limits" :key="lim.key" class="field">
              <label :for="`limit-${lim.key}`">{{ lim.label }}</label>
              <InputNumber
                v-model="s.draft.value.limits[lim.key]"
                :input-id="`limit-${lim.key}`"
                :min="0"
                :max="defaults.data.value?.ceilings[lim.key]"
                show-buttons
                size="small"
                :invalid="!!s.error(`limits.${lim.key}`)"
              />
              <small v-if="s.error(`limits.${lim.key}`)" class="field-error">{{ s.error(`limits.${lim.key}`) }}</small>
              <small v-else-if="defaults.data.value" class="field-hint">
                Default {{ defaults.data.value.limits[lim.key] }}, at most {{ defaults.data.value.ceilings[lim.key] }}.
              </small>
            </div>
          </div>
        </div>
      </section>

      <SectionActions
        :busy="s.busy.value"
        :dirty="s.dirty.value"
        :errors="s.errors.value"
        :placed="placed"
        @save="s.save()"
        @reset="s.reset()"
      />
      <SettingsHistory section="actions" title="the actions" @rolled-back="s.reload()" />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 1100px;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
}

.duty {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-3);
}

/* The switch keeps its size beside a long text. */
.duty > :first-child {
  flex: none;
}

.label {
  font-weight: 500;
}

.writes {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-3);
}

.wide-table {
  overflow-x: auto;
}

.rows {
  border-collapse: collapse;
  width: 100%;
}

.rows th {
  text-align: left;
  font-size: 0.857rem;
  font-weight: 600;
  color: var(--pnmt-muted);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  white-space: nowrap;
}

.rows td {
  padding: var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.confidence {
  width: 7.5rem;
}

.confidence :deep(input) {
  width: 100%;
}

.limits {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.small {
  font-size: 0.857rem;
}

.warn-text {
  color: var(--pnmt-warn);
}
</style>
