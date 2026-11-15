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
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import {
  instructionKindLabel,
  instructionState,
  instructionStateSeverity,
  instructionTarget,
  type InstructionItem,
} from './acting'
import { lastId } from './trace'
import { usePoll } from './usePoll'

/**
 * The chat with Cookie and Seraph (docs/cookie-plan.md §10.2, §21.1): one thread per person, newest at the bottom. A
 * message wakes Cookie; while it waits for a reply the thread is asked every 2 s, otherwise every 30 s. Cookie answers
 * with the lower of the person's rights and its own; Seraph, picked for a message, explains its verdicts from the
 * decision log and can't act. For someone with Cookie: Instruct, Cookie fills in a mute, a suppress or a note from the
 * message as a draft, which counts only once the person confirms it here.
 */
type Item = S['CookieMessageItem']
type To = 'cookie' | 'seraph'
const maxChars = 4000
const session = useSession()
const notify = useNotify()
const now = useNow()
const canInstruct = computed(() => session.can('Cookie', 'Instruct'))
const to = ref<To>('cookie')
const recipients = [
  { label: 'Cookie', value: 'cookie' },
  { label: 'Seraph', value: 'seraph' },
]

const messages = ref<Item[]>([])
const waiting = ref(false)
const state = ref<S['CookieState'] | null>(null)
const loaded = ref(false)
const loadError = ref<unknown>(null)
const text = ref('')
const sending = ref(false)
const problem = ref<string | null>(null)
const list = ref<HTMLElement | null>(null)

async function fetchThread() {
  try {
    const thread = await api.cookie.messages(lastId(messages.value))
    const byId = new Map(messages.value.map((m) => [m.id, m]))
    for (const m of thread.messages) byId.set(m.id, m)
    messages.value = [...byId.values()].sort((a, b) => a.id - b.id)
    waiting.value = thread.waiting
    state.value = thread.state
    loaded.value = true
    loadError.value = null
  } catch (e) {
    loadError.value = e
  }
}
const poll = usePoll(fetchThread, () => (waiting.value ? 2_000 : 30_000))
onMounted(() => void poll.restart())

// The newest at the bottom, in view.
watch(
  () => [messages.value.length, waiting.value],
  async () => {
    await nextTick()
    if (list.value) list.value.scrollTop = list.value.scrollHeight
  },
)

const stopped = computed(() => state.value === 'Stopped')
/** Who is answering: whoever the last message waiting for a reply went to. */
const thinker = computed(() => {
  const last = [...messages.value].reverse().find((m) => m.from === 'person' && !m.answered)
  return last?.to === 'seraph' ? 'Seraph' : 'Cookie'
})
const who = (m: Item) =>
  m.from === 'person' ? (m.to === 'seraph' ? 'You, to Seraph' : 'You') : m.from === 'seraph' ? 'Seraph' : 'Cookie'
const side = (m: Item) => (m.from === 'person' ? 'mine' : m.from === 'seraph' ? 'seraph' : 'cookie')
const tooLong = computed(() => text.value.trim().length > maxChars)

/** The server's refusals in plain words. */
function explain(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.code === 'cookie_stopped') return 'Cookie is stopped: an admin starts it on Admin → Cookie.'
    if (e.code === 'cookie_too_many_messages')
      return "That's as many messages as Cookie takes in an hour. Try again a little later."
    if (e.field('text')) return e.field('text')!
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
    const sent = await api.cookie.post(message, to.value)
    messages.value = [...messages.value, sent]
    waiting.value = true
    text.value = ''
    poll.schedule()
  } catch (e) {
    problem.value = explain(e)
    if (e instanceof ApiError && e.code === 'cookie_stopped') state.value = 'Stopped'
  } finally {
    sending.value = false
  }
}

// --- a draft instruction from the chat (§10.3) -----------------------------------------------------------------------
const acting = ref<string | null>(null)
async function instruct(m: Item, what: 'confirm' | 'end') {
  const i = m.instruction
  if (!i) return
  acting.value = i.id
  try {
    const next = what === 'confirm' ? await api.cookie.confirmInstruction(i.id) : await api.cookie.endInstruction(i.id)
    messages.value = messages.value.map((x) => (x.id === m.id ? { ...x, instruction: next } : x))
    notify.success(
      what === 'confirm' ? 'Confirmed' : 'Ended',
      what === 'confirm' ? `In force until ${formatAbsolute(next.until)}.` : undefined,
    )
  } catch (e) {
    notify.error(e, what === 'confirm' ? 'Not confirmed' : 'Not ended')
  } finally {
    acting.value = null
  }
}
const stateOf = (i: InstructionItem) => instructionState(i, now.value)

function onKey(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void send()
  }
}
</script>

