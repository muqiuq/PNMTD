<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import { lastId } from '@/views/cookie/trace'
import { usePoll } from '@/views/cookie/usePoll'
import LoungeLine from './LoungeLine.vue'
import LoungeSide from './LoungeSide.vue'
import { author, dayOf, flow, merge, stateLine, timeOf, type LoungeMessage } from './loungeFormat'

/**
 * The lounge (docs/lounge-plan.md §15.2): one conversation, the same for every signed-in user, where Cookie and Seraph talk
 * off duty. People with Lounge: Use write in it while it is started: a message the voices answer, or a comment they take
 * into their conversation (§9.3), on one message or on none. Asked every 10 s, every 2 s while a message waits or a voice
 * is typing; with afterId the server also sends older lines that changed since (a comment taken up, a line removed), which
 * replace theirs by id. Stopped, the room stays readable and the composer goes. The side panel: keepsakes and convictions.
 */
type Kind = 'message' | 'comment'
const messages = ref<LoungeMessage[]>([])
const state = ref<S['LoungeStateItem'] | null>(null)
const cookieName = ref('Cookie')
const about = ref<S['LoungeAbout'] | null>(null)
const loaded = ref(false)
const loadError = ref<unknown>(null)
const list = ref<HTMLElement | null>(null)

async function fetchRoom() {
  try {
    // After the first read, what came since and what changed in place (taken up, removed): merged by id.
    const room = await api.lounge.room(lastId(messages.value))
    messages.value = merge(messages.value, room.messages)
    state.value = room.state
    cookieName.value = room.cookieName
    loaded.value = true
    loadError.value = null
  } catch (e) {
    loadError.value = e
  }
}
const busy = computed(() => !!state.value && (state.value.waiting || state.value.state === 'Speaking'))
const poll = usePoll(fetchRoom, () => (busy.value ? 2_000 : 10_000))

async function fetchAbout() {
  try {
    about.value = await api.lounge.about()
  } catch {
    // The side panel is a bonus: the room works without it.
  }
}
const aboutPoll = usePoll(fetchAbout, () => 60_000)
onMounted(() => {
  void poll.restart()
  void aboutPoll.restart()
})

const entries = computed(() => flow(messages.value))
const speaker = computed(() =>
  state.value?.speaking === 'seraph' ? 'Seraph' : state.value?.speaking === 'cookie' ? cookieName.value : null,
)
const header = computed(() => (state.value ? stateLine(state.value, speaker.value) : ''))
const headerSeverity = computed(() => {
  switch (state.value?.state) {
    case 'Awake':
    case 'Speaking':
      return 'success'
    case 'Paused':
      return 'warn'
    case 'Asleep':
      return 'info'
    default:
      return 'secondary'
  }
})
const stopped = computed(() => state.value?.state === 'Stopped')
const canWrite = computed(() => !!state.value?.canWrite)

// The newest at the bottom, in view.
watch(
  () => [messages.value.length, state.value?.speaking],
  async () => {
    await nextTick()
    if (list.value) list.value.scrollTop = list.value.scrollHeight
  },
)

// --- the composer (§9.2, §9.3) --------------------------------------------------------------------------------------
const kind = ref<Kind>('message')
const onId = ref<number | null>(null)
const text = ref('')
const sending = ref(false)
const problem = ref<string | null>(null)
const input = ref<{ $el?: HTMLElement } | null>(null)
const kinds = [
  { label: 'Message', value: 'message' },
  { label: 'Comment', value: 'comment' },
]
const max = computed(() => (kind.value === 'comment' ? 500 : 1000))
const tooLong = computed(() => text.value.trim().length > max.value)
const target = computed(() => (onId.value == null ? null : (messages.value.find((m) => m.id === onId.value) ?? null)))
watch(kind, (k) => {
  if (k === 'message') onId.value = null
})

/** A message's Comment action: the composer as a comment on it. */
async function commentOn(m: LoungeMessage) {
  kind.value = 'comment'
  onId.value = m.id
  problem.value = null
  await nextTick()
  const el = input.value?.$el as HTMLTextAreaElement | undefined
  el?.focus?.()
}

/** @Cookie or @Seraph at the start of a message: that voice answers (§9.2). */
function address(voice: 'cookie' | 'seraph') {
  const name = voice === 'seraph' ? 'Seraph' : cookieName.value
  text.value = `@${name} ${text.value.replace(/^@\S+\s*/, '')}`
}

const asleepHint = computed(() => {
  const s = state.value
  if (s?.state !== 'Asleep' || !s.nextAt) return null
  const today = dayOf(new Date().toISOString())
  const when = dayOf(s.nextAt) === today ? timeOf(s.nextAt) : `${dayOf(s.nextAt)}, ${timeOf(s.nextAt)}`
  return `They're asleep. They'll read it at ${when}.`
})

/** The server's refusals in plain words. */
function explain(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.code === 'lounge_stopped') return 'The lounge is closed: an admin starts it on Admin → Cookie → Lounge.'
    if (e.code === 'lounge_too_many_messages')
      return "That's 20 messages and comments in an hour: try again a little later."
    if (e.field('text')) return e.field('text')!
    if (e.field('onId')) return e.field('onId')!
    return e.message
  }
  return e instanceof Error ? e.message : String(e)
}

