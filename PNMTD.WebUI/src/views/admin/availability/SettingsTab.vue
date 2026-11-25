<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ColorField from '@/components/ColorField.vue'
import ErrorState from '@/components/ErrorState.vue'
import { useNotify } from '@/composables/useNotify'
import { newId, withUnknown } from '@/views/admin/cookie/cookieAdmin'
import SectionActions from '@/views/admin/cookie/SectionActions.vue'
import CodeChip from './CodeChip.vue'
import HoursEditor from './HoursEditor.vue'
import { normalizeWeek, tidyPeriods } from './hours'
import { flags, isColour, works, type Flag } from './plan'
import { settingsKey, useAvailabilityPeople, useAvailabilitySettings } from './useAvailability'

/**
 * The plan's settings (docs/cookie-plan.md §8.2, §9.4), for Availability: Manage, saved as a whole: the availability types
 * with what they mean as checkboxes (several at once, never checked against each other), exactly one the default and at
 * most one for the days a person doesn't work; the count columns; the working hours and holidays (§7.3, Admin → Cookie
 * before revision 7); and the global fallback. A type left out that is in use is retired, keeping its cells.
 */
interface Draft {
  types: S['AvailabilityTypeInput'][]
  settings: {
    week: S['DayHours'][]
    holidays: S['Holiday'][]
    countColumns: S['CountColumn'][]
    globalFallbackUserId: string | null
    globalFallbackGroupId: string | null
  }
}
type Fallback = 'none' | 'user' | 'group'

const queryClient = useQueryClient()
const notify = useNotify()
const settings = useAvailabilitySettings()
const people = useAvailabilityPeople()
/** The groups to pick from, as the settings name them (for Availability: Manage too); a removed one shows as such. */
const groupChoices = computed(() => (settings.data.value?.groups ?? []).map((g) => ({ label: g.name, value: g.id })))
const removedGroup = () => 'A removed group'
const draft = ref<Draft | null>(null)
const fallback = ref<Fallback>('none')
const base = ref('')
const busy = ref(false)
const errors = ref<Record<string, string[]>>({})

const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T
const snapshot = () => JSON.stringify({ draft: draft.value, fallback: fallback.value })

function load(item: S['AvailabilitySettingsItem']) {
  const s = item.settings
  draft.value = {
    types: [...item.types]
      .filter((t) => !t.retired)
      .sort((a, b) => a.order - b.order)
      .map((t) => ({
        id: t.id,
        name: t.name,
        code: t.code,
        color: t.color,
        flags: [...t.flags],
        isDefault: t.isDefault,
        isDayOff: t.isDayOff,
      })),
    settings: {
      week: normalizeWeek(s.week ?? []),
      holidays: clone(s.holidays ?? []),
      countColumns: (s.countColumns ?? []).map((c) => ({
        id: c.id,
        name: c.name,
        flags: [...c.flags],
        groupId: c.groupId ?? null,
        minimum: c.minimum ?? null,
        shown: c.shown ?? true,
      })),
      globalFallbackUserId: s.globalFallbackUserId ?? null,
      globalFallbackGroupId: s.globalFallbackGroupId ?? null,
    },
  }
  fallback.value = s.globalFallbackUserId ? 'user' : s.globalFallbackGroupId ? 'group' : 'none'
  base.value = snapshot()
}

// A refetch replaces the draft only while it is unchanged, so it never throws away an edit.
watch(
  () => settings.data.value,
  (item) => {
    if (item && (draft.value === null || snapshot() === base.value)) load(item)
  },
  { immediate: true },
)
const dirty = computed(() => draft.value !== null && snapshot() !== base.value)
const retired = computed(() => (settings.data.value?.types ?? []).filter((t) => t.retired))

