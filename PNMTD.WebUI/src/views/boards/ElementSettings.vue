<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { boardsApi, type BoardCatalog, type BoardTargetOption } from '@/api/boards'
import { typeNames } from '@/boards/names'
import type { BoardElement, BoardElementSettings, BoardRedmineList, BoardRedmineTile } from '@/boards/types'
import { severityOptions } from '@/shared/zabbix'

/**
 * An element's settings (docs/boards-plan.md §7.1, §10.3): its title, height and the type's own settings; and moving it to another
 * column, or removing it. A type the editor may not change (§7.2) is shown locked.
 */
const props = defineProps<{
  element: BoardElement
  catalog: BoardCatalog | null
  columns: number
  column: number
  locked: string | null
}>()
const emit = defineEmits<{ change: [element: BoardElement]; move: [column: number]; remove: []; close: [] }>()

const e = computed(() => props.element)
const s = computed(() => props.element.settings)
function set<K extends keyof BoardElementSettings>(key: K, value: BoardElementSettings[K]) {
  emit('change', { ...props.element, settings: { ...props.element.settings, [key]: value } })
}
function setElement<K extends keyof BoardElement>(key: K, value: BoardElement[K]) {
  emit('change', { ...props.element, [key]: value })
}
const type = computed(() => props.element.type)
const has = (...types: BoardElement['type'][]) => types.includes(type.value)

const heights = [
  { value: 0, label: 'Its own height' },
  ...[1, 2, 3, 4, 5, 6].map((n) => ({ value: n, label: `${n} share${n > 1 ? 's' : ''} of the rest` })),
]
const importances = [
  { value: '', label: 'Any importance' },
  { value: 'Normal', label: 'Normal and High' },
  { value: 'High', label: 'High only' },
]
// PrimeVue's Select shows nothing for a null value: '' and 0 stand for "none" and become null again on the way out.
const locations = computed(() => [{ id: '', path: 'Everywhere' }, ...(props.catalog?.locations ?? [])])
const projects = computed(() => [
  { id: 0, label: 'Every project PNMT reads' },
  ...(props.catalog?.redmineProjects ?? []).map((p) => ({ id: p.id, label: p.parentId ? `· ${p.name}` : p.name })),
])
const redmineTiles: { value: BoardRedmineTile; label: string }[] = [
  { value: 'Open', label: 'Open' },
  { value: 'Urgent', label: 'Urgent' },
  { value: 'Unassigned', label: 'New, no one assigned' },
  { value: 'Overdue', label: 'Overdue' },
  { value: 'Stale', label: 'No update for n days' },
  { value: 'ReceivedToday', label: 'Received today' },
  { value: 'SolvedToday', label: 'Solved today' },
]
const redmineLists: { value: BoardRedmineList; label: string }[] = [
  { value: 'Urgent', label: 'Urgent' },
  { value: 'Unassigned', label: 'New, no one assigned' },
  { value: 'Overdue', label: 'Overdue' },
  { value: 'LinkedToProblem', label: 'Linked to a problem in PNMT' },
]
const columnOptions = computed(() =>
  Array.from({ length: props.columns }, (_, i) => ({ value: i, label: `Column ${i + 1}` })),
)

// --- status tiles: hosts and sensors by name -------------------------------------------------------------------------------
const names = ref(new Map<string, string>())
watch(
  () => (props.element.type === 'StatusTiles' ? (props.element.settings.targets ?? []).map((t) => t.id) : []),
  async (ids) => {
    const missing = ids.filter((id) => !names.value.has(id))
    if (!missing.length) return
    try {
      for (const option of await boardsApi.names(missing)) names.value.set(option.id, option.name)
      names.value = new Map(names.value)
    } catch {
      // Names are a convenience: the ids stay.
    }
  },
  { immediate: true },
)
const suggestions = ref<BoardTargetOption[]>([])
const pick = ref<BoardTargetOption | null>(null)
async function search(event: { query: string }) {
  try {
    suggestions.value = await boardsApi.targets(event.query)
  } catch {
    suggestions.value = []
  }
}
function addTarget(option: BoardTargetOption | null) {
  if (!option) return
  names.value = new Map(names.value).set(option.id, option.name)
  const targets = s.value.targets ?? []
  if (!targets.some((t) => t.id === option.id)) set('targets', [...targets, { kind: option.kind, id: option.id }])
  pick.value = null
}
const removeTarget = (id: string) =>
  set(
    'targets',
    (s.value.targets ?? []).filter((t) => t.id !== id),
  )