async function send() {
  const message = text.value.trim()
  if (!message || tooLong.value || sending.value) return
  sending.value = true
  problem.value = null
  try {
    const sent = await api.lounge.post({
      text: message,
      kind: kind.value,
      onId: kind.value === 'comment' ? onId.value : null,
    })
    messages.value = merge(messages.value, [sent])
    if (state.value && kind.value === 'message' && !sent.refused) state.value = { ...state.value, waiting: true }
    text.value = ''
    onId.value = null
    kind.value = 'message'
    poll.schedule()
  } catch (e) {
    problem.value = explain(e)
    if (e instanceof ApiError && e.code === 'lounge_stopped' && state.value)
      state.value = { ...state.value, state: 'Stopped', canWrite: false }
  } finally {
    sending.value = false
  }
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void send()
  }
}

const targetLabel = computed(() =>
  target.value ? `${author(target.value, cookieName.value)}'s message of ${timeOf(target.value.at)}` : null,
)
</script>

<template>
  <div class="page">
    <PageHeader title="Lounge" />
    <div class="room">
      <section class="panel conversation" aria-label="The conversation">
        <div class="head">
          <span class="muted small">Cookie and Seraph, off duty. Everyone reads the same conversation.</span>
          <Tag v-if="state" :value="header" :severity="headerSeverity" class="state-tag" />
        </div>
        <ErrorState v-if="loadError && !loaded" :error="loadError" what="the lounge" @retry="poll.restart()" />
        <div v-else-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
        <template v-else>
          <div ref="list" class="thread" role="log" aria-live="polite">
            <p v-if="!entries.length" class="muted empty">Nothing said yet.</p>
            <template v-for="e in entries" :key="e.message.id">
              <div v-if="e.day" class="day" role="separator">
                <span>{{ e.day }}</span>
              </div>
              <p v-if="e.message.from === 'system'" class="system muted small">{{ e.message.text }}</p>
              <LoungeLine
                v-else
                :message="e.message"
                :comments="e.comments"
                :cookie-name="cookieName"
                :can-comment="canWrite"
                @comment="commentOn"
              />
            </template>
            <div v-if="speaker" class="typing muted small">
              <i class="pi pi-spin pi-spinner" aria-hidden="true" /> {{ speaker }} is typing…
            </div>
          </div>
          <Message v-if="stopped" severity="info" :closable="false" size="small" class="note">
            The lounge is closed. The conversation stays readable.
          </Message>
          <Message
            v-else-if="state?.state === 'Paused' && state.reason"
            severity="warn"
            :closable="false"
            size="small"
            class="note"
          >
            {{ state.reason }}
          </Message>
          <form v-if="canWrite" class="compose" novalidate @submit.prevent="send">
            <div class="compose-top">
              <SelectButton
                v-model="kind"
                :options="kinds"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                aria-label="Write a message or a comment"
              />
              <template v-if="kind === 'message'">
                <Button label="@Cookie" size="small" text @click="address('cookie')" />
                <Button label="@Seraph" size="small" text @click="address('seraph')" />
              </template>
              <span v-if="targetLabel" class="on small">
                On {{ targetLabel }}
                <Button
                  icon="pi pi-times"
                  text
                  rounded
                  size="small"
                  aria-label="Comment on the conversation instead"
                  @click="onId = null"
                />
              </span>
            </div>
            <div class="compose-row">
              <Textarea
                ref="input"
                v-model="text"
                rows="2"
                auto-resize
                class="input"
                :placeholder="kind === 'comment' ? 'A comment for them to talk about' : 'Write to Cookie and Seraph'"
                :aria-label="kind === 'comment' ? 'Your comment' : 'Your message'"
                :invalid="tooLong"
                @keydown="onKey"
              />
              <Button
                type="submit"
                label="Send"
                icon="pi pi-send"
                :loading="sending"
                :disabled="!text.trim() || tooLong"
              />
            </div>
            <small v-if="problem" class="field-error" role="alert">{{ problem }}</small>
            <small v-else-if="tooLong" class="field-error">At most {{ max.toLocaleString() }} characters.</small>
            <small v-else-if="asleepHint" class="field-hint asleep">{{ asleepHint }}</small>
            <small class="field-hint">
              A message is answered; a comment is something they talk about. Everyone reads this, and Cookie and Seraph
              may remember it.
            </small>
          </form>
        </template>
      </section>
      <LoungeSide class="panel side-panel" :about="about" />
    </div>
  </div>
</template>

<style scoped>
.room {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  gap: var(--pnmt-space-3);
  align-items: start;
}

@media (max-width: 899px) {
  .room {
    grid-template-columns: minmax(0, 1fr);
  }
}

.conversation {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.thread {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-3);
  min-height: 16rem;
  max-height: 65vh;
  overflow-y: auto;
}

.empty {
  margin: auto 0;
  text-align: center;
}

.day {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  color: var(--pnmt-text-muted, inherit);
  font-size: 0.857rem;
}

.day::before,
.day::after {
  content: '';
  flex: 1;
  border-top: 1px solid var(--pnmt-border);
}

.system {
  margin: 0;
  text-align: center;
  font-style: italic;
}

.typing {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.note {
  margin: 0 var(--pnmt-space-3) var(--pnmt-space-2);
}

.compose {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-3);
  border-top: 1px solid var(--pnmt-border);
}

.compose-top {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.compose-row {
  display: flex;
  align-items: flex-end;
  gap: var(--pnmt-space-2);
}

.input {
  flex: 1;
  max-height: 30vh;
}

.on {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.asleep {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}
</style>
