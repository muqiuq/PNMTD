<script setup lang="ts">
import Button from 'primevue/button'
import { computed } from 'vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'
import LoungeText from './LoungeText.vue'
import { author, commentable, emailLine, initials, timeOf, type LoungeMessage } from './loungeFormat'

/**
 * A line of the lounge (docs/lounge-plan.md §15.2): Cookie's and Seraph's with their own avatar and colour, people's with
 * their colour and short code; what a voice's message raised or e-mailed under it; a refused one marked, a removed one
 * kept in its place. A comment is smaller and set in (§9.3), under the message it is on, and marked until a voice took it up.
 */
const props = withDefaults(
  defineProps<{ message: LoungeMessage; cookieName: string; comments?: LoungeMessage[]; canComment?: boolean }>(),
  { comments: () => [], canComment: false },
)
const emit = defineEmits<{ comment: [message: LoungeMessage] }>()
const session = useSession()
const m = computed(() => props.message)
const who = computed(() => author(m.value, props.cookieName))
const isComment = computed(() => m.value.kind === 'comment')
const unread = computed(() => isComment.value && m.value.takenById == null && !m.value.refused && !m.value.removed)
const avatarStyle = computed(() =>
  m.value.from === 'person' && m.value.authorColor ? { background: m.value.authorColor, color: '#fff' } : undefined,
)
const caseLink = computed(() =>
  m.value.caseId && session.can('Cookie', 'Use')
    ? { name: 'cookie', params: { tab: 'cases' }, query: { case: m.value.caseId } }
    : null,
)
</script>

<template>
  <div v-if="isComment" class="comment" :class="{ refused: message.refused }" :data-id="message.id">
    <span class="arrow" aria-hidden="true">↳</span>
    <span class="small">
      <strong>{{ who }}</strong>, comment <span class="muted">{{ timeOf(message.at) }}</span><span
        v-if="unread"
        class="unread"
        >not yet read</span
      >:
    </span>
    <em v-if="message.removed" class="muted">Removed by an admin</em>
    <LoungeText v-else :text="message.text" />
    <div v-if="message.refused" class="muted small refused-why">
      <i class="pi pi-ban" aria-hidden="true" /> Cookie and Seraph didn't read this one.
    </div>
  </div>
  <div v-else class="line" :class="[message.from, { important: message.important }]" :data-id="message.id">
    <div class="avatar" :style="avatarStyle" aria-hidden="true">
      <i v-if="message.from === 'cookie'" class="pi pi-microchip-ai" />
      <i v-else-if="message.from === 'seraph'" class="pi pi-shield" />
      <template v-else>{{ initials(message) }}</template>
    </div>
    <div class="body">
      <div class="who small">
        <strong>{{ who }}</strong> · <RelativeTime :value="message.at" />
        <span
          v-if="message.important"
          class="important-tag"
          title="They think this really matters: it may be longer than their usual one sentence."
        >
          <i class="pi pi-exclamation-circle" aria-hidden="true" /> important</span
        >
      </div>
      <div class="text" :class="{ refused: message.refused }">
        <em v-if="message.removed" class="muted">Removed by an admin</em>
        <LoungeText v-else :text="message.text" />
      </div>
      <div v-if="message.refused" class="muted small">
        <i class="pi pi-ban" aria-hidden="true" /> Cookie and Seraph didn't read this one.
      </div>
      <div v-if="message.caseId && !message.removed" class="power small">
        <i class="pi pi-briefcase" aria-hidden="true" /> Opened case
        <RouterLink v-if="caseLink" :to="caseLink">{{ message.caseTitle ?? 'the case' }}</RouterLink>
        <span v-else>{{ message.caseTitle ?? '' }}</span>
      </div>
      <div v-if="message.email && !message.removed" class="power small">
        <i class="pi pi-envelope" aria-hidden="true" /> {{ emailLine(message.email) }}
      </div>
      <div v-if="comments.length" class="comments">
        <LoungeLine v-for="c in comments" :key="c.id" :message="c" :cookie-name="cookieName" />
      </div>
      <Button
        v-if="canComment && commentable(message)"
        label="Comment"
        icon="pi pi-reply"
        text
        size="small"
        class="comment-action"
        :aria-label="`Comment on ${who}'s message`"
        @click="emit('comment', message)"
      />
    </div>
  </div>
</template>

<style scoped>
/* A message the voice marked important (docs/lounge-plan.md §4.3): a line at its side, and a word by its name. */
.line.important .body {
  border-left: 3px solid var(--pnmt-warn);
  padding-left: var(--pnmt-space-2);
}

.important-tag {
  margin-left: var(--pnmt-space-1);
  color: var(--pnmt-warn);
  font-weight: 600;
}

.line {
  display: flex;
  gap: var(--pnmt-space-2);
}

.avatar {
  flex: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 50%;
  font-size: 0.75rem;
  font-weight: 700;
  background: var(--pnmt-none-soft);
  border: 1px solid var(--pnmt-border);
}

.cookie .avatar {
  color: var(--p-primary-contrast-color);
  background: var(--p-primary-color);
  border-color: transparent;
}

.seraph .avatar {
  color: #fff;
  background: var(--p-violet-500);
  border-color: transparent;
}

.body {
  flex: 1;
  min-width: 0;
}

.seraph .who strong {
  color: var(--p-violet-700);
}

:root.pnmt-dark .seraph .who strong {
  color: var(--p-violet-300);
}

.text.refused {
  opacity: 0.55;
}

.power {
  margin-top: 2px;
  color: var(--pnmt-text-muted, inherit);
}

.comments {
  margin-top: var(--pnmt-space-1);
}

.comment {
  margin: 2px 0 2px var(--pnmt-space-3);
  padding-left: var(--pnmt-space-2);
  border-left: 2px solid var(--pnmt-border);
  font-size: 0.929rem;
}

.comment.refused {
  opacity: 0.6;
}

.arrow {
  margin-right: 4px;
  color: var(--pnmt-border);
}

.unread {
  margin-left: 6px;
  padding: 0 6px;
  border-radius: 999px;
  font-size: 0.75rem;
  background: var(--pnmt-none-soft);
  border: 1px dashed var(--pnmt-border);
}

.comment-action {
  margin-top: 2px;
  padding: 0 4px;
}

.small {
  font-size: 0.857rem;
}

.refused-why {
  margin-top: 2px;
}
</style>
