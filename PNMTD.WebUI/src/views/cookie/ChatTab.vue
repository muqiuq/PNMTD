<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Textarea from 'primevue/textarea'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { lastId } from './trace'
import { usePoll } from './usePoll'

/**
 * The chat with Cookie (docs/cookie-plan.md §10.2): one thread per person, newest at the bottom. A message wakes Cookie;
 * while it waits for a reply the thread is asked every 2 s, otherwise every 30 s. Cookie answers with the lower of the
 * person's rights and its own.
 */
type Item = S['CookieMessageItem']
const maxChars = 4000

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
    const sent = await api.cookie.post(message)
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

function onKey(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void send()
  }
}
</script>

<template>
  <section class="panel chat" aria-label="The chat with Cookie">
    <p class="muted small note">Cookie answers with the lower of your rights and its own.</p>
    <ErrorState v-if="loadError && !loaded" :error="loadError" what="the chat" @retry="poll.restart()" />
    <div v-else-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <div ref="list" class="thread" role="log" aria-live="polite">
        <p v-if="!messages.length" class="muted empty">
          Ask Cookie about a case, a host, a ticket, or what happened overnight.
        </p>
        <div v-for="m in messages" :key="m.id" class="message" :class="m.from === 'person' ? 'mine' : 'cookie'">
          <div class="who small muted">
            {{ m.from === 'person' ? 'You' : 'Cookie' }} · <RelativeTime :value="m.at" />
          </div>
          <div class="bubble">{{ m.text }}</div>
        </div>
        <div v-if="waiting" class="message cookie thinking">
          <div class="bubble muted"><i class="pi pi-spin pi-spinner" aria-hidden="true" /> Cookie is thinking…</div>
        </div>
      </div>
      <Message v-if="stopped" severity="info" :closable="false" size="small" class="state">
        Cookie is stopped: an admin starts it on Admin → Cookie. Until then you can read your thread, but not write.
      </Message>
      <form class="compose" novalidate @submit.prevent="send">
        <Textarea
          v-model="text"
          rows="2"
          auto-resize
          class="input"
          placeholder="Write to Cookie"
          aria-label="Your message to Cookie"
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

.cookie {
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

.problem {
  padding: 0 var(--pnmt-space-3) var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
