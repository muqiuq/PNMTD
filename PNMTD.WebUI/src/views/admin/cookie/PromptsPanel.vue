<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import type { S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { missingPlaceholders, setOverride } from './cookieAdmin'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useCookieDefaults, useSection } from './useSection'

/**
 * The prompts (docs/cookie-prompts.md): each ships with a default, and only the changed ones are saved, so the others
 * follow a new release's defaults. Code fills in the {placeholders}; the required ones may move, but not go.
 */
const s = useSection('prompts')
const defaults = useCookieDefaults()

const textOf = (p: S['CookiePromptItem']) => s.draft.value?.texts[p.key] ?? p.text
const changed = (p: S['CookiePromptItem']) => s.draft.value?.texts[p.key] !== undefined
function setText(p: S['CookiePromptItem'], text: string | undefined) {
  if (s.draft.value) setOverride(s.draft.value.texts, p.key, text ?? '', p.text)
}
function resetText(p: S['CookiePromptItem']) {
  if (s.draft.value) setOverride(s.draft.value.texts, p.key, p.text, p.text)
}
</script>

<template>
  <section class="panel" aria-labelledby="prompts-title">
    <div id="prompts-title" class="panel-header">Prompts</div>
    <div class="panel-body body">
      <p class="muted small intro">
        The texts Cookie's models get. Code fills in the parts in {braces} at each call, and adds the answer's format
        itself. Text from outside (tickets, mails, notes) always comes marked as untrusted.
      </p>
      <ErrorState
        v-if="defaults.isError.value"
        :error="defaults.error.value"
        what="the default prompts"
        @retry="defaults.refetch()"
      />
      <template v-else-if="defaults.data.value && s.draft.value">
        <div v-for="p in defaults.data.value.prompts" :key="p.key" class="prompt">
          <div class="prompt-head">
            <label :for="`prompt-${p.key}`" class="label">{{ p.label }}</label>
            <span class="muted small">{{ p.usedFor }}</span>
            <Tag v-if="changed(p)" value="changed" severity="info" />
            <Button
              v-if="changed(p)"
              label="Reset to default"
              icon="pi pi-undo"
              size="small"
              severity="secondary"
              text
              class="reset"
              @click="resetText(p)"
            />
          </div>
          <Textarea
            :id="`prompt-${p.key}`"
            :model-value="textOf(p)"
            rows="8"
            auto-resize
            class="text mono"
            :invalid="!!s.error(`texts.${p.key}`) || missingPlaceholders(textOf(p), p.required).length > 0"
            @update:model-value="(v) => setText(p, v)"
          />
          <small v-if="s.error(`texts.${p.key}`)" class="field-error">{{ s.error(`texts.${p.key}`) }}</small>
          <Message
            v-else-if="missingPlaceholders(textOf(p), p.required).length"
            severity="warn"
            :closable="false"
            size="small"
          >
            {{ missingPlaceholders(textOf(p), p.required).join(', ') }} can be moved, but not removed: code fills it in.
          </Message>
          <small v-if="p.required.length" class="field-hint">
            Required: <code v-for="r in p.required" :key="r" class="placeholder">{{ r }}</code>
          </small>
        </div>
        <SectionActions
          :busy="s.busy.value"
          :dirty="s.dirty.value"
          :errors="s.errors.value"
          :placed="(f) => f.startsWith('texts.')"
          label="Save the prompts"
          @save="s.save()"
          @reset="s.reset()"
        />
      </template>
    </div>
  </section>
  <SettingsHistory section="prompts" title="the prompts" @rolled-back="s.reload()" />
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-4);
}

.intro {
  margin: 0;
}

.prompt {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.prompt-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.label {
  font-weight: 600;
}

.reset {
  margin-left: auto;
}

.text {
  width: 100%;
  max-height: 60vh;
  font-size: 0.857rem;
}

.placeholder {
  margin-right: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
