<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Paginator from 'primevue/paginator'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import {
  decisionKindLabel,
  decisionOutcomeLabel,
  decisionOutcomeSeverity,
  formatConfidence,
  verdictLabel,
  verdictSeverity,
  type DecisionItem,
} from './acting'

/**
 * Cookie's decision log (docs/cookie-plan.md §11, §21.1): every proposal and what came of it, newest first, with Seraph's
 * verdict, the inbox decision it waits in, and the model. Whoever may see an entry's case marks it Right or Wrong with a
 * comment, which the daily summariser turns into lessons. Asked again every 2 s while Cookie works, every 15 s
 * otherwise. On a case (caseId) the case's own entries, without the link to it.
 */
const props = withDefaults(
  defineProps<{ caseId?: string; kind?: string | null; outcome?: string | null; pageSize?: number }>(),
  { caseId: undefined, kind: null, outcome: null, pageSize: 25 },
)
const session = useSession()
const queryClient = useQueryClient()
const notify = useNotify()

const status = useQuery({
  queryKey: [...keys.cookie, 'status'],
  queryFn: api.cookie.status,
  refetchInterval: (q) => (q.state.data?.cookie.state === 'Working' ? 2_000 : 15_000),
})
const working = computed(() => status.data.value?.cookie.state === 'Working')
const filters = computed(() => ({
  caseId: props.caseId,
  kind: props.kind ?? undefined,
  outcome: props.outcome ?? undefined,
}))
const table = useServerTable<DecisionItem>({
  key: [...keys.cookie, 'decisions'],
  fetch: (q) => api.cookie.decisions(q),
  filters,
  pageSize: props.pageSize,
  refetchInterval: computed(() => (working.value ? 2_000 : 15_000)),
})
const q = table.query
const canDecide = computed(() => session.can('Decisions', 'Decide'))
const caseLink = (id: string) => ({ name: 'cookie', params: { tab: 'cases' }, query: { case: id } })

