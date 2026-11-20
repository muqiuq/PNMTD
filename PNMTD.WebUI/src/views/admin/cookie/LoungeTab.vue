<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import { maxPeriods, nextPeriod, normalizeWeek, periodProblems, tidyPeriods } from '@/views/admin/availability/hours'
import { withUnknown } from './cookieAdmin'
import LoungeMemoryPanel from './LoungeMemoryPanel.vue'
import LoungeScenesPanel from './LoungeScenesPanel.vue'
import {
  describeRhythm,
  efforts,
  emailLevels,
  loungeProblems,
  loungeStateSeverity,
  maxMessagesPerHour,
  voices,
} from './loungeAdmin'
import PromptsPanel from './PromptsPanel.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useGroupOptions } from './usePickers'
import { useSection } from './useSection'

/**
 * Admin → Cookie → Lounge (docs/lounge-plan.md §15.3): Start and Stop of its own, independent of Cookie's (§3.3); the
 * rhythm, with what it means; what the voices read with, and who among the readers lacks those rights (§3.2); their two
 * powers; their convictions and memory; the budget. Then the memory browser, the characters and prompts, and the scenes.
 */
const s = useSection('lounge', {
  normalize: (v) => ({ ...v, awake: normalizeWeek(v.awake ?? []) }),
  prepare: (v) => ({ ...v, awake: v.awake.map((d) => ({ ...d, periods: tidyPeriods(d.periods) })) }),
})
const status = useQuery({
  queryKey: [...keys.lounge, 'admin', 'status'],
  queryFn: api.lounge.status,
  refetchInterval: 10_000,
})
const st = computed(() => status.data.value)
const opinions = useQuery({ queryKey: [...keys.lounge, 'admin', 'opinions'], queryFn: api.lounge.opinions })
const groups = useGroupOptions()
const groupOptions = computed(() => withUnknown(groups.options.value, [s.draft.value?.groupId], groups.unknown))
const { run, done, ask, busy } = useActions()

// The client's checks first, then the server's answer (§12).
const problems = computed(() => (s.draft.value ? loungeProblems(s.draft.value) : {}))
const error = (field: string) => s.error(field) ?? problems.value[field]
const invalid = computed(() => Object.keys(problems.value).length > 0)
const rhythm = computed(() => {
  if (!s.draft.value) return ''
  return s.dirty.value || !st.value ? describeRhythm(s.draft.value) : st.value.rhythm
})

// --- Start, Stop, Talk now, Put to sleep (§3.3, §14) ------------------------------------------------------------------
/** Start is disabled with the reason: the server's, or the unsaved group the draft would set. */
const cantStart = computed(() => {
  if (!st.value) return null
  if (!s.draft.value?.groupId)
    return 'Choose what the lounge reads with first: everyone reads what Cookie and Seraph say about it.'
  return st.value.cantStart
})
const refresh = [keys.lounge] as const
const start = () => run(api.lounge.start, { success: 'The lounge is started', invalidate: refresh })
const stop = async () => {
  if (
    !(await ask(
      'Stop the lounge?',
      'Cookie and Seraph stop talking. The room stays readable; nobody can write.',
      'Stop',
    ))
  )
    return
  await run(api.lounge.stop, { success: 'The lounge is stopped', invalidate: refresh })
}
const talk = () => run(api.lounge.talk, { success: 'They talk now', invalidate: refresh })
const sleep = () => run(api.lounge.sleep, { success: 'Asleep until the next awake period', invalidate: refresh })
const resetPositions = async () => {
  if (
    !(await ask(
      'Reset the positions?',
      'Both convictions go back to their start positions. The history stays.',
      'Reset positions',
    ))
  )
    return
  await done(api.lounge.resetPositions, { success: 'Positions reset', invalidate: refresh })
}

