<script setup lang="ts">
import InputText from 'primevue/inputtext'
import ErrorState from '@/components/ErrorState.vue'
import PromptsPanel from './PromptsPanel.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useSection } from './useSection'

/**
 * Identity (docs/cookie-plan.md §9.3): who Cookie is, filled into its prompts as {name}, {creator}, {organisation}, {tone}
 * and {language}; and the prompts themselves.
 */
const s = useSection('identity', {
  prepare: (v) => ({
    name: v.name.trim(),
    creator: v.creator.trim(),
    organisation: v.organisation.trim(),
    tone: v.tone.trim(),
    language: v.language.trim(),
  }),
})
const fields = ['name', 'creator', 'organisation', 'tone', 'language']
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
      <section class="panel" aria-labelledby="identity-title">
        <div id="identity-title" class="panel-header">Identity</div>
        <form class="panel-body form" novalidate @submit.prevent="s.save()">
          <div class="form-grid">
            <div class="field">
              <label for="cookie-name">Name</label>
              <InputText id="cookie-name" v-model="s.draft.value.name" :invalid="!!s.error('name')" />
              <small v-if="s.error('name')" class="field-error">{{ s.error('name') }}</small>
              <small v-else class="field-hint">What Cookie calls itself, and signs with.</small>
            </div>
            <div class="field">
              <label for="cookie-creator">Created by</label>
              <InputText id="cookie-creator" v-model="s.draft.value.creator" :invalid="!!s.error('creator')" />
              <small v-if="s.error('creator')" class="field-error">{{ s.error('creator') }}</small>
              <small v-else class="field-hint">What Cookie answers when asked who made it.</small>
            </div>
            <div class="field">
              <label for="cookie-organisation">Organisation</label>
              <InputText
                id="cookie-organisation"
                v-model="s.draft.value.organisation"
                :invalid="!!s.error('organisation')"
              />
              <small v-if="s.error('organisation')" class="field-error">{{ s.error('organisation') }}</small>
              <small v-else class="field-hint">Whom PNMT monitors for. Empty: "your organisation".</small>
            </div>
            <div class="field">
              <label for="cookie-language">Language</label>
              <InputText id="cookie-language" v-model="s.draft.value.language" :invalid="!!s.error('language')" />
              <small v-if="s.error('language')" class="field-error">{{ s.error('language') }}</small>
              <small v-else class="field-hint">What Cookie writes in. Tickets are answered in their requester's.</small>
            </div>
            <div class="field wide">
              <label for="cookie-tone">Tone</label>
              <InputText id="cookie-tone" v-model="s.draft.value.tone" :invalid="!!s.error('tone')" />
              <small v-if="s.error('tone')" class="field-error">{{ s.error('tone') }}</small>
              <small v-else class="field-hint">Its personality, in a few words: "warm, plain and to the point".</small>
            </div>
          </div>
          <SectionActions
            :busy="s.busy.value"
            :dirty="s.dirty.value"
            :errors="s.errors.value"
            :placed="(f) => fields.includes(f)"
            @save="s.save()"
            @reset="s.reset()"
          />
        </form>
      </section>
      <SettingsHistory section="identity" title="the identity" @rolled-back="s.reload()" />
      <PromptsPanel />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.panel {
  max-width: 960px;
}
</style>