const opened = ref<Set<number>>(new Set())
function toggle(id: number) {
  const next = new Set(opened.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  opened.value = next
}

// --- rating (§11) ---------------------------------------------------------------------------------------------------
type Rating = 'right' | 'wrong'
/** The entry being rated, the choice, and the comment. */
const ratingId = ref<number | null>(null)
const choice = ref<Rating>('right')
const comment = ref('')
const ratingError = ref<string | undefined>()
const saving = ref(false)

function startRating(d: DecisionItem, value: Rating) {
  ratingId.value = d.id
  choice.value = value
  comment.value = d.ratingComment ?? ''
  ratingError.value = undefined
}

async function rate(d: DecisionItem, value: Rating | null, comment: string | null) {
  saving.value = true
  ratingError.value = undefined
  try {
    await api.cookie.rate(d.id, { rating: value, comment: value ? comment?.trim() || null : null })
    ratingId.value = null
    notify.success(value ? 'Rated' : 'Rating taken back', value ? "It counts towards Cookie's lessons." : undefined)
    await queryClient.invalidateQueries({ queryKey: [...keys.cookie, 'decisions'] })
  } catch (e) {
    if (e instanceof ApiError && (e.field('comment') || e.field('rating')))
      ratingError.value = e.field('comment') ?? e.field('rating')
    else notify.error(e, 'Not rated')
  } finally {
    saving.value = false
  }
}

const ratingLabel = (r: string) => (r === 'right' ? 'Right' : r === 'wrong' ? 'Wrong' : r)
</script>

<template>
  <div class="decision-log">
    <ErrorState
      v-if="q.isError.value && !q.data.value"
      :error="q.error.value"
      what="the decisions"
      @retry="q.refetch()"
    />
    <div v-else-if="!q.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <p v-else-if="!table.items.value.length && caseId" class="muted none">None yet.</p>
    <EmptyState
      v-else-if="!table.items.value.length"
      icon="pi pi-check-square"
      :title="kind || outcome ? 'No decisions like these.' : 'No decisions yet.'"
      text="Cookie logs here what it proposed and what came of it: done, waiting in the inbox, rejected by Seraph, held back."
      class="panel"
    />
    <ol v-else class="entries" aria-label="The decision log">
      <li v-for="d in table.items.value" :key="d.id" class="entry" :aria-label="d.title">
        <div class="head">
          <RelativeTime :value="d.at" class="muted small" />
          <Tag :value="decisionKindLabel(d.kind)" severity="secondary" />
          <Tag v-if="d.orderedBy" :value="`ordered by ${d.orderedBy}`" severity="warn" />
          <span class="title">{{ d.title }}</span>
          <Tag :value="decisionOutcomeLabel(d.outcome)" :severity="decisionOutcomeSeverity(d.outcome)" class="end" />
        </div>
        <div class="meta small">
          <RouterLink v-if="d.caseId && !caseId" :to="caseLink(d.caseId)">{{ d.caseTitle ?? 'The case' }}</RouterLink>
          <span v-if="d.confidence !== null" class="muted">confidence {{ formatConfidence(d.confidence) }}</span>
          <span v-if="d.model" class="muted"
            >by <span class="mono">{{ d.model }}</span></span
          >
          <RouterLink
            v-if="d.inboxDecisionId && canDecide"
            :to="{ name: 'decisions', params: { id: d.inboxDecisionId } }"
            >In the decisions inbox <i class="pi pi-angle-right" aria-hidden="true"
          /></RouterLink>
          <span v-else-if="d.inboxDecisionId" class="muted">in the decisions inbox</span>
        </div>
        <div v-if="d.detail" class="wrap">{{ d.detail }}</div>
        <div v-if="d.verdict" class="verdict">
          <span class="muted small">Seraph</span>
          <Tag :value="verdictLabel(d.verdict)" :severity="verdictSeverity(d.verdict)" />
          <span v-if="d.verdictConfidence != null" class="muted small">{{
            formatConfidence(d.verdictConfidence)
          }}</span>
          <span v-if="d.verdictReason" class="wrap">{{ d.verdictReason }}</span>
        </div>
        <div v-if="d.text || d.reason">
          <Button
            :label="opened.has(d.id) ? 'Hide the text' : 'Show the text'"
            :icon="opened.has(d.id) ? 'pi pi-angle-up' : 'pi pi-angle-down'"
            size="small"
            severity="secondary"
            text
            class="fold"
            :aria-expanded="opened.has(d.id)"
            @click="toggle(d.id)"
          />
          <div v-if="opened.has(d.id)" class="folded">
            <pre v-if="d.text" class="body">{{ d.text }}</pre>
            <div v-if="d.reason" class="small"><span class="muted">Why:</span> {{ d.reason }}</div>
          </div>
        </div>

        <form
          v-if="ratingId === d.id"
          class="rate-form"
          novalidate
          :aria-label="`Rate ${d.title}`"
          @submit.prevent="rate(d, choice, comment)"
        >
          <div class="line">
            <Button
              label="Right"
              icon="pi pi-thumbs-up"
              size="small"
              :severity="choice === 'right' ? 'success' : 'secondary'"
              :outlined="choice !== 'right'"
              :aria-pressed="choice === 'right'"
              @click="choice = 'right'"
            />
            <Button
              label="Wrong"
              icon="pi pi-thumbs-down"
              size="small"
              :severity="choice === 'wrong' ? 'danger' : 'secondary'"
              :outlined="choice !== 'wrong'"
              :aria-pressed="choice === 'wrong'"
              @click="choice = 'wrong'"
            />
          </div>
          <Textarea
            v-model="comment"
            rows="2"
            auto-resize
            maxlength="1000"
            placeholder="Why (if you like): it helps Cookie's lessons"
            aria-label="Comment"
            :invalid="!!ratingError"
          />
          <small v-if="ratingError" class="field-error">{{ ratingError }}</small>
          <div class="line">
            <Button type="submit" label="Save" icon="pi pi-check" size="small" :loading="saving" />
            <Button label="Cancel" size="small" severity="secondary" text :disabled="saving" @click="ratingId = null" />
          </div>
        </form>
        <div v-else-if="d.rating" class="line small">
          <Tag
            :value="ratingLabel(d.rating)"
            :severity="d.rating === 'right' ? 'success' : 'danger'"
            :icon="d.rating === 'right' ? 'pi pi-thumbs-up' : 'pi pi-thumbs-down'"
          />
          <span class="muted">by {{ d.ratedByName ?? 'someone' }}</span>
          <span v-if="d.ratingComment" class="wrap">“{{ d.ratingComment }}”</span>
          <template v-if="d.canRate">
            <Button label="Change" size="small" severity="secondary" text @click="startRating(d, d.rating as Rating)" />
            <Button
              label="Take back"
              size="small"
              severity="secondary"
              text
              :disabled="saving"
              @click="rate(d, null, null)"
            />
          </template>
        </div>
        <div v-else-if="d.canRate" class="line">
          <span class="muted small">Was it right?</span>
          <Button
            label="Right"
            icon="pi pi-thumbs-up"
            size="small"
            severity="secondary"
            outlined
            @click="startRating(d, 'right')"
          />
          <Button
            label="Wrong"
            icon="pi pi-thumbs-down"
            size="small"
            severity="secondary"
            outlined
            @click="startRating(d, 'wrong')"
          />
        </div>
      </li>
    </ol>
    <Paginator
      v-if="table.total.value > table.pageSize.value"
      :rows="table.pageSize.value"
      :total-records="table.total.value"
      :first="(table.page.value - 1) * table.pageSize.value"
      template="FirstPageLink PrevPageLink PageLinks NextPageLink LastPageLink CurrentPageReport"
      current-page-report-template="{first}–{last} of {totalRecords}"
      @page="(e) => table.goTo(e.page + 1)"
    />
  </div>
</template>

<style scoped>
.decision-log {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.none {
  margin: 0;
}

.entries {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.entry {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  background: var(--pnmt-panel);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}

.head,
.meta,
.verdict,
.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.title {
  font-weight: 500;
  min-width: 0;
  overflow-wrap: anywhere;
}

.end {
  margin-left: auto;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.fold {
  padding-left: 0;
}

.folded {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.body {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  max-height: 40vh;
  overflow: auto;
  padding: var(--pnmt-space-2);
  background: var(--pnmt-none-soft);
  border-radius: var(--p-border-radius-sm);
}

.rate-form {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  max-width: 40rem;
}

.small {
  font-size: 0.857rem;
}
</style>