// --- the awake hours (§5.1) --------------------------------------------------------------------------------------------
async function copyWorkingHours() {
  const plan = await run(api.availability.settings, { failure: 'The working hours could not be read' })
  if (plan && s.draft.value) s.draft.value.awake = normalizeWeek(plan.settings.week ?? [])
}
const addPeriod = (periods: S['HoursPeriod'][]) => periods.push(nextPeriod(periods))

// --- convictions (§6) ----------------------------------------------------------------------------------------------------
const owners = voices.map((v) => ({ label: v.label, value: v.value as string }))
const current = (key: string) => {
  const last = (opinions.data.value ?? []).filter((o) => o.conviction === key)
  const of = (voice: string) => last.find((o) => o.voice === voice)?.after
  return { cookie: of('cookie'), seraph: of('seraph') }
}
const history = (key: string) => (opinions.data.value ?? []).filter((o) => o.conviction === key).slice(0, 10)
const position = (v: number | undefined, start: number) => (v ?? start).toFixed(2)

const fields = [
  'awake',
  'asleepOnHolidays',
  'messagesPerHour',
  'longestScene',
  'disagreeShare',
  'repliesPerHour',
  'importantPerDay',
  'groupId',
  'effort',
  'toolCallsPerMessage',
  'casesPerDay',
  'emailLevel',
  'emailsPerDay',
  'emailsPerPersonWeek',
  'halfLifeDays',
  'memoriesInPrompt',
  'maxDriftPerDay',
  'convictions',
  'budgetUsd',
]
const placed = (f: string) => fields.includes(f) || f.startsWith('convictions.')
const effortOptions = efforts.map((e) => ({ label: e, value: e }))
const usd = (v: number) => `${v.toFixed(2)} USD`
</script>

