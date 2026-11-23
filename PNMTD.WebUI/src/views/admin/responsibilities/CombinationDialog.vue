<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { responsibilitiesApi, responsibilityKeys, type RedmineCombination } from '@/api/responsibilities'
import { useActions } from '@/composables/useActions'
import PersonPicker from './PersonPicker.vue'

/**
 * A Redmine combination (docs/responsibilities-plan.md §6.1, §9.5): a project PNMT reads (subprojects indented under their
 * parent), a tracker or every tracker, the user who handles it, a note. From a responsibility's page its responsibility is set;
 * on the Redmine tab it is chosen, and a change can move it to another responsibility.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{
  /** The combination to change; none for a new one. */
  combination?: RedmineCombination | null
  /** The responsibility it is on, fixed when opened from that responsibility's page. */
  responsibilityId?: string | null
  fixed?: boolean
}>()
const { run } = useActions()

const lookups = useQuery({
  queryKey: responsibilityKeys.redmine,
  queryFn: responsibilitiesApi.redmine,
  staleTime: 5 * 60_000,
  enabled: visible,
})
const responsibilities = useQuery({
  queryKey: computed(() => responsibilityKeys.list({})),
  queryFn: () => responsibilitiesApi.list({}),
  enabled: computed(() => visible.value && !props.fixed),
})

const projects = computed(() => {
  const list = (lookups.data.value?.projects ?? []).map((p) => ({
    id: p.id,
    label: p.path,
    name: p.name,
    depth: p.depth,
  }))
  const current = props.combination
  if (current && !list.some((p) => p.id === current.projectId)) {
    list.unshift({ id: current.projectId, label: current.projectPath, name: current.projectName, depth: 0 })
  }
  return list
})
const EVERY = -1
const trackers = computed(() => {
  const list = [{ id: EVERY, name: 'Every tracker' }, ...(lookups.data.value?.trackers ?? [])]
  const current = props.combination
  if (current?.trackerId != null && !list.some((t) => t.id === current.trackerId)) {
    list.push({ id: current.trackerId, name: current.trackerName ?? `#${current.trackerId}` })
  }
  return list
})

const draft = ref({
  responsibilityId: null as string | null,
  projectId: null as number | null,
  trackerId: EVERY as number,
  userId: null as string | null,
  note: '',
})
const errors = ref<Record<string, string | undefined>>({})
const conflict = ref<string | null>(null)
watch(visible, (open) => {
  if (!open) return
  const c = props.combination
  draft.value = {
    responsibilityId: c?.responsibilityId ?? props.responsibilityId ?? null,
    projectId: c?.projectId ?? null,
    trackerId: c?.trackerId ?? EVERY,
    userId: c?.user?.id ?? null,
    note: c?.note ?? '',
  }
  errors.value = {}
  conflict.value = null
})

async function save() {
  errors.value = draft.value.responsibilityId ? {} : { responsibilityId: 'A responsibility.' }
  if (errors.value.responsibilityId) return
  conflict.value = null
  const body = {
    projectId: draft.value.projectId,
    trackerId: draft.value.trackerId === EVERY ? null : draft.value.trackerId,
    userId: draft.value.userId,
    note: draft.value.note.trim() || null,
    responsibilityId: draft.value.responsibilityId,
  }
  const c = props.combination
  const saved = await run(
    async () => {
      try {
        return c
          ? await responsibilitiesApi.updateCombination(c.responsibilityId, c.id, body)
          : await responsibilitiesApi.addCombination(draft.value.responsibilityId!, body)
      } catch (e) {
        if (e instanceof ApiError && e.code === 'duplicate_combination') {
          conflict.value = e.message
          return null
        }
        if (e instanceof ApiError && Object.keys(e.errors).length) {
          errors.value = {
            projectId: e.field('projectId'),
            trackerId: e.field('trackerId'),
            userId: e.field('userId'),
            note: e.field('note'),
            responsibilityId: e.field('responsibilityId'),
          }
          return null
        }
        throw e
      }
    },
    {
      success: c ? 'Combination saved' : 'Combination added',
      failure: 'Not saved',
      invalidate: [responsibilityKeys.all],
    },
  )
  if (saved) visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="combination ? 'Redmine combination' : 'Add a Redmine combination'"
    modal
    :style="{ width: '32rem' }"
  >
    <Message v-if="lookups.data.value?.error" severity="warn" :closable="false">
      Redmine can't be reached: projects and trackers can't be listed right now. {{ lookups.data.value.error }}
    </Message>
    <Message v-if="conflict" severity="error" :closable="false">{{ conflict }}</Message>
    <form id="combination-form" class="form" novalidate @submit.prevent="save">
      <div v-if="!fixed" class="field">
        <label for="c-responsibility">Responsibility</label>
        <Select
          v-model="draft.responsibilityId"
          input-id="c-responsibility"
          :options="responsibilities.data.value?.items ?? []"
          option-label="name"
          option-value="id"
          placeholder="Choose a responsibility"
          filter
          fluid
          :invalid="!!errors.responsibilityId"
        />
        <small v-if="errors.responsibilityId" class="field-error">{{ errors.responsibilityId }}</small>
      </div>
      <div class="field">
        <label for="c-project">Project</label>
        <Select
          v-model="draft.projectId"
          input-id="c-project"
          :options="projects"
          option-label="label"
          option-value="id"
          placeholder="Choose a project"
          :loading="lookups.isLoading.value"
          filter
          fluid
          :invalid="!!errors.projectId"
        >
          <template #option="{ option }">
            <span :style="{ paddingLeft: `${option.depth * 1.25}rem` }">{{ option.name }}</span>
          </template>
        </Select>
        <small v-if="errors.projectId" class="field-error">{{ errors.projectId }}</small>
        <small v-else class="field-hint">Subprojects are projects of their own.</small>
      </div>
      <div class="field">
        <label for="c-tracker">Tracker</label>
        <Select
          v-model="draft.trackerId"
          input-id="c-tracker"
          :options="trackers"
          option-label="name"
          option-value="id"
          fluid
          :invalid="!!errors.trackerId"
        />
        <small v-if="errors.trackerId" class="field-error">{{ errors.trackerId }}</small>
        <small v-else class="field-hint"
          >Redmine's trackers; a single one is listed under its project's every tracker.</small
        >
      </div>
      <div class="field">
        <label for="c-user">Who handles it</label>
        <PersonPicker
          v-model="draft.userId"
          input-id="c-user"
          :current="combination?.user"
          placeholder="Choose a person"
          :invalid="!!errors.userId"
        />
        <small v-if="errors.userId" class="field-error">{{ errors.userId }}</small>
      </div>
      <div class="field">
        <label for="c-note">Note</label>
        <InputText
          id="c-note"
          v-model="draft.note"
          :invalid="!!errors.note"
          placeholder="Support desk only during the migration"
        />
        <small v-if="errors.note" class="field-error">{{ errors.note }}</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="combination-form" :label="combination ? 'Save' : 'Add combination'" />
    </template>
  </Dialog>
</template>