// --- the types ------------------------------------------------------------------------------------------------------
function addType() {
  draft.value?.types.push({
    id: null,
    name: '',
    code: '',
    color: '#e5e7eb',
    flags: [],
    isDefault: !draft.value.types.length,
    isDayOff: false,
  })
}
function removeType(i: number) {
  draft.value?.types.splice(i, 1)
}
function moveRow<T>(list: T[], i: number, by: -1 | 1) {
  const j = i + by
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j], list[i]]
}
function toggle(list: Flag[], flag: Flag, on: boolean) {
  const i = list.indexOf(flag)
  if (on && i < 0) list.push(flag)
  if (!on && i >= 0) list.splice(i, 1)
}
const defaultIndex = computed({
  get: () => draft.value?.types.findIndex((t) => t.isDefault) ?? -1,
  set: (i: number) => draft.value?.types.forEach((t, j) => (t.isDefault = j === i)),
})
function setDayOff(i: number, on: boolean) {
  draft.value?.types.forEach((t, j) => (t.isDayOff = on && j === i))
}
const upper = (t: S['AvailabilityTypeInput'], value: string | undefined) => (t.code = (value ?? '').toUpperCase())

/** The server's checks on a type, as a hint while typing, and its last answer. */
function typeProblems(t: S['AvailabilityTypeInput'], i: number): string[] {
  const all = draft.value?.types ?? []
  const code = t.code.trim().toUpperCase()
  const problems: string[] = []
  if (!t.name.trim()) problems.push('A name.')
  if (!/^[A-Z0-9]{2,4}$/.test(code)) problems.push('A code of 2 to 4 letters or digits.')
  else if (all.filter((x) => x.code.trim().toUpperCase() === code).length > 1) problems.push('Each code once.')
  if (!isColour(t.color)) problems.push('A colour as #rrggbb.')
  return [...new Set([...problems, ...(errors.value[`types.${i}`] ?? [])])]
}
const typesProblems = computed(() => {
  const all = draft.value?.types ?? []
  const problems: string[] = []
  if (all.filter((t) => t.isDefault).length !== 1) problems.push('Exactly one type is the default.')
  return [...new Set([...problems, ...(errors.value.types ?? [])])]
})

// --- the count columns ----------------------------------------------------------------------------------------------
function addColumn() {
  draft.value?.settings.countColumns.push({
    id: newId(),
    name: '',
    flags: ['OnSite'],
    groupId: null,
    minimum: null,
    shown: true,
  })
}
function removeColumn(i: number) {
  draft.value?.settings.countColumns.splice(i, 1)
}
// Everyone is no group (null), which the select shows as its placeholder.
const groupOptions = computed(() => [
  { label: 'Everyone', value: null as string | null },
  ...withUnknown(
    groupChoices.value,
    (draft.value?.settings.countColumns ?? []).map((c) => c.groupId),
    removedGroup,
  ),
])
function columnProblems(c: S['CountColumn']): string[] {
  const problems: string[] = []
  if (!c.name.trim()) problems.push('A name.')
  if (!c.flags.length) problems.push('Choose at least one checkbox to count.')
  return [...new Set([...problems, ...(errors.value[`countColumns.${c.id}`] ?? [])])]
}

// --- the global fallback --------------------------------------------------------------------------------------------
const fallbackModes = [
  { label: 'Nobody', value: 'none' },
  { label: 'A user', value: 'user' },
  { label: 'A group', value: 'group' },
]
const userOptions = computed(() =>
  withUnknown(
    (people.data.value ?? []).map((u) => ({
      label: u.displayName && u.displayName !== u.username ? `${u.displayName} (${u.username})` : u.username,
      value: u.id,
    })),
    [draft.value?.settings.globalFallbackUserId],
    () => 'A removed user',
  ),
)
const fallbackGroupOptions = computed(() =>
  withUnknown(groupChoices.value, [draft.value?.settings.globalFallbackGroupId], removedGroup),
)

