<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, onBeforeUnmount, ref } from 'vue'
import { boardKeys, boardsApi, type BoardMessageItem } from '@/api/boards'
import MessageBox from '@/boards/MessageBox.vue'
import MessageIcon from '@/boards/MessageIcon.vue'
import { countdown, kindNames, kindOrder, type BoardMessageKind, type ShownMessage } from '@/boards/messages'
import { boardTime } from '@/boards/time'
import '@/boards/board.css'
import '@/boards/themes/themes.css'
import { useActions } from '@/composables/useActions'
import { formatAbsolute, formatTime } from '@/format'
import { useSession } from '@/stores/session'

/**
 * Admin → Debug's board messages (docs/board-messages-plan.md §7): a message box sent to boards that show messages, each with how far
 * it reaches; a preview in the first board's theme, drawn by the board app's own component; the open messages with Withdraw, and the
 * last 20 sent. Sending needs Boards: Message and shares the page's limit of 10 sends a minute.
 */
const session = useSession()
const canSend = computed(() => session.can('Boards', 'Message'))
const actions = useActions()

const boards = useQuery({ queryKey: boardKeys.list(), queryFn: boardsApi.list })
const catalog = useQuery({ queryKey: boardKeys.catalog(), queryFn: boardsApi.catalog, staleTime: 60_000 })
const targets = computed(() => (boards.data.value ?? []).filter((b) => b.enabled && b.showsMessages))
const chosen = ref<string[]>([])
const all = ref(false)
const picked = computed(() => (all.value ? targets.value : targets.value.filter((b) => chosen.value.includes(b.id))))
const reach = (b: { anywhere: boolean; networks: number }) =>
  b.anywhere ? 'from anywhere' : `${b.networks} network${b.networks === 1 ? '' : 's'}`

const kind = ref<BoardMessageKind>('Information')
const kinds = kindOrder.map((k) => ({ value: k, label: kindNames[k] }))
const title = ref('')
const text = ref('')
const seconds = ref(60)
const max = computed(() => catalog.data.value?.messageMaxSeconds ?? 3600)
const presets = computed(() =>
  [
    { value: 30, label: '30 s' },
    { value: 60, label: '1 min' },
    { value: 120, label: '2 min' },
    { value: 300, label: '5 min' },
    { value: 600, label: '10 min' },
    { value: 1800, label: '30 min' },
    { value: 3600, label: '1 h' },
  ].filter((p) => p.value <= max.value),
)

const now = ref(Date.now())
const ticker = setInterval(() => (now.value = Date.now()), 1000)
onBeforeUnmount(() => clearInterval(ticker))

const time = computed(() => boardTime(catalog.data.value?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone))
const first = computed(() => picked.value[0] ?? targets.value[0] ?? null)
const sample = computed<ShownMessage>(() => ({
  id: 'sample',
  kind: kind.value,
  title: title.value.trim() || kindNames[kind.value],
  text: text.value.trim() || null,
  from: session.user?.displayName ?? null,
  sentAt: new Date(now.value).toISOString(),
  seconds: seconds.value,
  remainingSeconds: seconds.value,
  endsAt: now.value + seconds.value * 1000,
}))

const result = ref<string | null>(null)
const valid = computed(
  () =>
    canSend.value &&
    picked.value.length > 0 &&
    title.value.trim().length > 0 &&
    title.value.length <= 100 &&
    text.value.length <= 1000 &&
    seconds.value >= 10 &&
    seconds.value <= max.value,
)
const names = (m: BoardMessageItem) => m.boards.map((b) => b.name).join(', ')

async function send() {
  result.value = null
  const sent = await actions.run(
    () =>
      boardsApi.sendMessage({
        boardIds: all.value ? null : chosen.value,
        allBoards: all.value,
        kind: kind.value,
        title: title.value.trim(),
        text: text.value.trim() || null,
        seconds: seconds.value,
      }),
    { failure: 'Not sent', invalidate: [['boards', 'messages']] },
  )
  if (sent) result.value = `Shown on ${names(sent)} until ${formatTime(sent.endsAt)}.`
}

