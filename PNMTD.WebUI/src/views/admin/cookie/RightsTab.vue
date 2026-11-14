<script setup lang="ts">
import Message from 'primevue/message'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import ErrorState from '@/components/ErrorState.vue'
import { withUnknown } from './cookieAdmin'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useGroupOptions } from './usePickers'
import { useSection } from './useSection'

/**
 * Rights (docs/cookie-plan.md §9.3, §10): the group whose rights Cookie reads with. Never the admin flag; in a chat,
 * the lower of the group's rights and the asker's.
 */
const s = useSection('rights')
const groups = useGroupOptions()
const options = computed(() => withUnknown(groups.options.value, [s.draft.value?.groupId], groups.unknown))
const chosen = computed(() => (groups.query.data.value ?? []).find((g) => g.id === s.draft.value?.groupId))
const rights = computed(() => Object.entries(chosen.value?.rights ?? {}))
</script>

<template>
  <div class="tab">
    <ErrorState
      v-if="s.settings.isError.value && !s.draft.value"
      :error="s.settings.error.value"
      what="the settings"
      @retry="s.settings.refetch()"
    />
    <section v-else-if="s.draft.value" class="panel" aria-labelledby="rights-title">
      <div id="rights-title" class="panel-header">Cookie's group</div>
      <div class="panel-body form">
        <p class="intro">
          Cookie reads with this group's rights, and never with the admin flag. In a chat it reads with the lower of the
          group's rights and the asker's. Without a group, Cookie can read nothing.
        </p>
        <Message v-if="!groups.listed.value" severity="info" :closable="false" size="small">
          Only admins can list PNMT's groups: you see the chosen one by its id.
        </Message>
        <div class="field">
          <label for="rights-group">Group</label>
          <Select
            v-model="s.draft.value.groupId"
            input-id="rights-group"
            :options="options"
            option-label="label"
            option-value="value"
            filter
            show-clear
            placeholder="None"
            class="group"
            :invalid="!!s.error('groupId')"
          />
          <small v-if="s.error('groupId')" class="field-error">{{ s.error('groupId') }}</small>
          <small v-else class="field-hint">
            A group of its own is best, so that its rights are Cookie's alone. Groups are under Admin → Groups.
          </small>
        </div>
        <div v-if="chosen" class="field">
          <span class="label">Its rights</span>
          <div class="rights">
            <span v-if="!rights.length" class="muted">none</span>
            <Tag v-for="[area, level] in rights" :key="area" :value="`${area}: ${level}`" severity="secondary" />
          </div>
        </div>
        <SectionActions
          :busy="s.busy.value"
          :dirty="s.dirty.value"
          :errors="s.errors.value"
          :placed="(f) => f === 'groupId'"
          @save="s.save()"
          @reset="s.reset()"
        />
      </div>
    </section>
    <SettingsHistory v-if="s.draft.value" section="rights" title="the rights" @rolled-back="s.reload()" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 760px;
}

.intro {
  margin: 0;
}

.group {
  max-width: 24rem;
}

.label {
  font-weight: 500;
}

.rights {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}
</style>
