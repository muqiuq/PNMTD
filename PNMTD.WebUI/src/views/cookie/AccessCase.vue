<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import {
  accessGroupLabel,
  accessVerdictLabel,
  accessVerdictSeverity,
  answerForm,
  answerLabel,
  answerRequest,
  answerSeverity,
  personAnswerLabel,
  rememberDays,
  reviewStateLabel,
  reviewStateSeverity,
  targetKindLabel,
  type AnswerForm,
  type RememberScope,
} from './access'
import { formatConfidence } from './acting'
import { channelName, outboxStateName, outboxStateSeverity } from './cookieFormat'

/**
 * An access case's own part (docs/cookie-access-plan.md §5, §6, §7, §10.4): the verdict and the answer, the logins with
 * their signals (the strong ones marked), each one's verdict, who judged it and why, and Was this you?; the notices sent;
 * and what a person can do, as links. A contact for the target, or Cookie: Instruct, answers Expected (with Remember this)
 * or Not expected; Ask Cookie for a ticket has Cookie write one in its next round, the only way an access case gets one.
 */
const props = defineProps<{ id: string; locationId: string | null }>()
const session = useSession()
const queryClient = useQueryClient()
const notify = useNotify()
const detail = useQuery({
  queryKey: computed(() => [...keys.cookie, 'access-case', props.id]),
  queryFn: () => api.cookie.accessCase(props.id),
  refetchInterval: 30_000,
})
const a = computed(() => detail.data.value)
const canMonitoring = computed(() => session.can('Monitoring', 'View'))
const judgedByLabel = (by: string | null) => (by === 'code' ? 'PNMT' : by === 'cookie' ? 'Cookie' : by)

// --- the answer (§7) ---------------------------------------------------------------------------------------------
const form = ref<AnswerForm | null>(null)
const formError = ref<string | undefined>()
const busy = ref(false)
const ticketProblem = ref<string | undefined>()
const dialogOpen = computed({ get: () => form.value !== null, set: (v) => !v && (form.value = null) })
watch(
  () => props.id,
  () => {
    form.value = null
    ticketProblem.value = undefined
  },
)
watch(
  () => form.value?.comment,
  () => (formError.value = undefined),
)

const scopes = computed<{ label: string; value: RememberScope; disabled?: boolean }[]>(() => [
  { label: a.value?.target === 'Pnmt' ? 'PNMT' : 'This target', value: 'target' },
  { label: 'Its location', value: 'location', disabled: !props.locationId },
  { label: 'Every target', value: 'all' },
])
const newest = computed(() => a.value?.logins[0])

function answer(expected: boolean) {
  form.value = answerForm(expected)
  formError.value = undefined
}

async function send() {
  if (!form.value) return
  const expected = form.value.expected
  busy.value = true
  try {
    await api.cookie.answerAccess(props.id, answerRequest(form.value))
    form.value = null
    notify.success(
      expected ? 'Expected' : 'Not expected',
      expected ? 'The case closes.' : 'The case stays open, important. Cookie writes a note in its next round.',
    )
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) formError.value = Object.values(e.errors)[0]?.[0]
    else notify.error(e, 'Not answered')
  } finally {
    busy.value = false
  }
}

