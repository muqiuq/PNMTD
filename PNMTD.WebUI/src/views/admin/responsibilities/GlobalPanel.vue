<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import { ref } from 'vue'
import { ApiError } from '@/api/client'
import { personLabel, responsibilitiesApi, responsibilityKeys, type ResponsibilityGlobal } from '@/api/responsibilities'
import { useActions } from '@/composables/useActions'
import PersonPicker from './PersonPicker.vue'

/**
 * The global responsible and deputy (docs/responsibilities-plan.md §5): they own what no responsibility covers, and stand in for
 * one that has nobody. Not the availability plan's global fallback, which says who is on duty when nobody of the day works.
 */
const props = defineProps<{ global: ResponsibilityGlobal; manage: boolean }>()
const { run } = useActions()

const editing = ref(false)
const draft = ref<{ responsibleId: string | null; deputyId: string | null }>({ responsibleId: null, deputyId: null })
const errors = ref<Record<string, string | undefined>>({})

function open() {
  draft.value = { responsibleId: props.global.responsible?.id ?? null, deputyId: props.global.deputy?.id ?? null }
  errors.value = {}
  editing.value = true
}

async function save() {
  errors.value = {}
  const saved = await run(
    async () => {
      try {
        return await responsibilitiesApi.setGlobal(draft.value.responsibleId, draft.value.deputyId)
      } catch (e) {
        if (e instanceof ApiError && Object.keys(e.errors).length) {
          errors.value = { responsibleId: e.field('responsibleId'), deputyId: e.field('deputyId') }
          return null
        }
        throw e
      }
    },
    { failure: 'Not saved', invalidate: [responsibilityKeys.all] },
  )
  if (saved) editing.value = false
}
</script>

<template>
  <section class="panel" aria-labelledby="global-title">
    <div class="panel-header">
      <span id="global-title">Global</span>
      <Button v-if="manage" label="Change…" size="small" text @click="open" />
    </div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Global responsible</dt>
        <dd data-test="global-responsible">{{ personLabel(global.responsible) }}</dd>
        <dt>Global deputy</dt>
        <dd data-test="global-deputy">{{ personLabel(global.deputy) }}</dd>
      </dl>
      <p class="muted note">
        They own what no responsibility covers, and stand in for a responsibility that has nobody. Who is on duty each
        day is the availability plan's.
      </p>
      <Message v-if="!global.responsible && !global.deputy" severity="warn" :closable="false">
        No global responsible is set. What no responsibility covers has nobody.
      </Message>
    </div>
  </section>

  <Dialog v-model:visible="editing" header="Global responsible and deputy" modal :style="{ width: '28rem' }">
    <form id="global-form" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="global-responsible">Global responsible</label>
        <PersonPicker
          v-model="draft.responsibleId"
          input-id="global-responsible"
          :current="global.responsible"
          :invalid="!!errors.responsibleId"
        />
        <small v-if="errors.responsibleId" class="field-error">{{ errors.responsibleId }}</small>
      </div>
      <div class="field">
        <label for="global-deputy">Global deputy</label>
        <PersonPicker
          v-model="draft.deputyId"
          input-id="global-deputy"
          :current="global.deputy"
          :invalid="!!errors.deputyId"
        />
        <small v-if="errors.deputyId" class="field-error">{{ errors.deputyId }}</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="editing = false" />
      <Button type="submit" form="global-form" label="Save" />
    </template>
  </Dialog>
</template>

<style scoped>
.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.note {
  margin: var(--pnmt-space-2) 0 0;
  font-size: 0.857rem;
}
</style>
