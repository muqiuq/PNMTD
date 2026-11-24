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
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { boardImageUrl, boardKeys, boardsApi, type BoardCatalog, type BoardTargetOption } from '@/api/boards'
import { typeNames } from '@/boards/names'
import type {
  BoardDateLanguage,
  BoardElement,
  BoardElementSettings,
  BoardPeople,
  BoardRedmineList,
  BoardRedmineTile,
  BoardStateColour,
  BoardTextSize,
} from '@/boards/types'
import { formatBytes } from '@/format/bytes'
import { severityOptions } from '@/shared/zabbix'
import { boardImageTypes, uploadBoardImage } from './boardImageUpload'
import { clockTimeFormats, dateFormats } from './timeOptions'

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
  /** The board's language of day and month names, for the date formats' examples. */
  dateLanguage?: BoardDateLanguage | null
  /** How the board names people: with names hidden, "On duty today" says it shows its labels only. */
  people?: BoardPeople | null
  /** The board's id: an image is uploaded for it (docs/board-messages-plan.md §9.4). */
  boardId?: string | null
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
  () =>
    props.element.type === 'StatusTiles' || props.element.type === 'SensorMessage'
      ? (props.element.settings.targets ?? []).map((t) => t.id)
      : [],
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
    const found = await boardsApi.targets(event.query)
    // A sensor's message: sensors only.
    suggestions.value = props.element.type === 'SensorMessage' ? found.filter((o) => o.kind === 'Sensor') : found
  } catch {
    suggestions.value = []
  }
}
/** A sensor's message has one sensor: picking another replaces it. */
function pickSensor(option: BoardTargetOption | null) {
  if (!option) return
  names.value = new Map(names.value).set(option.id, option.name)
  set('targets', [{ kind: option.kind, id: option.id }])
  pick.value = null
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

// --- the clock's own time and date format ----------------------------------------------------------------------------------
const clockDateFormats = computed(() => [
  { value: '', label: 'As the board' },
  ...dateFormats(props.catalog?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone, {
    dateLanguage: props.dateLanguage ?? 'Browser',
  }),
])
// --- a sensor's message ---------------------------------------------------------------------------------------------------
const messageSizes: { value: BoardTextSize; label: string }[] = [
  { value: 'Small', label: 'Small' },
  { value: 'Medium', label: 'Medium' },
  { value: 'Large', label: 'Large' },
  { value: 'Huge', label: 'Huge' },
  { value: 'Fit', label: 'Fit' },
]
const stateColours: { value: BoardStateColour; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'Bar', label: 'A bar' },
  { value: 'Background', label: 'Background' },
]
const sensorId = computed(() => s.value.targets?.[0]?.id ?? null)

// --- an image --------------------------------------------------------------------------------------------------------------
const queries = useQueryClient()
const imageList = useQuery({
  queryKey: computed(() => boardKeys.images(props.boardId ?? '')),
  queryFn: () => boardsApi.images(props.boardId!),
  enabled: computed(() => props.element.type === 'Image' && !!props.boardId),
})
const boardImages = computed(() => imageList.data.value ?? [])
const fileInput = ref<HTMLInputElement | null>(null)
const uploading = ref<number | null>(null)
const uploadError = ref<string | null>(null)
const dragging = ref(false)
const maxImage = computed(() => formatBytes(props.catalog?.maxImageBytes ?? 10 * 1024 * 1024))
const imageFits = [
  { value: 'Contain', label: 'All of it' },
  { value: 'Cover', label: 'Fill, cropped' },
  { value: 'Original', label: 'Its own size' },
]
const backgrounds = [
  { value: 'None', label: 'None' },
  { value: 'Black', label: 'Black' },
  { value: 'White', label: 'White' },
]
async function uploadImage(file: File | null | undefined) {
  if (!file || !props.boardId) return
  uploadError.value = null
  if (!boardImageTypes.includes(file.type)) {
    uploadError.value = `${file.name} isn't a PNG, JPEG, WebP or GIF.`
    return
  }
  uploading.value = 0
  try {
    const item = await uploadBoardImage(props.boardId, file, file.name, (f) => (uploading.value = Math.round(f * 100)))
    set('fileId', item.id)
    await queries.invalidateQueries({ queryKey: boardKeys.images(props.boardId) })
  } catch (e) {
    uploadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    uploading.value = null
  }
}
function onDrop(event: DragEvent) {
  dragging.value = false
  void uploadImage(event.dataTransfer?.files?.[0])
}
function onPaste(event: ClipboardEvent) {
  const file = [...(event.clipboardData?.files ?? [])][0]
  if (file) {
    event.preventDefault()
    void uploadImage(file)
  }
}