// --- saving ---------------------------------------------------------------------------------------------------------
function request(d: Draft): S['AvailabilitySettingsRequest'] {
  const order = (list: readonly Flag[]) => flags.map((f) => f.value).filter((f) => list.includes(f))
  return {
    types: d.types.map((t) => ({
      id: t.id,
      name: t.name.trim(),
      code: t.code.trim().toUpperCase(),
      color: t.color.trim().toLowerCase(),
      flags: order(t.flags),
      isDefault: t.isDefault,
      isDayOff: t.isDayOff,
    })),
    settings: {
      week: d.settings.week.map((w) => ({ day: w.day, periods: tidyPeriods(w.periods) })),
      holidays: [...d.settings.holidays]
        .sort((a, b) => a.date.localeCompare(b.date))
        .map((h) => ({ date: h.date, name: h.name.trim(), periods: tidyPeriods(h.periods) })),
      countColumns: d.settings.countColumns.map((c) => ({
        id: c.id,
        name: c.name.trim(),
        flags: order(c.flags),
        groupId: c.groupId ?? null,
        minimum: c.minimum ?? null,
        shown: c.shown,
      })),
      globalFallbackUserId: fallback.value === 'user' ? d.settings.globalFallbackUserId : null,
      globalFallbackGroupId: fallback.value === 'group' ? d.settings.globalFallbackGroupId : null,
    },
  }
}