const openList = useQuery({
  queryKey: boardKeys.messages(true),
  queryFn: () => boardsApi.messages(true),
  refetchInterval: 10_000,
})
const recent = useQuery({ queryKey: boardKeys.messages(false), queryFn: () => boardsApi.messages(false, 20) })
const left = (m: BoardMessageItem) => countdown(new Date(m.endsAt).getTime() - now.value)
const shownOpen = computed(() => (openList.data.value ?? []).filter((m) => new Date(m.endsAt).getTime() > now.value))

async function withdraw(m: BoardMessageItem) {
  if (
    !(await actions.ask('Withdraw this message?', `“${m.title}” goes from ${names(m)} at their next poll.`, 'Withdraw'))
  )
    return
  await actions.done(() => boardsApi.withdrawMessage(m.id), {
    success: 'Withdrawn',
    invalidate: [['boards', 'messages']],
  })
}
const sender = (m: BoardMessageItem) =>
  m.source === 'Cookie' ? `${m.sentByName}${m.approvedByName ? `, approved by ${m.approvedByName}` : ''}` : m.sentByName
</script>

<template>
  <section v-if="canSend" class="panel" aria-labelledby="board-message-title">
    <div id="board-message-title" class="panel-header">Send a board message</div>
    <form class="panel-body form board-message-form" novalidate @submit.prevent="send">
      <p v-if="boards.data.value && !targets.length" class="muted small">
        No board shows messages yet: switch on Look → Messages for one under Boards.
      </p>
      <div class="field">
        <label for="message-boards">Boards</label>
        <MultiSelect
          v-model="chosen"
          input-id="message-boards"
          :options="targets"
          option-label="name"
          option-value="id"
          display="chip"
          placeholder="Choose boards"
          :disabled="all"
          data-test="message-boards"
        />
        <label class="check"
          ><Checkbox v-model="all" binary data-test="message-all" /> All boards that show messages ({{
            targets.length
          }})</label
        >
        <ul v-if="picked.length" class="reach muted small">
          <li v-for="b in picked" :key="b.id">
            <strong>{{ b.name }}</strong>: {{ reach(b)
            }}<i v-if="b.anywhere" class="pi pi-exclamation-triangle warn" title="Anyone with its URL reads it" />
          </li>
        </ul>
      </div>
      <div class="pair">
        <div class="field">
          <label for="message-kind">Kind</label>
          <Select v-model="kind" input-id="message-kind" :options="kinds" option-label="label" option-value="value">
            <template #value="{ value }">
              <span class="kind"
                ><MessageIcon :kind="value" class="kind-icon" :data-kind="value" />
                {{ kindNames[value as BoardMessageKind] }}</span
              >
            </template>
            <template #option="{ option }">
              <span class="kind"
                ><MessageIcon :kind="option.value" class="kind-icon" :data-kind="option.value" />
                {{ option.label }}</span
              >
            </template>
          </Select>
        </div>
        <div class="field">
          <label for="message-seconds">Show for (seconds)</label>
          <InputNumber v-model="seconds" input-id="message-seconds" :min="10" :max="max" show-buttons />
        </div>
      </div>
      <SelectButton
        v-model="seconds"
        :options="presets"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        aria-label="Show for"
        size="small"
      />
      <div class="field">
        <label for="message-title"
          >Title <span class="muted small">{{ title.length }} / 100</span></label
        >
        <InputText id="message-title" v-model="title" :maxlength="100" data-test="message-title" />
      </div>
      <div class="field">
        <label for="message-text"
          >Message <span class="muted small">{{ text.length }} / 1000</span></label
        >
        <Textarea id="message-text" v-model="text" rows="3" auto-resize :maxlength="1000" data-test="message-text" />
        <small class="field-hint">Markdown: **bold**, *italics*, lists. Links show as text.</small>
      </div>
      <div class="field">
        <label
          >Preview<template v-if="first">, in {{ first.name }}'s theme</template></label
        >
        <div class="message-preview">
          <div
            class="board"
            :data-board-theme="first?.theme ?? 'default'"
            data-fit="true"
            data-motion="false"
            data-test="message-preview"
          >
            <div class="board-main">
              <MessageBox :messages="[sample]" :size="first?.messageSize ?? 'Large'" :time="time" still />
            </div>
          </div>
        </div>
      </div>
      <div class="form-actions">
        <Button
          type="submit"
          :label="all ? 'Send to all' : picked.length === 1 ? 'Send to 1 board' : `Send to ${picked.length} boards`"
          icon="pi pi-send"
          :loading="actions.busy.value"
          :disabled="!valid"
          data-test="message-send"
        />
      </div>
      <Message v-if="result" severity="success" :closable="false" size="small" data-testid="message-result">{{
        result
      }}</Message>
    </form>
  </section>

  <section class="panel messages-panel" aria-labelledby="open-messages-title">
    <div id="open-messages-title" class="panel-header">Open board messages</div>
    <div class="panel-body">
      <p v-if="!shownOpen.length" class="muted small">None open.</p>
      <ul v-else class="message-list" data-test="open-messages">
        <li v-for="m in shownOpen" :key="m.id">
          <MessageIcon :kind="m.kind" class="kind-icon" :data-kind="m.kind" />
          <span class="what"
            ><strong>{{ m.title }}</strong> <span class="muted">{{ kindNames[m.kind] }} · {{ names(m) }}</span></span
          >
          <span class="muted">{{ sender(m) }} · {{ formatTime(m.sentAt) }}</span>
          <span class="left">{{ left(m) }} left</span>
          <Button
            v-if="canSend"
            label="Withdraw"
            size="small"
            severity="secondary"
            :data-test="`withdraw-${m.id}`"
            @click="withdraw(m)"
          />
        </li>
      </ul>
      <h3 class="recent-title">Sent lately</h3>
      <p v-if="!(recent.data.value ?? []).length" class="muted small">Nothing in the last 30 days.</p>
      <ul v-else class="message-list recent">
        <li v-for="m in recent.data.value ?? []" :key="m.id">
          <MessageIcon :kind="m.kind" class="kind-icon" :data-kind="m.kind" />
          <span class="what"
            ><strong>{{ m.title }}</strong> <span class="muted">{{ names(m) }}</span></span
          >
          <span class="muted">{{ sender(m) }} · {{ formatAbsolute(m.sentAt) }}</span>
          <span class="muted"
            ><template v-if="m.withdrawnAt">withdrawn by {{ m.withdrawnByName }}</template
            ><template v-else-if="m.open">open</template><template v-else>ended</template></span
          >
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.board-message-form {
  gap: var(--pnmt-space-3);
}

