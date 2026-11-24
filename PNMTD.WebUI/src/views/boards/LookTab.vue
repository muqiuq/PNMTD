<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import InputText from 'primevue/inputtext'
import Slider from 'primevue/slider'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { themeName, themes } from '@/boards/themes/registry'
import { kindNames, kindOrder, type BoardMessageKind, type ShownMessage } from '@/boards/messages'
import { lookTime } from '@/boards/time'
import type { BoardLook } from '@/boards/types'
import ThemePicker from './ThemePicker.vue'
import { dateFormats, dateLanguages, timeFormats } from './timeOptions'

/**
 * Look (docs/boards-plan.md §6.2, §9): the theme, fit to window, the text size, how often the board polls, how it names people, and
 * motion; the time and the date (docs/board-messages-plan.md §9.1, §9.2).
 */
const props = defineProps<{ manage: boolean; zone?: string | null }>()
const look = defineModel<BoardLook>('look', { required: true })
const refresh = defineModel<number>('refresh', { required: true })
const emit = defineEmits<{ tryTheme: [id: string | null]; previewMessage: [message: ShownMessage | null] }>()
const picking = ref(false)
const people = [
  { value: 'Names', label: 'Names' },
  { value: 'Initials', label: 'Initials' },
  { value: 'Hidden', label: 'Hidden' },
]
const current = () => themes.find((t) => t.id === look.value.theme)
const options = computed(() => lookTime(look.value))

// --- messages (docs/board-messages-plan.md §5.5) ---------------------------------------------------------------------------
const messageSizes = [
  { value: 'Large', label: 'Large (80 % × 70 %)' },
  { value: 'Full', label: 'Full' },
]
const kinds = kindOrder.map((k) => ({ value: k, label: kindNames[k] }))
const sampleKind = ref<BoardMessageKind>('Warning')
const sampleTitle = ref('Mail delays')
const sampleText = ref('Outbound mail is delayed by about **20 minutes**. Nothing is lost; we are on it.')
const previewing = ref(false)
function preview(on: boolean) {
  previewing.value = on
  emit(
    'previewMessage',
    on
      ? {
          id: 'preview',
          kind: sampleKind.value,
          title: sampleTitle.value.trim() || kindNames[sampleKind.value],
          text: sampleText.value.trim() || null,
          from: 'Anna Muster',
          sentAt: new Date().toISOString(),
          seconds: 60,
          remainingSeconds: 42,
          endsAt: Date.now() + 42_000,
        }
      : null,
  )
}
watch([sampleKind, sampleTitle, sampleText], () => previewing.value && preview(true))
onBeforeUnmount(() => previewing.value && emit('previewMessage', null))
const formats = computed(() =>
  dateFormats(props.zone ?? Intl.DateTimeFormat().resolvedOptions().timeZone, {
    dateLanguage: options.value.dateLanguage,
  }),
)
</script>

<template>
  <fieldset :disabled="!manage" class="form look-tab">
    <div class="field">
      <label>Theme</label>
      <div class="theme">
        <span class="name">{{ themeName(look.theme) }}</span>
        <span class="muted">{{ current()?.line }}</span>
        <Button label="Change…" size="small" severity="secondary" data-test="change-theme" @click="picking = true" />
      </div>
    </div>
    <label class="switch"><ToggleSwitch v-model="look.fit" /> Fit to window</label>
    <small class="field-hint"
      >For one screen: exactly the window, the text scaled to its height, lists cut to what fits. Off: a page that
      scrolls, for a phone or a laptop.</small
    >
    <div class="field">
      <label>Text size: {{ look.textScale }} %</label>
      <Slider v-model="look.textScale" :min="75" :max="250" :step="5" aria-label="Text size" />
    </div>
    <div class="field">
      <label for="refresh">Refresh every (seconds)</label>
      <InputNumber id="refresh" v-model="refresh" :min="10" :max="300" show-buttons />
    </div>
    <div class="field">
      <label>People</label>
      <SelectButton
        v-model="look.people"
        :options="people"
        option-label="label"
        option-value="value"
        :allow-empty="false"
      />
      <small class="field-hint">How tickets' assignees, the lounge and who is on duty name people.</small>
    </div>
    <label class="switch"><ToggleSwitch v-model="look.motion" /> Motion (a theme's animations, the red flash)</label>
    <h3 class="section">Time and date</h3>
    <div class="field">
      <label>Time</label>
      <SelectButton
        :model-value="options.timeFormat"
        :options="timeFormats"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        data-test="time-format"
        @update:model-value="(v) => (look = { ...look, timeFormat: v })"
      />
      <small class="field-hint"
        >Every time on the board. The header's clock and each clock element can have their own.</small
      >
    </div>
    <div class="field">
      <label for="date-format">Date</label>
      <Select
        input-id="date-format"
        :model-value="options.dateFormat"
        :options="formats"
        option-label="label"
        option-value="value"
        data-test="date-format"
        @update:model-value="(v) => (look = { ...look, dateFormat: v })"
      />
    </div>
    <div class="field">
      <label for="date-language">Day and month names</label>
      <Select
        input-id="date-language"
        :model-value="options.dateLanguage"
        :options="dateLanguages"
        option-label="label"
        option-value="value"
        data-test="date-language"
        @update:model-value="(v) => (look = { ...look, dateLanguage: v })"
      />
      <small class="field-hint">Only these names; the board's own words stay English.</small>
    </div>
    <label class="switch"
      ><ToggleSwitch
        :model-value="options.weekNumber"
        @update:model-value="(v) => (look = { ...look, weekNumber: v })"
      />
      The week number (ISO 8601) with the date</label
    >
    <h3 class="section">Messages</h3>
    <label class="switch"
      ><ToggleSwitch
        :model-value="look.messages ?? true"
        data-test="look-messages"
        @update:model-value="(v) => (look = { ...look, messages: v })"
      />
      Show messages sent to this board</label
    >
    <small class="field-hint"
      >From Admin → Debug, and from Cookie where it may. Off: the board isn't offered as a target.</small
    >
    <div v-if="look.messages ?? true" class="field">
      <label>Message box</label>
      <SelectButton
        :model-value="look.messageSize ?? 'Large'"
        :options="messageSizes"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        @update:model-value="(v) => (look = { ...look, messageSize: v })"
      />
    </div>
    <fieldset class="sample" :disabled="false">
      <legend>Preview a message</legend>
      <Select v-model="sampleKind" :options="kinds" option-label="label" option-value="value" aria-label="Kind" />
      <InputText v-model="sampleTitle" aria-label="Title" :maxlength="100" />
      <Textarea v-model="sampleText" aria-label="Text" rows="3" auto-resize :maxlength="1000" />
      <Button
        :label="previewing ? 'Hide the sample' : 'Show it in the preview'"
        size="small"
        severity="secondary"
        data-test="preview-message"
        @click="preview(!previewing)"
      />
      <small class="field-hint">Only in this preview: nothing is sent.</small>
    </fieldset>
  </fieldset>
  <ThemePicker
    v-model:visible="picking"
    v-model:theme="look.theme"
    @try="(id) => emit('tryTheme', id === look.theme ? null : id)"
  />
</template>

<style scoped>
.look-tab {
  border: none;
  margin: 0;
  padding: 0;
  min-width: 0;
}

.theme {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.theme .name {
  font-weight: 600;
}

.section {
  margin: var(--pnmt-space-3, 12px) 0 0;
  font-size: 1rem;
}

.sample {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  padding: var(--pnmt-space-2) var(--pnmt-space-3, 12px);
}

.sample legend {
  font-weight: 600;
  padding: 0 4px;
}

.switch {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
