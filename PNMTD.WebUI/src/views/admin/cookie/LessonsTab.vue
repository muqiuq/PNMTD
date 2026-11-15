<script setup lang="ts">
import Button from 'primevue/button'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import ErrorState from '@/components/ErrorState.vue'
import {
  instructionScopeLabel,
  lessonProblems,
  maxActiveLessons,
  suppressScopes,
  type InstructionScope,
  type Lesson,
} from '@/views/cookie/acting'
import { newId } from './cookieAdmin'
import InstructionValuePicker from './InstructionValuePicker.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useSection } from './useSection'

/**
 * Lessons (docs/cookie-plan.md §11, §21.1): short rules the summariser draws from people's Right and Wrong in the
 * decision log, once a day or after 10 new ratings, a versioned section like the others. Active ones (at most 30) go into
 * every prompt of Cookie and Seraph. A lesson that is a rule names what it suppresses; the summariser makes it a suppress
 * object for 30 days. Admins edit, turn off, add and remove them, and roll back.
 */
const s = useSection('lessons', {
  normalize: (v) => ({
    ...v,
    lessons: (v.lessons ?? []).map((l) => ({
      id: l.id,
      text: l.text,
      active: l.active ?? true,
      suppressScope: l.suppressScope ?? null,
      suppressValue: l.suppressValue ?? null,
    })),
  }),
  prepare: (v) => ({
    ...v,
    lessons: v.lessons.map((l) => ({
      ...l,
      text: l.text.trim(),
      suppressScope: l.suppressScope || null,
      suppressValue: l.suppressScope ? l.suppressValue?.trim() || null : null,
    })),
  }),
})
const lessons = computed<Lesson[]>(() => s.draft.value?.lessons ?? [])
const active = computed(() => lessons.value.filter((l) => l.active).length)
const problems = computed(() => lessonProblems(lessons.value))
const problem = (l: Lesson) => s.error(`lessons.${l.id}`) ?? problems.value[l.id]
const listProblem = computed(() => s.error('lessons') ?? problems.value.lessons)

const scopeOptions = [
  { label: 'Nothing', value: null as string | null },
  ...suppressScopes.map((sc) => ({ label: instructionScopeLabel(sc), value: sc as string | null })),
]
function setScope(l: Lesson, scope: string | null) {
  l.suppressScope = scope
  l.suppressValue = null
}
function add() {
  s.draft.value?.lessons.push({ id: newId(), text: '', active: true, suppressScope: null, suppressValue: null })
}
function remove(i: number) {
  s.draft.value?.lessons.splice(i, 1)
}
const placed = (f: string) => f === 'lessons' || f.startsWith('lessons.')
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
      <section class="panel" aria-labelledby="lessons-title">
        <div id="lessons-title" class="panel-header">
          Lessons
          <span class="muted normal small">{{ active }} of {{ maxActiveLessons }} active</span>
          <Button
            label="Add"
            icon="pi pi-plus"
            size="small"
            severity="secondary"
            text
            class="end"
            :disabled="lessons.length >= 100"
            @click="add"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            Short rules from people's Right and Wrong in Cookie's decision log: the summariser writes them once a day,
            or after 10 new ratings, and merges similar ones. The active ones go into every prompt of Cookie and Seraph.
            A lesson that is a rule names what it suppresses, and the summariser makes it a suppress object for 30 days
            (Mute, suppress). Each summary replaces the active lessons with its own; the ones turned off stay.
          </p>
          <div v-if="listProblem" class="field-error" role="alert">{{ listProblem }}</div>
          <p v-if="!lessons.length" class="muted none">
            No lessons yet: they come from the ratings in Cookie's window → Decisions.
          </p>
          <ol v-else class="lessons">
            <li v-for="(l, i) in lessons" :key="l.id" class="lesson" :class="{ off: !l.active }">
              <div class="row">
                <Textarea
                  v-model="l.text"
                  rows="1"
                  auto-resize
                  maxlength="300"
                  class="text"
                  placeholder="No Pushover for toner warnings"
                  :aria-label="`Lesson ${i + 1}`"
                  :invalid="!!problem(l) && !l.text.trim()"
                />
                <span class="switch">
                  <ToggleSwitch v-model="l.active" :input-id="`lesson-${l.id}-active`" />
                  <label :for="`lesson-${l.id}-active`" class="small">Active</label>
                </span>
                <Button
                  icon="pi pi-trash"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Remove lesson ${i + 1}`"
                  @click="remove(i)"
                />
              </div>
              <div class="row">
                <span class="muted small">Suppresses</span>
                <Select
                  :model-value="l.suppressScope ?? null"
                  :options="scopeOptions"
                  option-label="label"
                  option-value="value"
                  placeholder="Nothing"
                  size="small"
                  class="scope"
                  :aria-label="`Lesson ${i + 1}, suppresses`"
                  @update:model-value="(v: string | null) => setScope(l, v)"
                />
                <InstructionValuePicker
                  v-if="l.suppressScope"
                  :key="l.suppressScope"
                  :model-value="l.suppressValue ?? null"
                  :scope="l.suppressScope as InstructionScope"
                  :label="`Lesson ${i + 1}, ${instructionScopeLabel(l.suppressScope).toLowerCase()}`"
                  :invalid="!!problem(l) && !l.suppressValue"
                  @update:model-value="(v: string | null) => (l.suppressValue = v)"
                />
              </div>
              <small v-if="problem(l)" class="field-error">{{ problem(l) }}</small>
            </li>
          </ol>
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
      <SettingsHistory section="lessons" title="the lessons" @rolled-back="s.reload()" />
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
  gap: var(--pnmt-space-2);
}

.intro {
  margin: 0;
  max-width: 90ch;
}

.none {
  margin: 0;
}

.normal {
  font-weight: 400;
}

.end {
  margin-left: auto;
}

.lessons {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.lesson {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) 0;
  border-bottom: 1px solid var(--pnmt-border);
}

.lesson.off .text {
  opacity: 0.6;
}

.row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.text {
  flex: 1;
  min-width: 16rem;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.scope {
  width: 10rem;
}

.small {
  font-size: 0.857rem;
}
</style>
