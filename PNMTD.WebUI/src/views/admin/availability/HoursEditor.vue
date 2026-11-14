<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import type { S } from '@/api/resources'
import { formatDay } from '@/format'
import { holidayProblems, maxPeriods, nextPeriod, periodProblems } from './hours'

/**
 * The working hours and holidays (docs/cookie-plan.md §7.3), in the availability plan's settings since revision 7: one or
 * more periods for each weekday, none is closed; and holidays, whose hours replace their weekday's (none: closed). The
 * first answer's hours count only working time; reminders outside it wait for the next period. In PNMT's time zone. The
 * week comes with every weekday once, Monday first (normalizeWeek); errors are the server's, by field.
 */
const props = defineProps<{ errors: Record<string, string[]> }>()
const week = defineModel<S['DayHours'][]>('week', { required: true })
const holidays = defineModel<S['Holiday'][]>('holidays', { required: true })

function addPeriod(periods: S['HoursPeriod'][]) {
  periods.push(nextPeriod(periods))
}
const addHoliday = () => holidays.value.push({ date: '', name: '', periods: [] })
const removeHoliday = (index: number) => holidays.value.splice(index, 1)
/** The client's checks and the server's last answer for a day or a holiday, each message once. */
const unique = (list: string[]) => [...new Set(list)]
const dayProblems = (day: S['DayHours']) =>
  unique([...periodProblems(day.periods), ...(props.errors[`week.${day.day}`] ?? [])])
const holidayIssues = (h: S['Holiday']) =>
  unique([...holidayProblems(h, holidays.value), ...(props.errors[`holidays.${h.date}`] ?? [])])
const general = (field: string) => props.errors[field] ?? []
</script>

<template>
  <section class="panel" aria-labelledby="hours-week-title">
    <div id="hours-week-title" class="panel-header">Working hours</div>
    <div class="panel-body body">
      <p class="muted small intro">
        From–to as HH:mm in PNMT's time zone; 24:00 ends a day. A day without periods is closed, and greyed in the plan.
        The 4 hours for a ticket's first answer count only these; the 14 days for an update count every day. Outside
        them, Cookie reaches nobody.
      </p>
      <div v-if="general('week').length" class="field-error" role="alert">{{ general('week').join(' ') }}</div>
      <div v-for="day in week" :key="day.day" class="row">
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
            :aria-label="`Add a period on ${day.day}`"
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
        aria-label="Add a holiday"
        @click="addHoliday"
      />
    </div>
    <div class="panel-body body">
      <p class="muted small intro">
        A holiday's hours replace its weekday's: none is closed, or special hours such as 08:00–12:00 on 24 Dec. The
        plan shows its name under the date.
      </p>
      <div v-if="general('holidays').length" class="field-error" role="alert">
        {{ general('holidays').join(' ') }}
      </div>
      <p v-if="!holidays.length" class="muted">None.</p>
      <div v-for="(h, index) in holidays" :key="index" class="row holiday">
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
        <div v-if="holidayIssues(h).length" class="problems field-error" role="alert">
          {{ holidayIssues(h).join(' ') }}
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
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
