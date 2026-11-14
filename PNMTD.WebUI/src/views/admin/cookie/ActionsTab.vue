<script setup lang="ts">
import InputNumber from 'primevue/inputnumber'
import Message from 'primevue/message'
import ToggleSwitch from 'primevue/toggleswitch'
import type { S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { dutyLabel, type DutyKind } from '@/views/cookie/cookieFormat'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useCookieDefaults, useSection } from './useSection'

/**
 * Actions (docs/cookie-plan.md §6.1, §7, §19.1): PNMT's duties, each off until an admin turns it on, and the output
 * limits, which hold back whatever would go over them, Cookie's messages and the duties' alike.
 */
const s = useSection('actions')
const defaults = useCookieDefaults()

type Switch = keyof S['DutySwitches']
const duties: { key: Switch; kind: DutyKind; text: string }[] = [
  {
    key: 'firstAnswerReminder',
    kind: 'FirstAnswerReminder',
    text: "A ticket without a first answer by staff at its reminder (by default after 3 working hours) gets a private note, and its assignee a message, or the project's escalation group when nobody is assigned.",
  },
  {
    key: 'firstAnswerMissed',
    kind: 'FirstAnswerMissed',
    text: "When the first answer is overdue (by default after 4 working hours), the assignee and the escalation group are told, by Pushover too: it's urgent.",
  },
  {
    key: 'updateReminder',
    kind: 'UpdateReminder',
    text: 'A ticket without an update by staff at its reminder (by default after 12 days) gets a private note, and its assignee a reminder.',
  },
  {
    key: 'updateMissed',
    kind: 'UpdateMissed',
    text: 'When an update is overdue (by default after 14 days), the assignee and the escalation group are told.',
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
const limits: { key: Limit; label: string; later?: boolean }[] = [
  { key: 'pushoverPerPersonDay', label: 'Pushover per person, a day' },
  { key: 'pushoverPerPersonHour', label: 'Pushover per person, an hour' },
  { key: 'emailPerPersonDay', label: 'E-mail per person, a day' },
  { key: 'messagesPerDay', label: 'Pushover and e-mail in total, a day' },
  { key: 'ticketsPerDay', label: 'New Redmine tickets, a day' },
  { key: 'notesPerTicketDay', label: 'Notes on one ticket, a day' },
  { key: 'redmineNotesPerDay', label: 'Notes on Redmine in total, a day' },
  { key: 'publicAnswersPerDay', label: 'Public answers on tickets, a day', later: true },
  { key: 'objectNotesPerDay', label: 'Notes on hosts, sensors and locations, a day', later: true },
  { key: 'bigBrotherPerDay', label: 'Big brother calls, a day', later: true },
  { key: 'bigBrotherPerCaseDay', label: 'Big brother calls per case, a day', later: true },
]
const placed = (f: string) => f.startsWith('limits.')
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
      <section class="panel" aria-labelledby="actions-duties-title">
        <div id="actions-duties-title" class="panel-header">PNMT's duties</div>
        <div class="panel-body body">
          <Message severity="info" :closable="false" size="small">
            Every duty starts off: a new installation sends nothing until you switch a duty on. What fell due while a
            duty was off stays off; switching it on sends nothing old.
          </Message>
          <p class="muted small intro">
            The deadlines are each watched project's (Redmine); reminders and escalations wait for working hours. The
            notes and tickets need Redmine__WriteApiKey.
          </p>
          <div v-for="d in duties" :key="d.key" class="duty">
            <ToggleSwitch v-model="s.draft.value.duties[d.key]" :input-id="`duty-${d.key}`" />
            <div>
              <label :for="`duty-${d.key}`" class="label">{{ dutyLabel[d.kind] }}</label>
              <div class="muted small">{{ d.text }}</div>
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
                Default {{ defaults.data.value.limits[lim.key] }}, at most {{ defaults.data.value.ceilings[lim.key]
                }}<template v-if="lim.later">; used once Cookie acts</template>.
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

.label {
  font-weight: 500;
}

.limits {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.small {
  font-size: 0.857rem;
}
</style>
