<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'
import { logonKindLabel } from '@/views/admin/servers/agentFormat'
import { caseLink } from '@/views/cookie/access'

/**
 * This wasn't expected (docs/cookie-access-plan.md §7), on a server's or router's successful login: its access case is
 * answered Not expected, and stays open with importance High. That is how a missed break-in teaches Cookie. Monitoring:
 * Edit. Afterwards, the case is a click away.
 */
const login = defineModel<S['AgentLoginItem'] | null>('login', { required: true })
const props = defineProps<{ invalidate: readonly unknown[] }>()
const queryClient = useQueryClient()
const router = useRouter()
const notify = useNotify()
const comment = ref('')
const error = ref<string | undefined>()
const busy = ref(false)
const caseId = ref<string | null>(null)
const open = computed({
  get: () => login.value !== null,
  set: (v: boolean) => {
    if (!v) login.value = null
  },
})
watch(login, () => {
  comment.value = ''
  error.value = undefined
  caseId.value = null
})

const described = computed(() => {
  const l = login.value
  if (!l) return ''
  const who = l.domain ? `${l.domain}\\${l.user}` : l.user
  return `${who} by ${logonKindLabel(l.kind)}${l.sourceAddress ? ` from ${l.sourceAddress}` : ''}, ${formatAbsolute(l.at)}`
})

async function confirm() {
  if (!login.value) return
  busy.value = true
  error.value = undefined
  try {
    const result = await api.cookie.notExpected(login.value.id, comment.value.trim() || null)
    caseId.value = result.id
    notify.success('Not expected', 'Its access case stays open, important.')
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: [...props.invalidate] }),
      queryClient.invalidateQueries({ queryKey: keys.cookie }),
    ])
  } catch (e) {
    if (e instanceof ApiError && e.field('comment')) error.value = e.field('comment')
    else notify.error(e, 'Not done')
  } finally {
    busy.value = false
  }
}

function openCase() {
  const id = caseId.value
  login.value = null
  if (id) void router.push(caseLink(id))
}
</script>

<template>
  <Dialog v-model:visible="open" header="This wasn't expected" modal :style="{ width: 'min(30rem, 95vw)' }">
    <p v-if="caseId" class="text">
      Done: the access case is answered Not expected and stays open, with importance High. Cookie writes a note in its
      next round and proposes nothing; a ticket only comes when someone asks for one.
    </p>
    <form v-else id="not-expected-form" class="form" novalidate @submit.prevent="confirm">
      <p class="text">
        <strong>{{ described }}</strong>: its access case is answered Not expected, and stays open with importance High.
        Cookie learns from it.
      </p>
      <div class="field">
        <label for="not-expected-comment">A comment (if you like)</label>
        <Textarea
          id="not-expected-comment"
          v-model="comment"
          rows="2"
          auto-resize
          maxlength="1000"
          autofocus
          :invalid="!!error"
        />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
    </form>
    <template #footer>
      <template v-if="caseId">
        <Button label="Close" severity="secondary" text @click="login = null" />
        <Button label="Open the case" icon="pi pi-arrow-right" @click="openCase" />
      </template>
      <template v-else>
        <Button label="Cancel" severity="secondary" text @click="login = null" />
        <Button type="submit" form="not-expected-form" label="Not expected" severity="warn" :loading="busy" />
      </template>
    </template>
  </Dialog>
</template>

<style scoped>
.text {
  margin-top: 0;
  word-break: break-word;
}
</style>
