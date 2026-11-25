<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import {
  responsibilitiesApi,
  responsibilityKeys,
  type ResponsibilityMapNote,
  type ResponsibilityNoteColour,
  type ResponsibilityNoteWidth,
} from '@/api/responsibilities'
import type { Point } from '@/components/graph/forceLayout'
import { useNotify } from '@/composables/useNotify'
import { noteWidths } from './overviewLayout'

/**
 * A note on the overview (docs/responsibilities-extensions-plan.md §7.1): short Markdown, as a board's note, a colour and a width. A
 * new one goes in the middle of the view (`at`), its place saved with it; a note's place changes with Save layout. A text that looks
 * like a password is saved only once that is acknowledged.
 */
const props = defineProps<{ note: ResponsibilityMapNote | null; at: Point | null }>()
const visible = defineModel<boolean>('visible', { required: true })
const emit = defineEmits<{ saved: [id: string] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const MAX = 2000
const colours: { value: ResponsibilityNoteColour; label: string }[] = [
  { value: 'Yellow', label: 'Yellow' },
  { value: 'Green', label: 'Green' },
  { value: 'Blue', label: 'Blue' },
  { value: 'Pink', label: 'Pink' },
  { value: 'Purple', label: 'Purple' },
  { value: 'Grey', label: 'Grey' },
]
const widths: { value: ResponsibilityNoteWidth; label: string }[] = [
  { value: 'Small', label: 'Small' },
  { value: 'Medium', label: 'Medium' },
  { value: 'Large', label: 'Large' },
]
const text = ref('')
const colour = ref<ResponsibilityNoteColour>('Yellow')
const width = ref<ResponsibilityNoteWidth>('Medium')
const error = ref<string | null>(null)
const warning = ref<string | null>(null)
const saving = ref(false)
watch(visible, (open) => {
  if (!open) return
  text.value = props.note?.text ?? ''
  colour.value = props.note?.colour ?? 'Yellow'
  width.value = props.note?.width ?? 'Medium'
  error.value = null
  warning.value = null
})
const length = computed(() => text.value.trim().length)
const round = (v: number) => Math.round(v * 100) / 100

async function save(acknowledgeCredentials = false) {
  error.value = length.value
    ? length.value > MAX
      ? `At most ${MAX.toLocaleString()} characters.`
      : null
    : 'A text is needed.'
  if (error.value) return
  saving.value = true
  try {
    const body = { text: text.value, colour: colour.value, width: width.value, acknowledgeCredentials }
    let id: string
    if (props.note) {
      id = (await responsibilitiesApi.updateNote(props.note.id, body)).id
    } else {
      // The middle of the view: the note's top centre a little above it.
      const at = props.at ?? { x: 0, y: 0 }
      id = (
        await responsibilitiesApi.createNote({
          ...body,
          x: round(at.x - noteWidths[width.value] / 2),
          y: round(at.y - 30),
        })
      ).id
    }
    notify.success(props.note ? 'Note saved' : 'Note added')
    await queryClient.invalidateQueries({ queryKey: responsibilityKeys.all })
    visible.value = false
    emit('saved', id)
  } catch (e) {
    if (e instanceof ApiError && e.code === 'looks_like_credential') warning.value = e.message
    else if (e instanceof ApiError && e.field('text')) error.value = e.field('text') ?? null
    else notify.error(e, 'Note not saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="note ? 'Edit note' : 'New note'" modal :style="{ width: '32rem' }">
    <form id="note-form" class="form" novalidate @submit.prevent="save()">
      <div class="field">
        <label for="note-text">Text</label>
        <Textarea
          id="note-text"
          v-model="text"
          rows="6"
          auto-resize
          :maxlength="MAX"
          :invalid="!!error"
          placeholder="Infra team: ask Anna before changing the firewall"
          data-test="note-text"
        />
        <small class="field-hint"
          >Markdown: **bold**, *italics*, lists and links; no images or HTML. {{ length.toLocaleString() }} /
          {{ MAX.toLocaleString() }}</small
        >
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div class="field">
        <span class="label">Colour</span>
        <div class="papers" role="group" aria-label="Colour">
          <button
            v-for="c in colours"
            :key="c.value"
            type="button"
            class="paper"
            :class="`note-${c.value.toLowerCase()}`"
            :title="c.label"
            :aria-label="c.label"
            :aria-pressed="colour === c.value"
            @click="colour = c.value"
          >
            <i v-if="colour === c.value" class="pi pi-check" aria-hidden="true" />
          </button>
        </div>
      </div>
      <div class="field">
        <span class="label">Width</span>
        <SelectButton
          v-model="width"
          :options="widths"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          aria-label="Width"
        />
      </div>
      <div v-if="warning" class="warning" role="alert">
        <p><i class="pi pi-exclamation-triangle" aria-hidden="true" /> {{ warning }}</p>
        <Button label="Save anyway" size="small" severity="warn" :loading="saving" @click="save(true)" />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="note-form"
        :label="note ? 'Save' : 'Add note'"
        :loading="saving"
        data-test="note-save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}

.papers {
  display: flex;
  gap: var(--pnmt-space-1);
}

.paper {
  width: 2rem;
  height: 2rem;
  padding: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-sm);
  color: var(--pnmt-text);
  cursor: pointer;
}

.paper[aria-pressed='true'],
.paper:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.note-yellow {
  background: var(--pnmt-note-yellow);
}

.note-green {
  background: var(--pnmt-note-green);
}

.note-blue {
  background: var(--pnmt-note-blue);
}

.note-pink {
  background: var(--pnmt-note-pink);
}

.note-purple {
  background: var(--pnmt-note-purple);
}

.note-grey {
  background: var(--pnmt-note-grey);
}

.warning {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-warn);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-warn-soft);
}

.warning p {
  margin: 0;
}
</style>