async function save() {
  if (!draft.value || !dirty.value) return
  busy.value = true
  errors.value = {}
  try {
    const saved = await api.availability.saveSettings(request(draft.value))
    queryClient.setQueryData(settingsKey, saved)
    load(saved)
    notify.success('Saved', 'The plan uses the new settings at once.')
    await queryClient.invalidateQueries({ queryKey: keys.availability })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = e.errors
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}

function reset() {
  if (settings.data.value) load(settings.data.value)
  errors.value = {}
}

const placed = (f: string) =>
  ['types', 'countColumns', 'week', 'holidays', 'globalFallback'].some((p) => f === p || f.startsWith(`${p}.`))
</script>

<template>
  <ErrorState
    v-if="settings.isError.value && !draft"
    :error="settings.error.value"
    what="the settings"
    @retry="settings.refetch()"
  />
  <div v-else-if="draft" class="tab">
    <section class="panel" aria-labelledby="types-title">
      <div id="types-title" class="panel-header">
        Availability types
        <Button label="Add" icon="pi pi-plus" size="small" severity="secondary" text class="end" @click="addType" />
      </div>
      <div class="panel-body body">
        <p class="muted small intro">
          What a type means is its checkboxes, and several may be checked: vacation with holiday is fine. A person works
          on a day whose type is on site, work travel or home office, and none of vacation, holiday or compensation; a
          type with none checked means not working. The fill writes the default on working days and the type for days
          off on the others.
        </p>
        <div v-if="typesProblems.length" class="field-error" role="alert">{{ typesProblems.join(' ') }}</div>
        <div class="wide-table">
          <table class="rows">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Code</th>
                <th scope="col">Colour</th>
                <th v-for="f in flags" :key="f.value" scope="col" class="flag">{{ f.label }}</th>
                <th scope="col" class="flag">Default</th>
                <th scope="col" class="flag">Days off</th>
                <th scope="col">Means</th>
                <th scope="col"><span class="sr-only">Order</span></th>
              </tr>
            </thead>
            <tbody>
              <template v-for="(t, i) in draft.types" :key="t.id ?? `new-${i}`">
                <tr>
                  <td>
                    <InputText
                      v-model="t.name"
                      size="small"
                      class="type-name"
                      maxlength="60"
                      placeholder="Home office"
                      :aria-label="`Type ${i + 1}, name`"
                    />
                  </td>
                  <td>
                    <span class="code-cell">
                      <InputText
                        :model-value="t.code"
                        size="small"
                        class="type-code mono"
                        maxlength="4"
                        placeholder="HOME"
                        :aria-label="`Type ${i + 1}, code`"
                        @update:model-value="(v) => upper(t, v)"
                      />
                      <CodeChip :code="t.code" :colour="t.color" />
                    </span>
                  </td>
                  <td>
                    <ColorField
                      :model-value="t.color"
                      :label="`Type ${i + 1}, colour`"
                      @update:model-value="(v) => (t.color = v ?? '')"
                    />
                  </td>
                  <td v-for="f in flags" :key="f.value" class="flag">
                    <Checkbox
                      :model-value="t.flags.includes(f.value)"
                      binary
                      :aria-label="`Type ${i + 1}, ${f.label}`"
                      @update:model-value="(on: boolean) => toggle(t.flags, f.value, on)"
                    />
                  </td>
                  <td class="flag">
                    <RadioButton
                      v-model="defaultIndex"
                      :value="i"
                      name="default-type"
                      :aria-label="`Type ${i + 1} is the default`"
                    />
                  </td>
                  <td class="flag">
                    <Checkbox
                      :model-value="t.isDayOff"
                      binary
                      :aria-label="`Type ${i + 1} is for days off`"
                      @update:model-value="(on: boolean) => setDayOff(i, on)"
                    />
                  </td>
                  <td class="muted small nowrap">{{ works(t.flags) ? 'works' : 'not working' }}</td>
                  <td class="nowrap">
                    <Button
                      icon="pi pi-arrow-up"
                      size="small"
                      severity="secondary"
                      text
                      :disabled="i === 0"
                      :aria-label="`Move type ${i + 1} up`"
                      @click="moveRow(draft.types, i, -1)"
                    />
                    <Button
                      icon="pi pi-arrow-down"
                      size="small"
                      severity="secondary"
                      text
                      :disabled="i === draft.types.length - 1"
                      :aria-label="`Move type ${i + 1} down`"
                      @click="moveRow(draft.types, i, 1)"
                    />
                    <Button
                      icon="pi pi-trash"
                      size="small"
                      severity="secondary"
                      text
                      :aria-label="`Remove type ${i + 1}`"
                      @click="removeType(i)"
                    />
                  </td>
                </tr>
                <tr v-if="typeProblems(t, i).length" class="problem-row">
                  <td :colspan="flags.length + 7" class="field-error" role="alert">
                    {{ typeProblems(t, i).join(' ') }}
                  </td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>
        <p v-if="retired.length" class="muted small intro">
          Retired, kept for the cells that have them:
          <template v-for="(t, i) in retired" :key="t.id"
            ><template v-if="i">, </template>{{ t.name }} ({{ t.code }})</template
          >. Removing a type that is in use retires it.
        </p>
      </div>
    </section>

    <section class="panel" aria-labelledby="columns-title">
      <div id="columns-title" class="panel-header">
        Count columns
        <Button
          label="Add"
          icon="pi pi-plus"
          size="small"
          severity="secondary"
          text
          class="end"
          :disabled="draft.settings.countColumns.length >= 12"
          @click="addColumn"
        />
      </div>
      <div class="panel-body body">
        <p class="muted small intro">
          Each counts the people of a day whose type has any of its checkboxes: everyone in the plan, or a group's
          members. Below its minimum the cell turns red, and today's count below it gives the status Info.
        </p>
        <div v-if="errors.countColumns?.length" class="field-error" role="alert">
          {{ errors.countColumns.join(' ') }}
        </div>
        <p v-if="!draft.settings.countColumns.length" class="muted">None.</p>
        <div v-else class="wide-table">
          <table class="rows">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Counts</th>
                <th scope="col">People</th>
                <th scope="col">Minimum</th>
                <th scope="col" class="flag">Shown</th>
                <th scope="col"><span class="sr-only">Order</span></th>
              </tr>
            </thead>
            <tbody>
              <template v-for="(c, i) in draft.settings.countColumns" :key="c.id">
                <tr>
                  <td>
                    <InputText
                      v-model="c.name"
                      size="small"
                      maxlength="40"
                      placeholder="On site"
                      :aria-label="`Count column ${i + 1}, name`"
                    />
                  </td>
                  <td>
                    <MultiSelect
                      v-model="c.flags"
                      :options="flags"
                      option-label="label"
                      option-value="value"
                      display="chip"
                      size="small"
                      class="column-flags"
                      placeholder="Nothing"
                      :aria-label="`Count column ${i + 1}, counts`"
                    />
                  </td>
                  <td>
                    <Select
                      v-model="c.groupId"
                      :options="groupOptions"
                      option-label="label"
                      option-value="value"
                      size="small"
                      class="column-group"
                      placeholder="Everyone"
                      :aria-label="`Count column ${i + 1}, people`"
                    />
                  </td>
                  <td>
                    <InputNumber
                      v-model="c.minimum"
                      :min="0"
                      :max="1000"
                      size="small"
                      class="minimum"
                      placeholder="none"
                      :aria-label="`Count column ${i + 1}, minimum`"
                    />
                  </td>
                  <td class="flag">
                    <Checkbox v-model="c.shown" binary :aria-label="`Count column ${i + 1}, shown`" />
                  </td>
                  <td class="nowrap">
                    <Button
                      icon="pi pi-arrow-up"
                      size="small"
                      severity="secondary"
                      text
                      :disabled="i === 0"
                      :aria-label="`Move count column ${i + 1} up`"
                      @click="moveRow(draft.settings.countColumns, i, -1)"
                    />
                    <Button
                      icon="pi pi-arrow-down"
                      size="small"
                      severity="secondary"
                      text
                      :disabled="i === draft.settings.countColumns.length - 1"
                      :aria-label="`Move count column ${i + 1} down`"
                      @click="moveRow(draft.settings.countColumns, i, 1)"
                    />
                    <Button
                      icon="pi pi-trash"
                      size="small"
                      severity="secondary"
                      text
                      :aria-label="`Remove count column ${i + 1}`"
                      @click="removeColumn(i)"
                    />
                  </td>
                </tr>
                <tr v-if="columnProblems(c).length" class="problem-row">
                  <td colspan="6" class="field-error" role="alert">{{ columnProblems(c).join(' ') }}</td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <HoursEditor v-model:week="draft.settings.week" v-model:holidays="draft.settings.holidays" :errors="errors" />

    <section class="panel" aria-labelledby="fallback-title">
      <div id="fallback-title" class="panel-header">Global fallback</div>
      <div class="panel-body form">
        <p class="muted small intro">
          Who is reached in working hours when neither the day's responsible nor its fallback works: a user, or a
          group's members who work that day. With nobody left, a message waits in the outbox as nobody to reach.
        </p>
        <SelectButton
          v-model="fallback"
          :options="fallbackModes"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-label="Global fallback"
        />
        <Select
          v-if="fallback === 'user'"
          v-model="draft.settings.globalFallbackUserId"
          :options="userOptions"
          option-label="label"
          option-value="value"
          filter
          placeholder="Choose a user"
          class="fallback"
          aria-label="Global fallback, user"
        />
        <Select
          v-else-if="fallback === 'group'"
          v-model="draft.settings.globalFallbackGroupId"
          :options="fallbackGroupOptions"
          option-label="label"
          option-value="value"
          filter
          placeholder="Choose a group"
          class="fallback"
          aria-label="Global fallback, group"
        />
        <small v-if="errors.globalFallback" class="field-error">{{ errors.globalFallback.join(' ') }}</small>
      </div>
    </section>

    <SectionActions :busy="busy" :dirty="dirty" :errors="errors" :placed="placed" @save="save" @reset="reset" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 1200px;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.intro {
  margin: 0;
  max-width: 90ch;
}

.end {
  margin-left: auto;
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
  padding: var(--pnmt-space-1);
  border-bottom: 1px solid var(--pnmt-border);
  white-space: nowrap;
}

.rows td {
  padding: var(--pnmt-space-1);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: middle;
}

.rows .problem-row td {
  border-bottom: 1px solid var(--pnmt-border);
  padding-top: 0;
}

.flag {
  text-align: center;
}

.type-name {
  width: 11rem;
}

.type-code {
  width: 5rem;
  text-transform: uppercase;
}

.code-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.column-flags {
  width: 18rem;
}

.column-group {
  width: 12rem;
}

.minimum :deep(input) {
  width: 5rem;
}

.fallback {
  width: min(24rem, 100%);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

.small {
  font-size: 0.857rem;
}
</style>
