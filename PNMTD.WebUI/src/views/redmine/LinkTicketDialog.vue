<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { ref } from 'vue'
import { api, keys, type RedmineTarget, type S } from '@/api/resources'
import { useActions } from '@/composables/useActions'

/** *Link ticket…* (§6.6): a ticket found by number or text, linked to this object, with a note. */
const props = defineProps<{ target: RedmineTarget; name: string }>()
const visible = defineModel<boolean>('visible', { default: false })
const queryClient = useQueryClient()
const { run } = useActions()

const picked = ref<S['RedmineIssueItem'] | string>('')
const suggestions = ref<S['RedmineIssueItem'][]>([])
const note = ref('')
async function complete(event: { query: string }) {
  const q = event.query.trim()
  suggestions.value = q ? (await api.redmine.issues({ search: q, status: 'all', pageSize: 10 })).items : []
}
async function link() {
  if (typeof picked.value === 'string') return
  const ticket = picked.value
  const done = await run(
    () => api.redmine.link({ issueId: ticket.id, ...props.target, note: note.value.trim() || null }),
    {
      success: `#${ticket.id} linked to ${props.name}`,
      failure: 'Not linked',
    },
  )
  if (done) {
    await queryClient.invalidateQueries({ queryKey: keys.redmine })
    visible.value = false
    picked.value = ''
    note.value = ''
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="`Link a ticket to ${name}`" modal :style="{ width: 'min(34rem, 100vw)' }">
    <form id="link-ticket" class="form" novalidate @submit.prevent="link">
      <div class="field">
        <label for="lt-ticket">Ticket</label>
        <AutoComplete
          v-model="picked"
          input-id="lt-ticket"
          :suggestions="suggestions"
          :option-label="(t: S['RedmineIssueItem']) => `#${t.id} ${t.subject}`"
          placeholder="A number (1234) or words of its subject"
          :delay="250"
          fluid
          autofocus
          @complete="complete"
        >
          <template #option="{ option }">
            <span class="mono">#{{ option.id }}</span
            >&nbsp;{{ option.subject }}
            <span class="muted">· {{ option.status }}</span>
          </template>
        </AutoComplete>
      </div>
      <div class="field">
        <label for="lt-note">Note</label>
        <InputText id="lt-note" v-model="note" maxlength="500" placeholder="Why it is linked (optional)" />
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="link-ticket" label="Link" :disabled="typeof picked === 'string'" />
    </template>
  </Dialog>
</template>
