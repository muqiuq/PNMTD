<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { ref } from 'vue'
import type { S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { severityOptions } from '@/views/zabbix/zabbix'
import { newId, setOverride } from './cookieAdmin'
import PromptPreview from './PromptPreview.vue'
import ScopeValuePicker from './ScopeValuePicker.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useCookieDefaults, useSection } from './useSection'

/**
 * Cases (docs/cookie-plan.md §4.3, §7.1, §9.3): which Zabbix problems join a case, when a problem counts as unresolved,
 * an instruction for each kind of case, and narrower ones for a location, a host, a Redmine project or a sensor type,
 * which Cookie gets after the general one.
 */
const s = useSection('cases', {
  prepare: (v) => ({
    ...v,
    scoped: v.scoped.map((i) => ({ ...i, value: (i.value ?? '').trim(), label: i.label.trim() || i.value.trim() })),
  }),
})
const defaults = useCookieDefaults()

const textOf = (k: S['CookiePromptItem']) => s.draft.value?.instructions[k.key] ?? k.text
const changed = (k: S['CookiePromptItem']) => s.draft.value?.instructions[k.key] !== undefined
function setText(k: S['CookiePromptItem'], text: string | undefined) {
  if (s.draft.value) setOverride(s.draft.value.instructions, k.key, text ?? '', k.text)
}

// A kind's instruction as the model receives it, after Cookie's identity, goals and rules (case:<kind>).
const previewing = ref<string | null>(null)
const previewTitle = ref('')
function preview(k: S['CookiePromptItem']) {
  previewTitle.value = k.label
  previewing.value = `case:${k.key}`
}

const scopes: { label: string; value: S['InstructionScope'] }[] = [
  { label: 'A location', value: 'Location' },
  { label: 'A host', value: 'Host' },
  { label: 'A Redmine project', value: 'Project' },
  { label: 'A sensor type', value: 'SensorType' },
]
function addScoped() {
  s.draft.value?.scoped.push({ id: newId(), scope: 'Location', value: '', label: '', text: '' })
}
function removeScoped(id: string) {
  if (s.draft.value) s.draft.value.scoped = s.draft.value.scoped.filter((i) => i.id !== id)
}
function changeScope(i: S['ScopedInstruction'], scope: S['InstructionScope']) {
  i.scope = scope
  i.value = ''
  i.label = ''
}
const placed = (f: string) =>
  ['zabbixMinSeverity', 'unresolvedHours'].includes(f) || f.startsWith('instructions.') || f.startsWith('scoped.')
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
      <section class="panel" aria-labelledby="cases-rules-title">
        <div id="cases-rules-title" class="panel-header">What makes a case</div>
        <div class="panel-body form-grid">
          <div class="field">
            <label for="cases-severity">Zabbix problems from</label>
            <Select
              v-model="s.draft.value.zabbixMinSeverity"
              input-id="cases-severity"
              :options="severityOptions"
              option-label="label"
              option-value="value"
              :invalid="!!s.error('zabbixMinSeverity')"
            />
            <small v-if="s.error('zabbixMinSeverity')" class="field-error">{{ s.error('zabbixMinSeverity') }}</small>
            <small v-else class="field-hint"
              >A Zabbix problem of this severity or worse joins its host's case. Bad sensors always do.</small
            >
          </div>
          <div class="field">
            <label for="cases-unresolved">Unresolved after</label>
            <InputNumber
              v-model="s.draft.value.unresolvedHours"
              input-id="cases-unresolved"
              :min="1"
              :max="1440"
              :max-fraction-digits="1"
              suffix=" hours"
              :invalid="!!s.error('unresolvedHours')"
            />
            <small v-if="s.error('unresolvedHours')" class="field-error">{{ s.error('unresolvedHours') }}</small>
            <small v-else class="field-hint"
              >A problem without progress this long gets a ticket, when that duty is on (Actions). 48 hours is 2
              days.</small
            >
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="cases-kinds-title">
        <div id="cases-kinds-title" class="panel-header">An instruction for each kind of case</div>
        <div class="panel-body body">
          <p class="muted small intro">
            What Cookie should look at, what counts as urgent, what to do, and what never to do. Only changed ones are
            saved; the others follow the defaults.
          </p>
          <ErrorState
            v-if="defaults.isError.value"
            :error="defaults.error.value"
            what="the kinds of case"
            @retry="defaults.refetch()"
          />
          <div v-for="k in defaults.data.value?.caseKinds ?? []" :key="k.key" class="item">
            <div class="item-head">
              <label :for="`kind-${k.key}`" class="label">{{ k.label }}</label>
              <Tag v-if="changed(k)" value="changed" severity="info" />
              <span class="end buttons">
                <Button
                  v-if="changed(k)"
                  label="Reset to default"
                  icon="pi pi-undo"
                  size="small"
                  severity="secondary"
                  text
                  @click="setText(k, k.text)"
                />
                <Button
                  label="Preview"
                  icon="pi pi-eye"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Preview: ${k.label}`"
                  @click="preview(k)"
                />
              </span>
            </div>
            <Textarea
              :id="`kind-${k.key}`"
              :model-value="textOf(k)"
              rows="5"
              auto-resize
              class="text"
              :invalid="!!s.error(`instructions.${k.key}`)"
              @update:model-value="(v) => setText(k, v)"
            />
            <small v-if="s.error(`instructions.${k.key}`)" class="field-error">{{
              s.error(`instructions.${k.key}`)
            }}</small>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="cases-scoped-title">
        <div id="cases-scoped-title" class="panel-header">
          Narrower instructions
          <Button label="Add" icon="pi pi-plus" size="small" severity="secondary" text class="end" @click="addScoped" />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            For a location, a host, a Redmine project or a sensor type. Cookie gets them after the general instruction,
            for the cases they apply to.
          </p>
          <p v-if="!s.draft.value.scoped.length" class="muted">None.</p>
          <div v-for="i in s.draft.value.scoped" :key="i.id" class="item scoped">
            <div class="item-head">
              <Select
                :model-value="i.scope"
                :options="scopes"
                option-label="label"
                option-value="value"
                size="small"
                aria-label="Applies to"
                @update:model-value="(v: S['InstructionScope']) => changeScope(i, v)"
              />
              <ScopeValuePicker
                :model-value="i.value"
                :scope="i.scope"
                :invalid="!!s.error(`scoped.${i.id}`) && !i.value.trim()"
                @update:model-value="(v) => (i.value = v ?? '')"
                @picked="(label) => (i.label = label)"
              />
              <InputText v-model="i.label" size="small" placeholder="Label" aria-label="Label" class="label-input" />
              <Button
                icon="pi pi-trash"
                size="small"
                severity="secondary"
                text
                class="end"
                aria-label="Remove"
                @click="removeScoped(i.id)"
              />
            </div>
            <Textarea
              v-model="i.text"
              rows="3"
              auto-resize
              class="text"
              placeholder="What Cookie should know or do here"
              aria-label="Instruction"
              :invalid="!!s.error(`scoped.${i.id}`)"
            />
            <small v-if="s.error(`scoped.${i.id}`)" class="field-error">{{ s.error(`scoped.${i.id}`) }}</small>
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
      <SettingsHistory section="cases" title="the case settings" @rolled-back="s.reload()" />
      <PromptPreview v-model="previewing" :title="previewTitle" :dirty="s.dirty.value" />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
}

.item {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.scoped {
  padding-bottom: var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.scoped:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.item-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.label {
  font-weight: 600;
}

.label-input {
  width: 14rem;
}

.end {
  margin-left: auto;
}

.buttons {
  display: flex;
}

.text {
  width: 100%;
  max-height: 50vh;
}

.small {
  font-size: 0.857rem;
}
</style>