async function askTicket() {
  busy.value = true
  ticketProblem.value = undefined
  try {
    await api.cookie.askAccessTicket(props.id)
    notify.success('Asked Cookie for a ticket', 'Cookie writes one in its next round.')
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) ticketProblem.value = e.message
    else notify.error(e, 'Not asked')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <ErrorState
    v-if="detail.isError.value && !a"
    :error="detail.error.value"
    what="the access case"
    @retry="detail.refetch()"
  />
  <div v-else-if="!a" class="center"><ProgressSpinner style="width: 24px; height: 24px" /></div>
  <section v-else class="access" aria-labelledby="access-case-title">
    <h3 id="access-case-title" class="sub">The login</h3>
    <dl class="facts">
      <dt>{{ targetKindLabel[a.target] }}</dt>
      <dd>
        <RouterLink
          v-if="a.sensorId && a.target !== 'Pnmt' && canMonitoring"
          :to="{ name: 'sensor', params: { id: a.sensorId, tab: 'access' } }"
          >{{ a.targetName }}</RouterLink
        >
        <span v-else>{{ a.targetName }}</span>
      </dd>
      <dt>Verdict</dt>
      <dd class="line">
        <Tag v-if="a.verdict" :value="accessVerdictLabel(a.verdict)" :severity="accessVerdictSeverity(a.verdict)" />
        <span v-else class="muted">none yet</span>
      </dd>
      <dt>Notice</dt>
      <dd>
        <template v-if="a.notifiedVerdict"
          >sent as {{ a.notifiedVerdict
          }}<template v-if="a.notifiedAt">, <RelativeTime :value="a.notifiedAt" /></template
        ></template>
        <span v-else class="muted">none sent</span>
        <div v-if="a.pendingVerdict" class="muted small">
          A notice as {{ a.pendingVerdict }} waits for working hours.
        </div>
      </dd>
      <dt>Answer</dt>
      <dd class="line">
        <template v-if="a.answer">
          <Tag :value="answerLabel(a.answer)" :severity="answerSeverity(a.answer)" />
          <span class="small"
            >{{ a.answeredBy ?? 'someone'
            }}<template v-if="a.answeredAt">, {{ formatAbsolute(a.answeredAt) }}</template></span
          >
        </template>
        <span v-else class="muted">none yet</span>
      </dd>
      <template v-if="a.ticketAskedAt">
        <dt>Ticket</dt>
        <dd>asked for <RelativeTime :value="a.ticketAskedAt" />: Cookie writes one in its next round</dd>
      </template>
    </dl>

    <div v-if="a.canAnswer" class="case-actions">
      <Button
        v-if="a.answer !== 'expected'"
        label="Expected…"
        icon="pi pi-check"
        size="small"
        severity="success"
        outlined
        :disabled="busy"
        @click="answer(true)"
      />
      <Button
        v-if="a.answer !== 'not-expected'"
        label="Not expected…"
        icon="pi pi-exclamation-triangle"
        size="small"
        severity="danger"
        outlined
        :disabled="busy"
        @click="answer(false)"
      />
      <span class="muted small">Your answer rates Cookie's verdict, and Cookie learns from it.</span>
    </div>
    <div v-if="a.canAskTicket" class="case-actions">
      <Button
        label="Ask Cookie for a ticket"
        icon="pi pi-ticket"
        size="small"
        severity="secondary"
        outlined
        :disabled="busy"
        @click="askTicket"
      />
      <span class="muted small">Cookie writes one in its next round, at the ticket's level, with the logins.</span>
    </div>
    <Message v-if="ticketProblem" severity="warn" :closable="false" size="small">{{ ticketProblem }}</Message>

    <h3 class="sub">Logins</h3>
    <p v-if="!a.logins.length" class="muted none">None.</p>
    <DataTable v-else :value="a.logins" size="small" data-key="id" class="table">
      <Column header="When" style="width: 8.5rem">
        <template #body="{ data }">
          <span class="nowrap small">{{ formatAbsolute(data.at) }}</span>
          <div class="line">
            <Tag v-if="data.late" value="late" severity="secondary" title="Older than 24 hours when it came" />
            <Tag v-if="data.flagged" value="flagged" severity="danger" title="Its text looks like an injection" />
          </div>
        </template>
      </Column>
      <Column header="Login">
        <template #body="{ data }">
          <div class="wrap">{{ data.text }}</div>
          <div class="line small muted">
            {{ accessGroupLabel(data.group) }}
            <Tag v-if="data.isAdmin" value="admin" severity="warn" />
            <Tag v-if="data.publicSource" value="public source" severity="warn" />
          </div>
          <ul v-if="data.signals.length" class="signals">
            <li v-for="sig in data.signals" :key="sig.key" :class="{ strong: sig.strength === 'Strong' }">
              <Tag
                :value="sig.strength === 'Strong' ? 'strong' : 'medium'"
                :severity="sig.strength === 'Strong' ? 'danger' : 'secondary'"
                class="strength"
              />
              <span class="small">{{ sig.text }}</span>
            </li>
          </ul>
          <div v-if="data.reason" class="reason small">{{ data.reason }}</div>
        </template>
      </Column>
      <Column header="Verdict" style="width: 10rem">
        <template #body="{ data }">
          <Tag :value="reviewStateLabel(data.state)" :severity="reviewStateSeverity(data.state)" />
          <div v-if="data.judgedBy" class="muted small">
            by {{ judgedByLabel(data.judgedBy)
            }}<template v-if="data.confidence !== null"> · {{ formatConfidence(data.confidence) }}</template>
          </div>
          <div class="small ask">
            <template v-if="data.askedAt">
              <span class="muted">Was this you?</span>
              <template v-if="data.personAnswer">
                {{ personAnswerLabel(data.personAnswer) }}<template v-if="data.person"> ({{ data.person }})</template>
              </template>
              <span v-else class="muted">
                asked<template v-if="data.person"> {{ data.person }}</template> <RelativeTime :value="data.askedAt" />,
                no answer</span
              >
            </template>
            <span v-else-if="data.person" class="muted">{{ data.person }}'s account</span>
          </div>
        </template>
      </Column>
    </DataTable>

    <template v-if="a.notices.length">
      <h3 class="sub">Notices</h3>
      <DataTable :value="a.notices" size="small" class="table">
        <Column header="When" style="width: 6.5rem">
          <template #body="{ data }"><RelativeTime :value="data.at" /></template>
        </Column>
        <Column header="How" style="width: 8rem">
          <template #body="{ data }">{{ channelName(data.channel) }}</template>
        </Column>
        <Column header="To">
          <template #body="{ data }">
            <div class="ellipsis" :title="data.subject">{{ data.recipient || '–' }}</div>
            <div v-if="data.detail" class="muted small wrap">{{ data.detail }}</div>
          </template>
        </Column>
        <Column header="State" style="width: 7rem">
          <template #body="{ data }">
            <Tag :value="outboxStateName(data.state)" :severity="outboxStateSeverity(data.state)" />
            <div class="muted small">{{ data.source === 'code' ? 'by PNMT' : 'by Cookie' }}</div>
          </template>
        </Column>
      </DataTable>
    </template>

    <template v-if="a.links.length">
      <h3 class="sub">What you can do</h3>
      <ul class="links">
        <li v-for="l in a.links" :key="l.path">
          <RouterLink :to="l.path">{{ l.label }}</RouterLink>
        </li>
      </ul>
      <p class="muted small none">Cookie never does any of these itself.</p>
    </template>

    <Dialog
      v-model:visible="dialogOpen"
      :header="form?.expected ? 'Expected' : 'Not expected'"
      modal
      :style="{ width: 'min(32rem, 95vw)' }"
    >
      <form v-if="form" id="access-answer" class="form" novalidate @submit.prevent="send">
        <p class="text">
          <template v-if="form.expected"
            >It was you, a colleague or the vendor: the case closes, and Cookie's verdict is rated by it.</template
          >
          <template v-else
            >Nobody expected it: the case stays open with importance High, and PNMT's state says Alert. Cookie writes a
            note in its next round and proposes nothing. A ticket only comes when someone asks for one.</template
          >
        </p>
        <div class="field">
          <label for="access-answer-comment">A comment (if you like)</label>
          <Textarea
            id="access-answer-comment"
            v-model="form.comment"
            rows="2"
            auto-resize
            maxlength="1000"
            autofocus
            :invalid="!!formError"
          />
        </div>
        <template v-if="form.expected">
          <span class="checkbox">
            <Checkbox v-model="form.remember" binary input-id="access-remember" />
            <label for="access-remember">Remember this: the same access stays quiet from now on</label>
          </span>
          <template v-if="form.remember">
            <div class="field">
              <span class="label">For</span>
              <SelectButton
                v-model="form.scope"
                :options="scopes"
                option-label="label"
                option-value="value"
                option-disabled="disabled"
                :allow-empty="false"
                size="small"
                aria-label="Remember this for"
              />
            </div>
            <span class="checkbox">
              <Checkbox v-model="form.anySource" binary input-id="access-any-source" />
              <label for="access-any-source"
                >From any source<template v-if="newest?.source"
                  >, not only {{ newest.source }}'s network</template
                ></label
              >
            </span>
            <span class="checkbox">
              <Checkbox v-model="form.anyWay" binary input-id="access-any-way" />
              <label for="access-any-way"
                >In any way<template v-if="newest"
                  >, not only {{ accessGroupLabel(newest.group).toLowerCase() }}</template
                ></label
              >
            </span>
            <div class="field">
              <span class="label">How long</span>
              <SelectButton
                v-model="form.days"
                :options="rememberDays"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                aria-label="Remember this for how long"
              />
            </div>
          </template>
        </template>
        <small v-if="formError" class="field-error">{{ formError }}</small>
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="form = null" />
        <Button
          type="submit"
          form="access-answer"
          :label="form?.expected ? 'Expected' : 'Not expected'"
          :severity="form?.expected ? 'success' : 'danger'"
          :loading="busy"
        />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-3);
}

.access {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.sub {
  font-size: 0.93rem;
  margin: var(--pnmt-space-3) 0 0;
}

.none,
.text {
  margin: 0;
}

.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.case-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.table {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.signals {
  list-style: none;
  margin: var(--pnmt-space-1) 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.signals li {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-1);
}

.signals li.strong {
  font-weight: 600;
}

.strength {
  flex: none;
  font-size: 0.75rem;
}

.reason {
  margin-top: var(--pnmt-space-1);
  border-left: 2px solid var(--p-primary-color);
  padding-left: var(--pnmt-space-2);
}

.ask {
  margin-top: var(--pnmt-space-1);
}

.links {
  margin: 0;
  padding-left: 1.2rem;
}

.checkbox {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.label {
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
}
</style>