<template>
  <div class="tab">
    <!-- 1. Status: Start and Stop of its own (§3.3). -->
    <section class="panel" aria-labelledby="lounge-status-title">
      <div id="lounge-status-title" class="panel-header">
        The lounge
        <Tag
          v-if="st"
          :value="st.running ? st.state : 'Stopped'"
          :severity="loungeStateSeverity(st.state)"
          class="head-tag"
        />
      </div>
      <div class="panel-body body">
        <ErrorState
          v-if="status.isError.value"
          :error="status.error.value"
          what="the lounge's status"
          @retry="status.refetch()"
        />
        <template v-else-if="st">
          <p class="muted small intro">
            Cookie and Seraph off duty, in one conversation every signed-in user reads (Lounge in the menu). Started and
            stopped here, independent of Cookie: Cookie stopped, the lounge may still talk; the lounge stopped, Cookie
            works on.
          </p>
          <div class="line">
            <Button
              v-if="!st.running"
              label="Start"
              icon="pi pi-play"
              :disabled="busy || !!cantStart"
              :title="cantStart ?? undefined"
              @click="start"
            />
            <Button v-else label="Stop" icon="pi pi-stop" severity="secondary" :disabled="busy" @click="stop" />
            <Button
              v-if="st.running"
              label="Talk now"
              icon="pi pi-comments"
              severity="secondary"
              outlined
              :disabled="busy"
              @click="talk"
            />
            <Button
              v-if="st.running"
              label="Put to sleep"
              icon="pi pi-moon"
              severity="secondary"
              outlined
              :disabled="busy"
              @click="sleep"
            />
            <span v-if="st.running && st.startedAt" class="muted small"
              >Started by {{ st.startedBy ?? 'someone' }}, <RelativeTime :value="st.startedAt"
            /></span>
            <span v-else-if="st.stoppedAt" class="muted small"
              >Stopped by {{ st.stoppedBy ?? 'someone' }}, <RelativeTime :value="st.stoppedAt"
            /></span>
          </div>
          <Message v-if="!st.running && cantStart" severity="warn" :closable="false" size="small" class="cant-start">
            {{ cantStart }}
          </Message>
          <Message
            v-if="st.running && st.reason"
            :severity="st.state === 'Paused' ? 'warn' : 'info'"
            :closable="false"
            size="small"
          >
            {{ st.reason }}
          </Message>
          <dl class="facts">
            <dt>Today</dt>
            <dd>
              {{ st.scenesToday }} scenes, {{ st.messagesToday }} messages, {{ st.commentsToday }} comments,
              {{ st.emailsToday }} e-mails, {{ st.casesToday }} cases raised
            </dd>
            <dt>This month</dt>
            <dd>{{ usd(st.budget.spent) }} of {{ st.budget.limit > 0 ? usd(st.budget.limit) : 'no limit' }}</dd>
            <template v-if="st.nextAt && st.running">
              <dt>Next</dt>
              <dd>{{ formatAbsolute(st.nextAt) }}</dd>
            </template>
            <template v-if="st.lastError">
              <dt>Last error</dt>
              <dd class="wrap">{{ st.lastError }} (<RelativeTime :value="st.lastErrorAt" />)</dd>
            </template>
          </dl>
        </template>
      </div>
    </section>

    <ErrorState
      v-if="s.settings.isError.value && !s.draft.value"
      :error="s.settings.error.value"
      what="the settings"
      @retry="s.settings.refetch()"
    />
    <form v-else-if="s.draft.value" class="tab" novalidate @submit.prevent="!invalid && s.save()">
      <!-- 2. Rhythm (§5). -->
      <section class="panel" aria-labelledby="lounge-rhythm-title">
        <div id="lounge-rhythm-title" class="panel-header">Rhythm</div>
        <div class="panel-body body">
          <p class="muted small intro">
            When they are awake, in PNMT's time zone, and how much they talk. Outside the awake hours they sleep: a
            message waits for the morning.
          </p>
          <div class="hours">
            <div v-for="day in s.draft.value.awake" :key="day.day" class="day-row">
              <div class="day-name">{{ day.day }}</div>
              <div class="periods">
                <span v-if="!day.periods.length" class="muted">asleep</span>
                <span v-for="(p, i) in day.periods" :key="i" class="period">
                  <InputText
                    v-model="p.from"
                    size="small"
                    class="time mono"
                    maxlength="5"
                    :aria-label="`${day.day}, period ${i + 1}, from`"
                  />
                  <span>–</span>
                  <InputText
                    v-model="p.to"
                    size="small"
                    class="time mono"
                    maxlength="5"
                    :aria-label="`${day.day}, period ${i + 1}, to`"
                  />
                  <Button
                    icon="pi pi-times"
                    size="small"
                    severity="secondary"
                    text
                    :aria-label="`Remove ${day.day}'s period ${i + 1}`"
                    @click="day.periods.splice(i, 1)"
                  />
                </span>
                <Button
                  v-if="day.periods.length < maxPeriods"
                  label="Add"
                  icon="pi pi-plus"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Add a period on ${day.day}`"
                  @click="addPeriod(day.periods)"
                />
                <small v-for="m in periodProblems(day.periods)" :key="m" class="field-error">{{ m }}</small>
              </div>
            </div>
          </div>
          <small v-if="error('awake')" class="field-error" role="alert">{{ error('awake') }}</small>
          <div class="line">
            <Button
              label="Copy the working hours"
              icon="pi pi-copy"
              size="small"
              severity="secondary"
              outlined
              :disabled="busy"
              @click="copyWorkingHours"
            />
            <span class="switch">
              <ToggleSwitch v-model="s.draft.value.asleepOnHolidays" input-id="lounge-holidays" />
              <label for="lounge-holidays">Asleep on holidays</label>
            </span>
          </div>
          <div class="grid">
            <div class="field">
              <label for="lounge-per-hour">Messages an hour</label>
              <InputNumber
                v-model="s.draft.value.messagesPerHour"
                input-id="lounge-per-hour"
                :min="0"
                :max="maxMessagesPerHour"
                show-buttons
                :invalid="!!error('messagesPerHour')"
              />
              <small v-if="error('messagesPerHour')" class="field-error">{{ error('messagesPerHour') }}</small>
              <small v-else class="field-hint"
                >0 to {{ maxMessagesPerHour }}: from about 45 an hour, one continuous conversation.</small
              >
            </div>
            <div class="field">
              <label for="lounge-longest">Longest scene</label>
              <InputNumber
                v-model="s.draft.value.longestScene"
                input-id="lounge-longest"
                :min="2"
                :max="10"
                show-buttons
                suffix=" messages"
                :invalid="!!error('longestScene')"
              />
              <small v-if="error('longestScene')" class="field-error">{{ error('longestScene') }}</small>
              <small v-else class="field-hint">2 or 3 most often; a disagreement gets 2 more.</small>
            </div>
            <div class="field">
              <label for="lounge-disagree">Share of disagreements</label>
              <InputNumber
                v-model="s.draft.value.disagreeShare"
                input-id="lounge-disagree"
                :min="0"
                :max="1"
                :min-fraction-digits="1"
                :max-fraction-digits="2"
                :step="0.05"
                show-buttons
                :invalid="!!error('disagreeShare')"
              />
              <small v-if="error('disagreeShare')" class="field-error">{{ error('disagreeShare') }}</small>
              <small v-else class="field-hint">0 to 1: the share of scenes set up as a disagreement.</small>
            </div>
            <div class="field">
              <label for="lounge-replies">Replies to people an hour</label>
              <InputNumber
                v-model="s.draft.value.repliesPerHour"
                input-id="lounge-replies"
                :min="0"
                :max="60"
                show-buttons
                :invalid="!!error('repliesPerHour')"
              />
              <small v-if="error('repliesPerHour')" class="field-error">{{ error('repliesPerHour') }}</small>
              <small v-else class="field-hint">0 to 60; they don't count against the messages an hour.</small>
            </div>
            <div class="field">
              <label for="lounge-important">Important messages a day</label>
              <InputNumber
                v-model="s.draft.value.importantPerDay"
                input-id="lounge-important"
                :min="0"
                :max="10"
                show-buttons
                :invalid="!!error('importantPerDay')"
              />
              <small v-if="error('importantPerDay')" class="field-error">{{ error('importantPerDay') }}</small>
              <small v-else class="field-hint"
                >0 to 10, each voice: when something really matters, it may say it fully, up to six sentences instead of
                one. Raising a case may be long too.</small
              >
            </div>
          </div>
          <p class="rhythm" data-test="rhythm">{{ rhythm }}</p>
          <Message
            v-if="st?.budgetLasts"
            :severity="st.budgetWarning ? 'warn' : 'secondary'"
            :closable="false"
            size="small"
          >
            {{ st.budgetLasts }}
          </Message>
        </div>
      </section>

      <!-- 3. Reads with (§3.2). -->
      <section class="panel" aria-labelledby="lounge-reads-title">
        <div id="lounge-reads-title" class="panel-header">Reads with</div>
        <div class="panel-body body">
          <p class="muted small intro">
            Everyone reads the lounge, so the voices read only with a group you choose for that audience, and never more
            than Cookie's group: the lower of the two. There is no default. A group with Monitoring: View, Redmine: View
            and Availability: View is a good start.
          </p>
          <div class="field">
            <label for="lounge-group">Group</label>
            <Select
              v-model="s.draft.value.groupId"
              input-id="lounge-group"
              :options="groupOptions"
              option-label="label"
              option-value="value"
              filter
              show-clear
              placeholder="None: the lounge can't start"
              class="group"
              :invalid="!!error('groupId')"
            />
            <small v-if="error('groupId')" class="field-error">{{ error('groupId') }}</small>
          </div>
          <template v-if="st">
            <div class="field">
              <span class="label">The voices read with</span>
              <div class="tags">
                <span v-if="!st.readsWith.length" class="muted">nothing yet</span>
                <Tag v-for="r in st.readsWith" :key="r" :value="r" severity="secondary" />
              </div>
            </div>
            <div v-if="st.gap.length" class="gap" role="group" aria-label="Who lacks these rights">
              <p v-for="g in st.gap" :key="`${g.area}:${g.level}`" class="small">
                <i class="pi pi-exclamation-triangle" aria-hidden="true" />
                {{ g.lacking }} of {{ st.users }} users don't have {{ g.area }}: {{ g.level }}; they will read what
                Cookie and Seraph say about it.
                <span class="muted">{{ g.users.join(', ') }}{{ g.users.length < g.lacking ? ', …' : '' }}</span>
              </p>
            </div>
            <p v-else-if="st.readsWith.length" class="muted small">Every user has these rights.</p>
            <div class="field">
              <span class="label">Who may write</span>
              <span class="small">{{ st.writers.length ? st.writers.join(', ') : 'nobody yet' }}</span>
              <small class="field-hint">Admins, and the members of groups with Lounge: Use (Admin → Groups).</small>
            </div>
          </template>
        </div>
      </section>

      <!-- 4. Powers (§8). -->
      <section class="panel" aria-labelledby="lounge-powers-title">
        <div id="lounge-powers-title" class="panel-header">Powers</div>
        <div class="panel-body body">
          <p class="muted small intro">
            Their only two: opening a case for Cookie's normal work, and a few e-mails to PNMT's users. Each is checked
            by code and limited; an e-mail is reviewed by the other voice's model, never its own.
          </p>
          <div class="grid">
            <div class="field">
              <label for="lounge-cases">Cases a day</label>
              <InputNumber
                v-model="s.draft.value.casesPerDay"
                input-id="lounge-cases"
                :min="0"
                :max="5"
                show-buttons
                :invalid="!!error('casesPerDay')"
              />
              <small v-if="error('casesPerDay')" class="field-error">{{ error('casesPerDay') }}</small>
            </div>
            <div class="field">
              <label id="lounge-email-level">E-mail</label>
              <SelectButton
                v-model="s.draft.value.emailLevel"
                :options="emailLevels"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                aria-labelledby="lounge-email-level"
              />
              <small class="field-hint">Propose: a person approves each in the decisions inbox.</small>
            </div>
            <div class="field">
              <label for="lounge-emails-day">E-mails a day</label>
              <InputNumber
                v-model="s.draft.value.emailsPerDay"
                input-id="lounge-emails-day"
                :min="0"
                :max="10"
                show-buttons
                :invalid="!!error('emailsPerDay')"
              />
              <small v-if="error('emailsPerDay')" class="field-error">{{ error('emailsPerDay') }}</small>
            </div>
            <div class="field">
              <label for="lounge-emails-week">Per person a week</label>
              <InputNumber
                v-model="s.draft.value.emailsPerPersonWeek"
                input-id="lounge-emails-week"
                :min="0"
                :max="3"
                show-buttons
                :invalid="!!error('emailsPerPersonWeek')"
              />
              <small v-if="error('emailsPerPersonWeek')" class="field-error">{{ error('emailsPerPersonWeek') }}</small>
            </div>
          </div>
        </div>
      </section>

      <!-- 6. Convictions (§6). -->
      <section class="panel" aria-labelledby="lounge-convictions-title">
        <div id="lounge-convictions-title" class="panel-header">Convictions</div>
        <div class="panel-body body">
          <p class="muted small intro">
            One very strong opinion each, on the other side of the other's: a question with two poles, from −1 to +1. A
            night may move a position by the drift a day at most; the owner never drops below 0.5 on its side.
          </p>
          <small v-if="error('convictions')" class="field-error" role="alert">{{ error('convictions') }}</small>
          <div v-for="(c, i) in s.draft.value.convictions" :key="i" class="conviction">
            <div class="grid">
              <div class="field">
                <label :for="`lounge-c${i}-key`">Key</label>
                <InputText :id="`lounge-c${i}-key`" v-model="c.key" class="mono" />
              </div>
              <div class="field">
                <label :id="`lounge-c${i}-owner`">Owner</label>
                <SelectButton
                  v-model="c.owner"
                  :options="owners"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  :aria-labelledby="`lounge-c${i}-owner`"
                />
              </div>
            </div>
            <div class="field">
              <label :for="`lounge-c${i}-question`">Question</label>
              <InputText :id="`lounge-c${i}-question`" v-model="c.question" />
            </div>
            <div class="field">
              <label :for="`lounge-c${i}-minus`">−1</label>
              <InputText :id="`lounge-c${i}-minus`" v-model="c.minus" />
            </div>
            <div class="field">
              <label :for="`lounge-c${i}-plus`">+1</label>
              <InputText :id="`lounge-c${i}-plus`" v-model="c.plus" />
            </div>
            <div class="grid">
              <div class="field">
                <label :for="`lounge-c${i}-cookie`">Cookie starts at</label>
                <InputNumber
                  v-model="c.cookie"
                  :input-id="`lounge-c${i}-cookie`"
                  :min="-1"
                  :max="1"
                  :min-fraction-digits="2"
                  :max-fraction-digits="2"
                  :step="0.05"
                  show-buttons
                />
              </div>
              <div class="field">
                <label :for="`lounge-c${i}-seraph`">Seraph starts at</label>
                <InputNumber
                  v-model="c.seraph"
                  :input-id="`lounge-c${i}-seraph`"
                  :min="-1"
                  :max="1"
                  :min-fraction-digits="2"
                  :max-fraction-digits="2"
                  :step="0.05"
                  show-buttons
                />
              </div>
            </div>
            <small v-if="error(`convictions.${i}`)" class="field-error">{{ error(`convictions.${i}`) }}</small>
            <p class="small muted">
              Now: Cookie {{ position(current(c.key).cookie, c.cookie) }}, Seraph
              {{ position(current(c.key).seraph, c.seraph) }}.
            </p>
            <ul v-if="history(c.key).length" class="history small">
              <li v-for="o in history(c.key)" :key="o.id">
                <RelativeTime :value="o.at" />: {{ o.voice === 'seraph' ? 'Seraph' : 'Cookie' }}
                {{ o.before.toFixed(2) }} → {{ o.after.toFixed(2) }}, “{{ o.why }}”
              </li>
            </ul>
          </div>
          <div class="grid">
            <div class="field">
              <label for="lounge-drift">Drift a day</label>
              <InputNumber
                v-model="s.draft.value.maxDriftPerDay"
                input-id="lounge-drift"
                :min="0"
                :max="0.2"
                :min-fraction-digits="2"
                :max-fraction-digits="2"
                :step="0.01"
                show-buttons
                :invalid="!!error('maxDriftPerDay')"
              />
              <small v-if="error('maxDriftPerDay')" class="field-error">{{ error('maxDriftPerDay') }}</small>
            </div>
          </div>
          <div class="line">
            <Button
              label="Reset positions"
              icon="pi pi-replay"
              size="small"
              severity="secondary"
              outlined
              :disabled="busy"
              @click="resetPositions"
            />
          </div>
        </div>
      </section>

      <!-- 7. Memory (§7). -->
      <section class="panel" aria-labelledby="lounge-memory-title">
        <div id="lounge-memory-title" class="panel-header">Memory</div>
        <div class="panel-body body">
          <p class="muted small intro">
            A memory nobody touches is at half strength after the half-life, and forgotten below 0.15. Three keepsakes
            each never fade.
          </p>
          <div class="grid">
            <div class="field">
              <label for="lounge-half-life">Half-life</label>
              <InputNumber
                v-model="s.draft.value.halfLifeDays"
                input-id="lounge-half-life"
                :min="1"
                :max="60"
                suffix=" days"
                show-buttons
                :invalid="!!error('halfLifeDays')"
              />
              <small v-if="error('halfLifeDays')" class="field-error">{{ error('halfLifeDays') }}</small>
            </div>
            <div class="field">
              <label for="lounge-in-prompt">Memories in a prompt</label>
              <InputNumber
                v-model="s.draft.value.memoriesInPrompt"
                input-id="lounge-in-prompt"
                :min="5"
                :max="30"
                show-buttons
                :invalid="!!error('memoriesInPrompt')"
              />
              <small v-if="error('memoriesInPrompt')" class="field-error">{{ error('memoriesInPrompt') }}</small>
            </div>
          </div>
        </div>
      </section>

      <!-- 8. Budget and effort (§11). -->
      <section class="panel" aria-labelledby="lounge-budget-title">
        <div id="lounge-budget-title" class="panel-header">Budget and effort</div>
        <div class="panel-body body">
          <p class="muted small intro">
            A budget of its own a calendar month, apart from Cookie's: spent, the voices sleep until the 1st. Banter
            doesn't need much reasoning.
          </p>
          <div class="grid">
            <div class="field">
              <label for="lounge-budget">Budget a month</label>
              <InputNumber
                v-model="s.draft.value.budgetUsd"
                input-id="lounge-budget"
                mode="currency"
                currency="USD"
                locale="en-US"
                :min="0"
                :max="1000"
                :invalid="!!error('budgetUsd')"
              />
              <small v-if="error('budgetUsd')" class="field-error">{{ error('budgetUsd') }}</small>
              <small v-else class="field-hint">0 for none.</small>
            </div>
            <div class="field">
              <label id="lounge-effort">Effort</label>
              <SelectButton
                v-model="s.draft.value.effort"
                :options="effortOptions"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                aria-labelledby="lounge-effort"
              />
              <small v-if="error('effort')" class="field-error">{{ error('effort') }}</small>
            </div>
            <div class="field">
              <label for="lounge-tools">Tool calls a message</label>
              <InputNumber
                v-model="s.draft.value.toolCallsPerMessage"
                input-id="lounge-tools"
                :min="0"
                :max="5"
                show-buttons
                :invalid="!!error('toolCallsPerMessage')"
              />
              <small v-if="error('toolCallsPerMessage')" class="field-error">{{ error('toolCallsPerMessage') }}</small>
            </div>
          </div>
        </div>
      </section>

      <Message v-if="invalid" severity="warn" :closable="false" size="small"
        >See the marked fields before saving.</Message
      >
      <SectionActions
        :busy="s.busy.value || invalid"
        :dirty="s.dirty.value"
        :errors="s.errors.value"
        :placed="placed"
        @save="!invalid && s.save()"
        @reset="s.reset()"
      />
    </form>
    <SettingsHistory section="lounge" title="the lounge" @rolled-back="s.reload()" />
    <LoungeMemoryPanel />
    <PromptsPanel lounge />
    <LoungeScenesPanel />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 960px;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
}

.head-tag {
  margin-left: var(--pnmt-space-2);
}

.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

/* Two columns of fields as on the other tabs (one on a phone); a field never wider than its column (an InputNumber with buttons is wide by itself). */
.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}

.grid > * {
  min-width: 0;
}

.field :deep(.p-inputnumber),
.field :deep(.p-select),
.field > :deep(.p-inputtext) {
  width: 100%;
}

.field :deep(.p-inputnumber-input) {
  width: 100%;
  min-width: 0;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.label {
  font-weight: 600;
}

.facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 2px var(--pnmt-space-3);
  margin: 0;
}

.facts dt {
  font-weight: 600;
}

.facts dd {
  margin: 0;
}

.hours {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.day-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.day-name {
  width: 7rem;
  flex: none;
}

.periods {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.period {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.time {
  width: 4.5rem;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.rhythm {
  margin: 0;
  font-weight: 500;
}

.group {
  max-width: 24rem;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.gap p {
  margin: 0 0 var(--pnmt-space-1);
}

.conviction {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding-bottom: var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.history {
  margin: 0;
  padding-left: 1.1rem;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.cant-start {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
