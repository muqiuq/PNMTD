<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import type { S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { formatDay } from '@/format'
import { holidayProblems, maxPeriods, nextPeriod, normalizeWeek, periodProblems, tidyPeriods } from './hours'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useSection } from './useSection'

/**
 * Working hours (docs/cookie-plan.md §7.3): one or more periods for each weekday, none is closed; and holidays, whose
 * hours replace their weekday's (none: closed). The first answer's hours count only working time; reminders outside
 * it wait for the next period. In PNMT's time zone.
 */
const s = useSection('hours', {
  normalize: (v) => ({ ...v, week: normalizeWeek(v.week) }),
  prepare: (v) => ({
    week: v.week.map((d) => ({ day: d.day, periods: tidyPeriods(d.periods) })),
    holidays: [...v.holidays]
      .sort((a, b) => a.date.localeCompare(b.date))
      .map((h) => ({ date: h.date, name: h.name.trim(), periods: tidyPeriods(h.periods) })),
  }),
})

function addPeriod(periods: S['HoursPeriod'][]) {
  periods.push(nextPeriod(periods))
}
function addHoliday() {
  s.draft.value?.holidays.push({ date: '', name: '', periods: [] })
}
function removeHoliday(index: number) {
  s.draft.value?.holidays.splice(index, 1)
}
/** The client's checks and the server's last answer for a day or a holiday, each message once. */
const unique = (list: string[]) => [...new Set(list)]
const dayProblems = (day: S['DayHours']) =>
  unique([...periodProblems(day.periods), ...(s.errors.value[`week.${day.day}`] ?? [])])
const holidayIssues = (h: S['Holiday'], all: S['Holiday'][]) =>
  unique([...holidayProblems(h, all), ...(s.errors.value[`holidays.${h.date}`] ?? [])])
const placed = (f: string) => f.startsWith('week.') || f.startsWith('holidays.')
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
      <section class="panel" aria-labelledby="hours-week-title">
        <div id="hours-week-title" class="panel-header">The week</div>
        <div class="panel-body body">
          <p class="muted small intro">
            From–to as HH:mm in PNMT's time zone; 24:00 ends a day. A day without periods is closed. The 4 hours for a
            first answer count only these; the 14 days for an update count every day.
          </p>
          <div v-for="day in s.draft.value.week" :key="day.day" class="row">
            <div class="name">{{ day.day }}</div>
            <div class="periods">
              <span v-if="!day.periods.length" class="muted">closed</span>
              <span v-for="(p, i) in day.periods" :key="i" class="period">
                <InputText
                  v-model="p.from"
                  size="small"
                  class="time mono"
                  placeholder="08:00"
                  maxlength="5"
                  :aria-label="`${day.day}, period ${i + 1}, from`"
                />
                <span>–</span>
                <InputText
                  v-model="p.to"
                  size="small"
                  class="time mono"
                  placeholder="17:00"
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
                @click="addPeriod(day.periods)"
              />
            </div>
            <div v-if="dayProblems(day).length" class="problems field-error" role="alert">
              {{ dayProblems(day).join(' ') }}
            </div>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="hours-holidays-title">
        <div id="hours-holidays-title" class="panel-header">
          Holidays
          <Button
            label="Add"
            icon="pi pi-plus"
            size="small"
            severity="secondary"
            text
            class="end"
            @click="addHoliday"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            A holiday's hours replace its weekday's: none is closed, or special hours such as 08:00–12:00 on 24 Dec.
          </p>
          <p v-if="!s.draft.value.holidays.length" class="muted">None.</p>
          <div v-for="(h, index) in s.draft.value.holidays" :key="index" class="row holiday">
            <div class="name">
              <InputText v-model="h.date" type="date" size="small" :aria-label="`Holiday ${index + 1}, date`" />
              <InputText
                v-model="h.name"
                size="small"
                placeholder="Name"
                class="holiday-name"
                :aria-label="`Holiday ${index + 1}, name`"
              />
              <span v-if="h.date" class="muted small">{{ formatDay(h.date) }}</span>
            </div>
            <div class="periods">
              <span v-if="!h.periods.length" class="muted">closed</span>
              <span v-for="(p, i) in h.periods" :key="i" class="period">
                <InputText
                  v-model="p.from"
                  size="small"
                  class="time mono"
                  placeholder="08:00"
                  maxlength="5"
                  :aria-label="`Holiday ${index + 1}, period ${i + 1}, from`"
                />
                <span>–</span>
                <InputText
                  v-model="p.to"
                  size="small"
                  class="time mono"
                  placeholder="12:00"
                  maxlength="5"
                  :aria-label="`Holiday ${index + 1}, period ${i + 1}, to`"
                />
                <Button
                  icon="pi pi-times"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Remove the holiday's period ${i + 1}`"
                  @click="h.periods.splice(i, 1)"
                />
              </span>
              <Button
                v-if="h.periods.length < maxPeriods"
                label="Add hours"
                icon="pi pi-plus"
                size="small"
                severity="secondary"
                text
                @click="addPeriod(h.periods)"
              />
              <Button
                icon="pi pi-trash"
                size="small"
                severity="secondary"
                text
                class="end"
                :aria-label="`Remove holiday ${index + 1}`"
                @click="removeHoliday(index)"
              />
            </div>
            <div v-if="holidayIssues(h, s.draft.value.holidays).length" class="problems field-error" role="alert">
              {{ holidayIssues(h, s.draft.value.holidays).join(' ') }}
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
      <SettingsHistory section="hours" title="the working hours" @rolled-back="s.reload()" />
    </template>
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
  gap: var(--pnmt-space-2);
}

.intro {
  margin: 0;
}

.row {
  display: grid;
  grid-template-columns: 8rem minmax(0, 1fr);
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  align-items: center;
  padding: var(--pnmt-space-1) 0;
  border-bottom: 1px solid var(--pnmt-border);
}

.row:last-child {
  border-bottom: none;
}

.holiday {
  grid-template-columns: minmax(0, 1fr);
}

.name {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  font-weight: 500;
}

.holiday-name {
  width: 16rem;
}

.periods {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}

.period {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.time {
  width: 4.5rem;
  text-align: center;
}

.problems {
  grid-column: 1 / -1;
}

.end {
  margin-left: auto;
}

.small {
  font-size: 0.857rem;
}
</style>