.pair {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-3);
}

.check {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin-top: 4px;
}

.small {
  font-size: 0.857rem;
}

.reach {
  margin: 4px 0 0;
  padding-left: 1.2em;
}

.warn {
  color: var(--pnmt-warn);
  margin-left: 4px;
}

.kind {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.kind-icon {
  width: 1.1em;
  height: 1.1em;
  flex: 0 0 auto;
  color: #0d6efd;
}

.kind-icon[data-kind='Alert'] {
  color: #dc3545;
}

.kind-icon[data-kind='Warning'] {
  color: #e0a800;
}

.kind-icon[data-kind='Maintenance'] {
  color: #6f42c1;
}

.kind-icon[data-kind='Success'] {
  color: #198754;
}

.message-preview {
  width: 100%;
  max-width: 640px;
  aspect-ratio: 16 / 9;
  border: 1px solid var(--pnmt-border);
  border-radius: 6px;
  overflow: hidden;
}

.message-preview .board {
  height: 100%;
}

.form-actions {
  justify-content: flex-start;
}

.messages-panel {
  margin-top: var(--pnmt-space-3);
}

.message-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.message-list li {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.what {
  flex: 1 1 16em;
  min-width: 0;
}

.left {
  font-variant-numeric: tabular-nums;
}

.recent-title {
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
  font-size: 1rem;
}
</style>