// --- the clock's other time zones -----------------------------------------------------------------------------------------
const zones = computed(() => s.value.zones ?? [])
const setZone = (i: number, key: 'zone' | 'label', value: string) =>
  set(
    'zones',
    zones.value.map((z, j) => (j === i ? { ...z, [key]: value } : z)),
  )
</script>

<template>
  <div class="element-settings">
    <div class="head">
      <Button icon="pi pi-angle-left" label="Layout" text size="small" severity="secondary" @click="emit('close')" />
      <strong>{{ typeNames[e.type] }}</strong>
    </div>
    <p v-if="locked" class="locked">
      <i class="pi pi-lock" aria-hidden="true" /> {{ locked }} It stays as it is; you can move or remove it.
    </p>
    <fieldset :disabled="!!locked" class="form">
      <div class="field">
        <label for="es-title">Title</label>
        <InputText
          id="es-title"
          :model-value="e.title ?? ''"
          :placeholder="typeNames[e.type]"
          @update:model-value="(v) => setElement('title', v?.trim() ? v : null)"
        />
        <label class="check"
          ><Checkbox :model-value="e.showTitle" binary @update:model-value="(v) => setElement('showTitle', v)" /> Show
          the title</label
        >
      </div>
      <div class="field">
        <label for="es-height">Height</label>
        <Select
          id="es-height"
          :model-value="e.height ?? 0"
          :options="heights"
          option-label="label"
          option-value="value"
          @update:model-value="(v) => setElement('height', v || null)"
        />
        <small class="field-hint"
          >Shares split the column's height that the other elements leave, when the board fits the window.</small
        >
      </div>

      <!-- PNMT's monitoring -->
      <div v-if="has('SensorSummary', 'FailingSensors')" class="field">
        <label>Importance</label>
        <Select
          :model-value="s.importance ?? ''"
          :options="importances"
          option-label="label"
          option-value="value"
          @update:model-value="(v) => set('importance', v || null)"
        />
      </div>
      <div v-if="has('SensorSummary', 'FailingSensors', 'HostBars', 'RecentChanges')" class="field">
        <label>Location</label>
        <Select
          :model-value="s.locationId ?? ''"
          :options="locations"
          option-label="path"
          option-value="id"
          filter
          @update:model-value="(v) => set('locationId', v || null)"
        />
        <small class="field-hint">With its sub-locations.</small>
      </div>
      <label v-if="has('SensorSummary')" class="check"
        ><Checkbox :model-value="s.ignored" binary @update:model-value="(v) => set('ignored', v)" /> Also ignored and
        off</label
      >
      <label v-if="has('FailingSensors')" class="check"
        ><Checkbox :model-value="s.messages" binary @update:model-value="(v) => set('messages', v)" /> Show the sensors'
        messages</label
      >
      <small v-if="has('FailingSensors') && s.messages" class="field-hint warn"
        >A message can say more than a wall should, such as a login that failed.</small
      >
      <template v-if="has('StatusTiles')">
        <div class="field">
          <label>Hosts and sensors</label>
          <AutoComplete
            v-model="pick"
            :suggestions="suggestions"
            option-label="name"
            placeholder="Search a host or a sensor"
            :delay="250"
            @complete="search"
            @option-select="(ev) => addTarget(ev.value)"
          />
          <ul class="targets">
            <li v-for="t in s.targets ?? []" :key="t.id">
              <i :class="t.kind === 'Host' ? 'pi pi-server' : 'pi pi-wave-pulse'" aria-hidden="true" />
              <span class="grow">{{ names.get(t.id) ?? t.id }}</span>
              <Button
                icon="pi pi-times"
                text
                size="small"
                severity="secondary"
                :aria-label="`Remove ${names.get(t.id) ?? 'it'}`"
                @click="removeTarget(t.id)"
              />
            </li>
          </ul>
        </div>
        <div class="field">
          <label>Tiles per row</label>
          <InputNumber
            :model-value="s.perRow ?? 3"
            :min="1"
            :max="8"
            show-buttons
            @update:model-value="(v) => set('perRow', v)"
          />
        </div>
      </template>
      <template v-if="has('HostBars')">
        <label class="check"
          ><Checkbox :model-value="s.onlyProblems" binary @update:model-value="(v) => set('onlyProblems', v)" /> Only
          hosts with a problem</label
        >
        <SelectButton
          :model-value="s.tiles"
          :options="[
            { value: false, label: 'Rows with bars' },
            { value: true, label: 'Tiles' },
          ]"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          @update:model-value="(v) => set('tiles', v)"
        />
      </template>

      <!-- Redmine -->
      <div v-if="has('RedmineOverview', 'RedmineNewest', 'RedmineReceived', 'RedmineSolved')" class="field">
        <label>Project</label>
        <Select
          :model-value="s.projectId ?? 0"
          :options="projects"
          option-label="label"
          option-value="id"
          @update:model-value="(v) => set('projectId', v || null)"
        />
      </div>
      <template v-if="has('RedmineOverview')">
        <div class="field">
          <label>Tiles</label>
          <MultiSelect
            :model-value="s.redmineTiles ?? redmineTiles.filter((t) => t.value !== 'Stale').map((t) => t.value)"
            :options="redmineTiles"
            option-label="label"
            option-value="value"
            display="chip"
            @update:model-value="(v) => set('redmineTiles', v)"
          />
        </div>
        <div class="field">
          <label>Lists</label>
          <MultiSelect
            :model-value="s.redmineLists ?? redmineLists.map((l) => l.value)"
            :options="redmineLists"
            option-label="label"
            option-value="value"
            display="chip"
            @update:model-value="(v) => set('redmineLists', v)"
          />
        </div>
      </template>
      <div v-if="has('RedmineNewest')" class="field">
        <label>Marked New for (minutes)</label>
        <InputNumber
          :model-value="s.newMinutes ?? 30"
          :min="0"
          :max="240"
          show-buttons
          @update:model-value="(v) => set('newMinutes', v)"
        />
      </div>
      <label v-if="has('RedmineOverview', 'RedmineNewest')" class="check"
        ><Checkbox :model-value="s.subjects" binary @update:model-value="(v) => set('subjects', v)" /> Show the subjects
        (private tickets never)</label
      >
      <template v-if="has('RedmineReceived', 'RedmineSolved')">
        <label class="check"
          ><Checkbox :model-value="s.yesterday" binary @update:model-value="(v) => set('yesterday', v)" /> Yesterday's
          number below it</label
        >
        <div class="field two">
          <div>
            <label>Yellow from</label>
            <InputNumber
              :model-value="s.yellow"
              :min="0"
              :max="9999"
              placeholder="never"
              @update:model-value="(v) => set('yellow', v)"
            />
          </div>
          <div>
            <label>Red from</label>
            <InputNumber
              :model-value="s.red"
              :min="0"
              :max="9999"
              placeholder="never"
              @update:model-value="(v) => set('red', v)"
            />
          </div>
        </div>
      </template>

      <!-- Zabbix -->
      <template v-if="has('ZabbixCount', 'ZabbixHistory')">
        <div class="field">
          <label>From the severity</label>
          <Select
            :model-value="s.severity ?? 'Warning'"
            :options="severityOptions"
            option-label="label"
            option-value="value"
            @update:model-value="(v) => set('severity', v)"
          />
        </div>
        <div class="field">
          <label>Host groups</label>
          <MultiSelect
            :model-value="s.groups ?? []"
            :options="catalog?.zabbixGroups ?? []"
            placeholder="Every group PNMT reads"
            display="chip"
            filter
            @update:model-value="(v) => set('groups', v.length ? v : null)"
          />
        </div>
        <label class="check"
          ><Checkbox :model-value="s.suppressed" binary @update:model-value="(v) => set('suppressed', v)" /> Count
          suppressed problems (maintenance)</label
        >
      </template>
      <div v-if="has('ZabbixHistory')" class="field">
        <label>Resolved within (hours)</label>
        <InputNumber
          :model-value="s.windowHours ?? 24"
          :min="1"
          :max="72"
          show-buttons
          @update:model-value="(v) => set('windowHours', v)"
        />
      </div>

      <!-- the lounge -->
      <label v-if="has('Lounge')" class="check"
        ><Checkbox :model-value="s.comments" binary @update:model-value="(v) => set('comments', v)" /> Comments
        too</label
      >

      <!-- general -->
      <template v-if="has('Clock')">
        <label class="check"
          ><Checkbox :model-value="s.seconds" binary @update:model-value="(v) => set('seconds', v)" /> Seconds</label
        >
        <label class="check"
          ><Checkbox :model-value="s.date" binary @update:model-value="(v) => set('date', v)" /> The date</label
        >
        <div class="field">
          <label>More time zones</label>
          <div v-for="(z, i) in zones" :key="i" class="zone">
            <InputText
              :model-value="z.label"
              placeholder="New York"
              :aria-label="`Label ${i + 1}`"
              @update:model-value="(v) => setZone(i, 'label', v ?? '')"
            />
            <InputText
              :model-value="z.zone"
              placeholder="America/New_York"
              class="mono"
              :aria-label="`Time zone ${i + 1}`"
              @update:model-value="(v) => setZone(i, 'zone', v ?? '')"
            />
            <Button
              icon="pi pi-times"
              text
              size="small"
              severity="secondary"
              aria-label="Remove"
              @click="
                set(
                  'zones',
                  zones.filter((_, j) => j !== i),
                )
              "
            />
          </div>
          <Button
            v-if="zones.length < 3"
            label="Add a time zone"
            icon="pi pi-plus"
            text
            size="small"
            @click="set('zones', [...zones, { zone: '', label: '' }])"
          />
        </div>
      </template>
      <template v-if="has('Note')">
        <div class="field">
          <label for="es-note">Text (Markdown)</label>
          <Textarea
            id="es-note"
            :model-value="s.text ?? ''"
            rows="8"
            auto-resize
            :maxlength="4000"
            @update:model-value="(v) => set('text', v)"
          />
          <small class="field-hint">Without images or HTML. Anyone who opens the board reads it.</small>
        </div>
        <label class="check"
          ><Checkbox :model-value="s.large" binary @update:model-value="(v) => set('large', v)" /> Large</label
        >
        <label class="check"
          ><Checkbox :model-value="s.center" binary @update:model-value="(v) => set('center', v)" /> Centred</label
        >
      </template>
      <div v-if="has('Certificates')" class="field">
        <label>Expiring within (days)</label>
        <InputNumber
          :model-value="s.days ?? 30"
          :min="1"
          :max="90"
          show-buttons
          @update:model-value="(v) => set('days', v)"
        />
      </div>

      <div
        v-if="
          has(
            'FailingSensors',
            'HostBars',
            'RecentChanges',
            'RedmineOverview',
            'RedmineNewest',
            'ZabbixHistory',
            'Lounge',
            'Certificates',
          )
        "
        class="field"
      >
        <label>{{ has('RedmineOverview') ? 'Tickets per list' : has('Lounge') ? 'Lines' : 'Rows' }}</label>
        <InputNumber
          :model-value="s.rows"
          :min="has('Lounge') ? 5 : 1"
          :max="
            has('RedmineOverview')
              ? 10
              : has('RedmineNewest')
                ? 20
                : has('RecentChanges')
                  ? 15
                  : has('HostBars')
                    ? 200
                    : 50
          "
          placeholder="the default"
          show-buttons
          @update:model-value="(v) => set('rows', v)"
        />
        <small class="field-hint">Fit to window, a list shows the rows that fit and how many more there are.</small>
      </div>
    </fieldset>
    <div class="foot">
      <Select
        v-if="columns > 1"
        :model-value="column"
        :options="columnOptions"
        option-label="label"
        option-value="value"
        size="small"
        aria-label="Move to column"
        @update:model-value="(v) => emit('move', v)"
      />
      <Button label="Remove" icon="pi pi-trash" severity="danger" text size="small" @click="emit('remove')" />
    </div>
  </div>
</template>

<style scoped>
.element-settings {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

fieldset {
  border: none;
  margin: 0;
  padding: 0;
  min-width: 0;
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.two {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-2);
}

.two > div {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.targets {
  list-style: none;
  margin: 4px 0 0;
  padding: 0;
}

.targets li,
.zone {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.zone {
  margin-bottom: 4px;
}

.grow {
  flex: 1;
  min-width: 0;
}

.locked,
.warn {
  color: var(--pnmt-warn);
}

.foot {
  display: flex;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
}
</style>
