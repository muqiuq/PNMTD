<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { credentialLines, kbApi, kbKeys, kbRequest, linesText, type KbAiStatus, type KbPage } from '@/api/kb'
import { useNotify } from '@/composables/useNotify'

/**
 * The AI tag question (docs/locations-knowledge-plan.md §7.2): what is sent where, the two statements (never checked in
 * advance, never remembered), *Mark as sensitive* instead, *Not now*. Then the scan runs as a work item, polled until
 * it ends; its tags come with *Undo*. `mode="unmark"`: the same statements unmark a sensitive page (§7.1).
 */
const props = withDefaults(defineProps<{ page: KbPage; mode?: 'scan' | 'unmark' }>(), { mode: 'scan' })
const visible = defineModel<boolean>('visible', { required: true })
const emit = defineEmits<{ changed: [page: KbPage] }>()

const queryClient = useQueryClient()
const notify = useNotify()
const noCredentials = ref(false)
const noPersonalData = ref(false)
const busy = ref(false)
const problem = ref<string | null>(null)
const status = ref<KbAiStatus | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined

watch(visible, (open) => {
  if (open) {
    noCredentials.value = false
    noPersonalData.value = false
    problem.value = null
    status.value = null
  } else {
    clearTimeout(timer)
  }
})
onBeforeUnmount(() => clearTimeout(timer))

const both = computed(() => noCredentials.value && noPersonalData.value)
const running = computed(
  () => status.value !== null && !['Succeeded', 'Failed', 'Cancelled'].includes(status.value.state),
)
const finished = computed(() => status.value !== null && !running.value)

async function refresh(page: KbPage) {
  queryClient.setQueryData(kbKeys.page(page.number), page)
  await queryClient.invalidateQueries({ queryKey: kbKeys.all })
  emit('changed', page)
}

function explain(e: unknown): string {
  const lines = credentialLines(e)
  if (lines) {
    return `${linesText(lines)} ${lines.length === 1 ? 'looks' : 'look'} like a credential: nothing was sent. Edit the page, or mark it sensitive.`
  }
  if (e instanceof ApiError && e.code === 'edit_conflict') {
    return 'Someone saved the page meanwhile. Read it again, then ask once more.'
  }
  return e instanceof Error ? e.message : String(e)
}

async function start() {
  busy.value = true
  problem.value = null
  try {
    const statements = { noCredentials: true, noPersonalData: true }
    if (props.mode === 'unmark') {
      const page = await kbApi.sensitive(props.page.id, { sensitive: false, revision: props.page.revision, statements })
      notify.success(`KB-${page.number} is no longer sensitive`)
      await refresh(page)
      visible.value = false
      return
    }
    status.value = await kbApi.startAiTags(props.page.id, { revision: props.page.revision, statements })
    poll()
  } catch (e) {
    problem.value = explain(e)
  } finally {
    busy.value = false
  }
}

function poll() {
  clearTimeout(timer)
  timer = setTimeout(async () => {
    if (!status.value || !visible.value) return
    try {
      status.value = await kbApi.aiTags(props.page.id, status.value.workId)
    } catch (e) {
      problem.value = explain(e)
      return
    }
    if (running.value) {
      poll()
    } else if (status.value.added.length) {
      await refresh(await kbApi.get(props.page.number))
    }
  }, 1500)
}

async function markSensitive() {
  busy.value = true
  try {
    const page = await kbApi.sensitive(props.page.id, { sensitive: true })
    notify.success(`KB-${page.number} is sensitive: never sent to AI or MCP`)
    await refresh(page)
    visible.value = false
  } catch (e) {
    problem.value = explain(e)
  } finally {
    busy.value = false
  }
}

/** Undo: the tags the scan added, removed again in a revision of its own. */
async function undo() {
  const added = status.value?.added ?? []
  busy.value = true
  try {
    const page = await kbApi.get(props.page.number)
    const saved = await kbApi.update(
      page.id,
      kbRequest(page, {
        tags: page.tags.map((t) => t.name).filter((t) => !added.includes(t)),
        note: 'AI tags undone',
      }),
    )
    notify.success(`${added.length} AI tag${added.length === 1 ? '' : 's'} removed`)
    await refresh(saved.page)
    visible.value = false
  } catch (e) {
    problem.value = explain(e)
  } finally {
    busy.value = false
  }
}

const resultText = computed(() => {
  const s = status.value
  if (!s) return ''
  if (s.state === 'Succeeded' && s.added.length) {
    return `${s.added.length} tag${s.added.length === 1 ? '' : 's'} added: ${s.added.join(', ')}.`
  }
  return s.message ?? (s.state === 'Succeeded' ? 'No new tags.' : `The scan ended: ${s.state.toLowerCase()}.`)
})
</script>

<template>
  <Dialog
    v-model:visible="visible"
    modal
    :header="mode === 'unmark' ? 'Unmark as sensitive' : 'Find tags with AI'"
    :style="{ width: '34rem' }"
    :breakpoints="{ '600px': '95vw' }"
  >
    <template v-if="!status">
      <p v-if="mode === 'unmark'" class="intro">
        Unmarked, the page can be sent to the AI service PNMT uses, and later shown to MCP. Confirm that this page
        contains:
      </p>
      <p v-else class="intro">
        To suggest up to 10 tags, the page's text is sent to the AI service PNMT uses (OpenRouter). Files are never
        sent. Confirm that this page contains:
      </p>
      <div class="statements">
        <label class="statement">
          <Checkbox v-model="noCredentials" binary input-id="kb-no-credentials" />
          <span>no credentials: no passwords, keys, tokens or codes</span>
        </label>
        <label class="statement">
          <Checkbox v-model="noPersonalData" binary input-id="kb-no-personal" />
          <span>no personal data, and no confidential or IP-protected information</span>
        </label>
      </div>
      <template v-if="mode === 'scan'">
        <p class="muted or">Or keep it away from AI and MCP for good:</p>
        <Button
          label="Mark as sensitive"
          icon="pi pi-lock"
          severity="secondary"
          outlined
          size="small"
          :loading="busy"
          @click="markSensitive"
        />
      </template>
    </template>
    <div v-else-if="running" class="running">
      <ProgressSpinner style="width: 28px; height: 28px" />
      <span>{{ status.state === 'Queued' ? 'Waiting for the AI…' : 'The AI is reading the page…' }}</span>
    </div>
    <Message v-else-if="finished" :severity="status.added.length ? 'success' : status.blockedBy ? 'warn' : 'info'">
      {{ resultText }}
    </Message>
    <Message v-if="problem" severity="warn" class="problem">{{ problem }}</Message>

    <template #footer>
      <template v-if="!status">
        <Button label="Not now" text severity="secondary" @click="visible = false" />
        <Button
          :label="mode === 'unmark' ? 'Unmark' : 'Find tags'"
          :icon="mode === 'unmark' ? 'pi pi-lock-open' : 'pi pi-sparkles'"
          :disabled="!both"
          :loading="busy"
          @click="start"
        />
      </template>
      <template v-else>
        <Button v-if="finished && status.added.length" label="Undo" text :loading="busy" @click="undo" />
        <Button :label="running ? 'Close' : 'Done'" @click="visible = false" />
      </template>
    </template>
  </Dialog>
</template>

<style scoped>
.intro {
  margin-top: 0;
}

.statements {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  margin: var(--pnmt-space-3) 0;
}

.statement {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
  cursor: pointer;
}

.or {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
}

.running {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.problem {
  margin-top: var(--pnmt-space-3);
}
</style>