// --- on duty today ---------------------------------------------------------------------------------------------------------
const nameSizes: { value: BoardTextSize; label: string }[] = [
  { value: 'Medium', label: 'Medium' },
  { value: 'Large', label: 'Large' },
  { value: 'Huge', label: 'Huge' },
]
const dutyLayouts = [
  { value: 'stacked', label: 'Stacked' },
  { value: 'line', label: 'One line' },
]

const weekOptions = [
  { value: '', label: 'As the board' },
  { value: 'on', label: 'On' },
  { value: 'off', label: 'Off' },
]

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
      <template v-if="has('SensorMessage')">
        <div class="field">
          <label>Sensor</label>
          <p v-if="sensorId" class="chosen" data-test="message-sensor">
            <i class="pi pi-wave-pulse" aria-hidden="true" /> {{ names.get(sensorId) ?? sensorId }}
          </p>
          <AutoComplete
            v-model="pick"
            :suggestions="suggestions"
            option-label="name"
            :placeholder="sensorId ? 'Search another sensor' : 'Search a sensor'"
            :delay="250"
            data-test="message-sensor-search"
            @complete="search"
            @option-select="(ev) => pickSensor(ev.value)"
          />
          <small class="field-hint warn"
            >The message is shown as the sensor sent it, to everyone who opens this board.</small
          >
        </div>
        <div class="field">
          <label>Size</label>
          <SelectButton
            :model-value="s.textSize ?? 'Medium'"
            :options="messageSizes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            data-test="message-size"
            @update:model-value="(v) => set('textSize', v)"
          />
          <small class="field-hint"
            >Fit: as large as the element allows; give it a share of the column's height. In a page that scrolls, as
            wide as the column allows.</small
          >
        </div>
        <div v-if="s.textSize !== 'Fit'" class="field">
          <label for="es-lines">At most (lines)</label>
          <InputNumber
            input-id="es-lines"
            :model-value="s.rows ?? 6"
            :min="1"
            :max="20"
            show-buttons
            @update:model-value="(v) => set('rows', v)"
          />
        </div>
        <label class="check"
          ><Checkbox :model-value="s.monospace" binary @update:model-value="(v) => set('monospace', v)" /> Monospace,
          for a script's output</label
        >
        <label class="check"
          ><Checkbox :model-value="s.center" binary @update:model-value="(v) => set('center', v)" /> Centred</label
        >
        <div class="field">
          <label>The state's colour</label>
          <SelectButton
            :model-value="s.stateColour ?? 'Bar'"
            :options="stateColours"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @update:model-value="(v) => set('stateColour', v)"
          />
        </div>
        <label class="check"
          ><Checkbox :model-value="s.age" binary @update:model-value="(v) => set('age', v)" /> How old it is</label
        >
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
          <label for="es-time-format">Time</label>
          <Select
            input-id="es-time-format"
            :model-value="s.timeFormat ?? ''"
            :options="clockTimeFormats"
            option-label="label"
            option-value="value"
            data-test="clock-time-format"
            @update:model-value="(v) => set('timeFormat', v || null)"
          />
        </div>
        <div v-if="s.date" class="field">
          <label for="es-date-format">Date format</label>
          <Select
            input-id="es-date-format"
            :model-value="s.dateFormat ?? ''"
            :options="clockDateFormats"
            option-label="label"
            option-value="value"
            data-test="clock-date-format"
            @update:model-value="(v) => set('dateFormat', v || null)"
          />
        </div>
        <div class="field">
          <label>Week number</label>
          <SelectButton
            :model-value="s.weekNumber == null ? '' : s.weekNumber ? 'on' : 'off'"
            :options="weekOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @update:model-value="(v) => set('weekNumber', v === '' ? null : v === 'on')"
          />
        </div>
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
      <template v-if="has('Image')">
        <div class="field">
          <label>Image</label>
          <div
            class="drop"
            :data-over="dragging ? 'true' : undefined"
            tabindex="0"
            data-test="image-drop"
            @dragover.prevent="dragging = true"
            @dragleave="dragging = false"
            @drop.prevent="onDrop"
            @paste="onPaste"
          >
            <img v-if="s.fileId && boardId" :src="boardImageUrl(boardId, s.fileId)" alt="" class="current" />
            <Button
              :label="s.fileId ? 'Upload another…' : 'Upload an image…'"
              icon="pi pi-upload"
              size="small"
              severity="secondary"
              :loading="uploading !== null"
              :disabled="!boardId"
              data-test="image-upload"
              @click="fileInput?.click()"
            />
            <input
              ref="fileInput"
              type="file"
              :accept="boardImageTypes.join(',')"
              hidden
              data-test="image-file"
              @change="(ev) => uploadImage((ev.target as HTMLInputElement).files?.[0])"
            />
            <small class="field-hint"
              >Or drop or paste one here. PNG, JPEG, WebP or GIF, animated ones too; at most {{ maxImage }}.</small
            >
            <small v-if="uploading !== null" class="field-hint">Uploading… {{ uploading }} %</small>
          </div>
          <small v-if="uploadError" class="field-hint warn" role="alert">{{ uploadError }}</small>
        </div>
        <div v-if="boardImages.length" class="field">
          <label>Or one of this board's images</label>
          <ul class="images">
            <li v-for="img in boardImages" :key="img.id">
              <button
                type="button"
                class="image-pick"
                :aria-pressed="s.fileId === img.id"
                @click="set('fileId', img.id)"
              >
                <img :src="boardImageUrl(boardId!, img.id)" alt="" />
                <span class="grow"
                  >{{ img.name }}<br /><span class="muted"
                    >{{ formatBytes(img.size)
                    }}<template v-if="img.width"> · {{ img.width }} × {{ img.height }}</template></span
                  ></span
                >
              </button>
            </li>
          </ul>
        </div>
        <div class="field">
          <label>Fit</label>
          <SelectButton
            :model-value="s.imageFit ?? 'Contain'"
            :options="imageFits"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @update:model-value="(v) => set('imageFit', v)"
          />
        </div>
        <div class="field">
          <label>Background</label>
          <SelectButton
            :model-value="s.background ?? 'None'"
            :options="backgrounds"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @update:model-value="(v) => set('background', v)"
          />
        </div>
        <div class="field">
          <label for="es-caption">Caption</label>
          <InputText
            id="es-caption"
            :model-value="s.caption ?? ''"
            :maxlength="200"
            @update:model-value="(v) => set('caption', v?.trim() ? v : null)"
          />
        </div>
        <div class="field">
          <label for="es-alt">Alternative text</label>
          <InputText
            id="es-alt"
            :model-value="s.alt ?? ''"
            :placeholder="s.caption ?? 'Image'"
            :maxlength="200"
            @update:model-value="(v) => set('alt', v?.trim() ? v : null)"
          />
          <small class="field-hint">For screen readers. With motion off, an animation shows its first frame.</small>
        </div>
      </template>
      <template v-if="has('OnDuty')">
        <div class="field">
          <label for="es-deputies">Deputies shown</label>
          <InputNumber
            input-id="es-deputies"
            :model-value="s.deputies ?? 0"
            :min="0"
            :max="5"
            show-buttons
            @update:model-value="(v) => set('deputies', v ?? 0)"
          />
          <small class="field-hint"
            >From the availability plan: the day's fallback, the global fallback, then the global fallback group, those
            who work that day.</small
          >
        </div>
        <div class="field two">
          <div>
            <label for="es-duty-label">Label for the one on duty</label>
            <InputText
              id="es-duty-label"
              :model-value="s.dutyLabel ?? ''"
              placeholder="Responsible, Fallback …"
              :maxlength="40"
              @update:model-value="(v) => set('dutyLabel', v?.trim() ? v : null)"
            />
          </div>
          <div>
            <label for="es-deputy-label">Label for a deputy</label>
            <InputText
              id="es-deputy-label"
              :model-value="s.deputyLabel ?? ''"
              placeholder="Deputy"
              :maxlength="40"
              @update:model-value="(v) => set('deputyLabel', v?.trim() ? v : null)"
            />
          </div>
        </div>
        <div class="field">
          <label for="es-nobody">Text when nobody is on duty</label>
          <InputText
            id="es-nobody"
            :model-value="s.nobodyText ?? ''"
            placeholder="Nobody is on duty today."
            :maxlength="100"
            @update:model-value="(v) => set('nobodyText', v?.trim() ? v : null)"
          />
        </div>
        <div class="field">
          <label for="es-closed">Text on a closed day</label>
          <InputText
            id="es-closed"
            :model-value="s.closedText ?? ''"
            placeholder="Closed today."
            :maxlength="100"
            @update:model-value="(v) => set('closedText', v?.trim() ? v : null)"
          />
        </div>
        <label class="check"
          ><Checkbox :model-value="s.role" binary @update:model-value="(v) => set('role', v)" /> The labels</label
        >
        <label class="check"
          ><Checkbox :model-value="s.where" binary @update:model-value="(v) => set('where', v)" /> Where each works
          today (on site, home office, travelling)</label
        >
        <label class="check"
          ><Checkbox :model-value="s.dayComment" binary @update:model-value="(v) => set('dayComment', v)" /> The day's
          comment from the availability plan</label
        >
        <small class="field-hint"
          >Whoever fills in the plan writes it for the team; anyone who opens the board reads it.</small
        >
        <label class="check"
          ><Checkbox :model-value="s.tomorrow" binary @update:model-value="(v) => set('tomorrow', v)" /> Who is on duty
          on the next open day</label
        >
        <div class="field">
          <label>Name size</label>
          <SelectButton
            :model-value="s.textSize ?? 'Large'"
            :options="nameSizes"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @update:model-value="(v) => set('textSize', v)"
          />
        </div>
        <div class="field">
          <label>Layout</label>
          <SelectButton
            :model-value="s.oneLine ? 'line' : 'stacked'"
            :options="dutyLayouts"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            @update:model-value="(v) => set('oneLine', v === 'line')"
          />
        </div>
        <p v-if="people === 'Hidden'" class="field-hint">
          The board hides people's names (Look → People): this shows the labels only.
        </p>
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

.drop {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2);
  border: 1px dashed var(--pnmt-border);
  border-radius: 6px;
}

.drop[data-over='true'] {
  border-color: var(--pnmt-accent, #3b59a5);
  background: var(--pnmt-bg);
}

.drop .current {
  max-width: 100%;
  max-height: 140px;
  object-fit: contain;
}

.images {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.image-pick {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  padding: 4px;
  text-align: left;
  background: none;
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.image-pick[aria-pressed='true'] {
  border-color: var(--pnmt-accent, #3b59a5);
  outline: 1px solid var(--pnmt-accent, #3b59a5);
}

.image-pick img {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 4px;
}

.chosen {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin: 0;
  font-weight: 600;
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