<template>
  <section class="panel chat" aria-label="The chat with Cookie">
    <p class="muted small note">
      Cookie answers with the lower of your rights and its own. Seraph, which reviews what Cookie sends, explains its
      verdicts and can't act.
    </p>
    <ErrorState v-if="loadError && !loaded" :error="loadError" what="the chat" @retry="poll.restart()" />
    <div v-else-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <div ref="list" class="thread" role="log" aria-live="polite">
        <p v-if="!messages.length" class="muted empty">
          Ask Cookie about a case, a host, a ticket, or what happened overnight.
        </p>
        <div v-for="m in messages" :key="m.id" class="message" :class="side(m)">
          <div class="who small muted">
            <i v-if="m.from === 'seraph'" class="pi pi-shield" aria-hidden="true" />
            {{ who(m) }} · <RelativeTime :value="m.at" />
          </div>
          <div class="bubble">{{ m.text }}</div>
          <div
            v-if="m.instruction"
            class="instruction"
            role="group"
            :aria-label="`${instructionKindLabel(m.instruction.kind)}, ${stateOf(m.instruction)}`"
          >
            <div class="line">
              <Tag :value="instructionKindLabel(m.instruction.kind)" severity="secondary" />
              <span v-if="instructionTarget(m.instruction)" class="target">{{ instructionTarget(m.instruction) }}</span>
              <Tag
                :value="stateOf(m.instruction)"
                :severity="instructionStateSeverity(stateOf(m.instruction))"
                class="end"
              />
            </div>
            <div v-if="m.instruction.text" class="wrap">{{ m.instruction.text }}</div>
            <div class="muted small">Until {{ formatAbsolute(m.instruction.until) }}</div>
            <template v-if="canInstruct">
              <div v-if="stateOf(m.instruction) === 'draft'" class="line">
                <Button
                  label="Confirm"
                  icon="pi pi-check"
                  size="small"
                  :loading="acting === m.instruction.id"
                  :disabled="acting !== null"
                  @click="instruct(m, 'confirm')"
                />
                <span class="muted small">A draft: nothing counts until you confirm it.</span>
              </div>
              <div v-else-if="stateOf(m.instruction) === 'active'" class="line">
                <Button
                  label="End now"
                  icon="pi pi-stop"
                  size="small"
                  severity="secondary"
                  outlined
                  :loading="acting === m.instruction.id"
                  :disabled="acting !== null"
                  @click="instruct(m, 'end')"
                />
                <span class="muted small">In force: code holds back what it covers.</span>
              </div>
            </template>
          </div>
        </div>
        <div v-if="waiting" class="message thinking" :class="thinker === 'Seraph' ? 'seraph' : 'cookie'">
          <div class="bubble muted">
            <i class="pi pi-spin pi-spinner" aria-hidden="true" /> {{ thinker }} is thinking…
          </div>
        </div>
      </div>
      <Message v-if="stopped" severity="info" :closable="false" size="small" class="state">
        Cookie is stopped: an admin starts it on Admin → Cookie. Until then you can read your thread, but not write.
      </Message>
      <form class="compose" novalidate @submit.prevent="send">
        <SelectButton
          v-model="to"
          :options="recipients"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="to"
          aria-label="Write to"
          :disabled="stopped"
        />
        <Textarea
          v-model="text"
          rows="2"
          auto-resize
          class="input"
          :placeholder="to === 'seraph' ? 'Ask Seraph about its verdicts' : 'Write to Cookie'"
          :aria-label="to === 'seraph' ? 'Your message to Seraph' : 'Your message to Cookie'"
          :disabled="stopped"
          :invalid="tooLong"
          @keydown="onKey"
        />
        <Button
          type="submit"
          label="Send"
          icon="pi pi-send"
          :loading="sending"
          :disabled="stopped || !text.trim() || tooLong"
        />
      </form>
      <small v-if="problem" class="field-error problem" role="alert">{{ problem }}</small>
      <small v-else-if="tooLong" class="field-error problem">At most {{ maxChars.toLocaleString() }} characters.</small>
      <small v-else class="field-hint problem">Enter sends; Shift+Enter starts a new line.</small>
    </template>
  </section>
</template>

<style scoped>
.chat {
  display: flex;
  flex-direction: column;
  max-width: 960px;
}

.note {
  margin: 0;
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
  min-height: 12rem;
  max-height: 60vh;
  overflow-y: auto;
}

.empty {
  margin: auto 0;
  text-align: center;
}

.message {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-width: 80%;
}

.mine {
  align-self: flex-end;
  align-items: flex-end;
}

.cookie,
.seraph {
  align-self: flex-start;
}

.bubble {
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-radius: var(--p-border-radius-md);
  white-space: pre-wrap;
  word-break: break-word;
  background: var(--pnmt-none-soft);
  border: 1px solid var(--pnmt-border);
}

.mine .bubble {
  background: color-mix(in srgb, var(--p-primary-color) 12%, transparent);
  border-color: color-mix(in srgb, var(--p-primary-color) 30%, transparent);
}

.seraph .bubble {
  background: color-mix(in srgb, var(--p-violet-500) 10%, transparent);
  border-color: color-mix(in srgb, var(--p-violet-500) 35%, transparent);
}

.seraph .who {
  color: var(--p-violet-700);
}

:root.pnmt-dark .seraph .who {
  color: var(--p-violet-300);
}

.instruction {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  margin-top: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px dashed var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
  min-width: min(24rem, 100%);
}

.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.target {
  font-weight: 500;
}

.end {
  margin-left: auto;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.thinking .bubble {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.state {
  margin: 0 var(--pnmt-space-3);
}

.compose {
  display: flex;
  align-items: flex-end;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-3) var(--pnmt-space-3) var(--pnmt-space-1);
  border-top: 1px solid var(--pnmt-border);
}

.input {
  flex: 1;
  max-height: 30vh;
}

.to {
  flex: none;
}

@media (max-width: 599px) {
  .compose {
    flex-wrap: wrap;
  }

  .to {
    width: 100%;
  }
}

.problem {
  padding: 0 var(--pnmt-space-3) var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
