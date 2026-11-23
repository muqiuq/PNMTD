<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import SelectButton from 'primevue/selectbutton'
import Slider from 'primevue/slider'
import ToggleSwitch from 'primevue/toggleswitch'
import { ref } from 'vue'
import { themeName, themes } from '@/boards/themes/registry'
import type { BoardLook } from '@/boards/types'
import ThemePicker from './ThemePicker.vue'

/**
 * Look (docs/boards-plan.md §6.2, §9): the theme, fit to window, the text size, how often the board polls, how it names people, and
 * motion.
 */
defineProps<{ manage: boolean }>()
const look = defineModel<BoardLook>('look', { required: true })
const refresh = defineModel<number>('refresh', { required: true })
const emit = defineEmits<{ tryTheme: [id: string | null] }>()
const picking = ref(false)
const people = [
  { value: 'Names', label: 'Names' },
  { value: 'Initials', label: 'Initials' },
  { value: 'Hidden', label: 'Hidden' },
]
const current = () => themes.find((t) => t.id === look.value.theme)
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

.switch {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
