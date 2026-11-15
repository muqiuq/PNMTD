<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import DatePicker from 'primevue/datepicker'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import { formatAbsolute } from '@/format'
import {
  instructionKindLabel,
  instructionKinds,
  instructionProblems,
  instructionRequest,
  instructionScopeLabel,
  instructionSourceLabel,
  instructionSpans,
  instructionState,
  instructionStateSeverity,
  instructionTarget,
  maxInstructionDays,
  muteScopes,
  sortInstructions,
  suppressScopes,
  untilAfter,
  type InstructionForm,
  type InstructionItem,
  type InstructionKind,
  type InstructionScope,
  type InstructionSpan,
} from '@/views/cookie/acting'
import InstructionValuePicker from './InstructionValuePicker.vue'

/**
 * Admin → Cookie → Mute, suppress (docs/cookie-plan.md §10.3, §21.1): what code enforces for a while, at most 30 days.
 * Mute a host, a sensor, a location or a case: no messages, tickets, notes on tickets or big brother calls for it.
 * Suppress a channel, a sensor type or a kind of case. A note goes into Cookie's and Seraph's prompts. The list has the
 * active ones and the drafts from the chat first, then the last 30 days', each with who created it, from where, and End
 * now. Created here, one is in force at once.
 */
const queryClient = useQueryClient()
const notify = useNotify()
const now = useNow()
const listKey = [...keys.cookie, 'admin', 'instructions'] as const
const list = useQuery({ queryKey: listKey, queryFn: api.cookie.instructions, refetchInterval: 30_000 })
const rows = computed(() => sortInstructions(list.data.value ?? [], now.value))
const stateOf = (i: InstructionItem) => instructionState(i, now.value)

// --- a new one ------------------------------------------------------------------------------------------------------
const blank = (): InstructionForm => ({
  kind: 'mute',
  scope: 'host',
  value: null,
  label: '',
  text: '',
  until: null,
})
const form = ref<InstructionForm>(blank())
const span = ref<InstructionSpan>('P1D')
const spans = [...instructionSpans]
const custom = ref<Date | null>(null)
const errors = ref<Record<string, string>>({})
const tried = ref(false)
const busy = ref(false)

const scopeOptions = computed(() =>
  (form.value.kind === 'mute' ? muteScopes : form.value.kind === 'suppress' ? suppressScopes : []).map((s) => ({
    label: instructionScopeLabel(s),
    value: s as string,
  })),
)
function setKind(kind: InstructionKind) {
  form.value.kind = kind
  form.value.scope = kind === 'mute' ? 'host' : kind === 'suppress' ? 'channel' : ''
  form.value.value = null
  form.value.label = ''
}
function setScope(scope: string) {
  form.value.scope = scope
  form.value.value = null
  form.value.label = ''
}
const kindText: Record<InstructionKind, string> = {
  mute: 'No messages, tickets, notes on tickets or big brother calls for it. Its cases still open and show, and notes inside PNMT go on.',
  suppress:
    'No messages of one kind: a channel for everything, or everything for a sensor type or a kind of case. Notes inside PNMT and mail rule events go on.',
  note: "Goes into Cookie's and Seraph's prompts with your name. It can narrow or steer, never widen: no new goals, rules, levels, limits or rights.",
}

/** Until when, from the span chosen, or the moment. */
const until = computed(() => (span.value === 'custom' ? custom.value : untilAfter(span.value, now.value)))
const minDate = computed(() => new Date(now.value))
const maxDate = computed(() => new Date(now.value + maxInstructionDays * 86_400_000))
const problems = computed(() => instructionProblems({ ...form.value, until: until.value }, Date.now()))
const shown = computed(() => (tried.value ? { ...problems.value, ...errors.value } : errors.value))
watch([form, span, custom], () => (errors.value = {}), { deep: true })

async function create() {
  tried.value = true
  const f = { ...form.value, until: span.value === 'custom' ? custom.value : untilAfter(span.value, Date.now()) }
  if (Object.keys(instructionProblems(f, Date.now())).length) return
  busy.value = true
  errors.value = {}
  try {
    const created = await api.cookie.createInstruction(instructionRequest(f))
    notify.success(
      `${instructionKindLabel(created.kind)} in force`,
      `Until ${formatAbsolute(created.until)}${instructionTarget(created) ? `: ${instructionTarget(created)}` : ''}.`,
    )
    form.value = { ...blank(), kind: form.value.kind, scope: form.value.scope }
    custom.value = null
    tried.value = false
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length)
      errors.value = Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0] ?? '']))
    else notify.error(e, 'Not created')
  } finally {
    busy.value = false
  }
}

// --- End now --------------------------------------------------------------------------------------------------------
const ending = ref<string | null>(null)
async function end(i: InstructionItem) {
  ending.value = i.id
  try {
    await api.cookie.endInstruction(i.id)
    notify.success(stateOf(i) === 'draft' ? 'Discarded' : 'Ended')
    await queryClient.invalidateQueries({ queryKey: keys.cookie })
  } catch (e) {
    notify.error(e, 'Not ended')
  } finally {
    ending.value = null
  }
}
</script>

<template>
  <div class="tab">
    <section class="panel" aria-labelledby="instructions-new-title">
      <div id="instructions-new-title" class="panel-header">New</div>
      <form class="panel-body form" novalidate @submit.prevent="create">
        <p class="muted small intro">
          Code enforces these, whatever a model says, for at most {{ maxInstructionDays }} days. Created here, one is in
          force at once; in the chat, Cookie fills one in as a draft that its person confirms.
        </p>
        <div class="field">
          <label id="instructions-kind">Kind</label>
          <SelectButton
            :model-value="form.kind"
            :options="instructionKinds"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-labelledby="instructions-kind"
            @update:model-value="setKind"
          />
          <small class="field-hint">{{ kindText[form.kind] }}</small>
        </div>
        <div v-if="form.kind !== 'note'" class="what">
          <div class="field">
            <label for="instructions-scope">Applies to</label>
            <Select
              :model-value="form.scope"
              :options="scopeOptions"
              option-label="label"
              option-value="value"
              input-id="instructions-scope"
              aria-label="Applies to"
              :invalid="!!shown.scope"
              class="scope"
              @update:model-value="setScope"
            />
            <small v-if="shown.scope" class="field-error">{{ shown.scope }}</small>
          </div>
          <div class="field grow">
            <label for="instructions-value">{{ instructionScopeLabel(form.scope) }}</label>
            <InstructionValuePicker
              :key="form.scope"
              v-model="form.value"
              :scope="form.scope as InstructionScope"
              :label="instructionScopeLabel(form.scope)"
              input-id="instructions-value"
              :invalid="!!shown.value"
              @picked="(label) => (form.label = label)"
            />
            <small v-if="shown.value" class="field-error">{{ shown.value }}</small>
          </div>
        </div>
        <div class="field">
          <label for="instructions-text">{{ form.kind === 'note' ? 'The instruction' : 'Why (if you like)' }}</label>
          <Textarea
            id="instructions-text"
            v-model="form.text"
            rows="2"
            auto-resize
            maxlength="1000"
            :placeholder="
              form.kind === 'note'
                ? 'Leave the printers on the second floor to Anna this week'
                : 'Maintenance this weekend'
            "
            :invalid="!!shown.text"
          />
          <small v-if="shown.text" class="field-error">{{ shown.text }}</small>
          <small v-else class="field-hint">Cookie and Seraph read it in their prompts, with your name.</small>
        </div>
        <div class="field">
          <label id="instructions-for">For</label>
          <div class="span">
            <SelectButton
              v-model="span"
              :options="spans"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              size="small"
              aria-labelledby="instructions-for"
            />
            <DatePicker
              v-if="span === 'custom'"
              v-model="custom"
              input-id="instructions-until"
              show-time
              hour-format="24"
              :min-date="minDate"
              :max-date="maxDate"
              placeholder="Until"
              aria-label="Until"
              size="small"
              :invalid="!!shown.until"
            />
          </div>
          <small v-if="shown.until" class="field-error">{{ shown.until }}</small>
          <small v-else-if="until" class="field-hint">Until {{ formatAbsolute(until) }}.</small>
        </div>
        <Message v-if="errors.kind" severity="error" :closable="false" size="small">{{ errors.kind }}</Message>
        <div class="form-actions">
          <Button type="submit" label="Create" icon="pi pi-plus" :loading="busy" />
        </div>
      </form>
    </section>

    <section class="panel" aria-labelledby="instructions-list-title">
      <div id="instructions-list-title" class="panel-header">
        In force and recent <span class="muted normal small">(the last 30 days)</span>
      </div>
      <ErrorState
        v-if="list.isError.value && !list.data.value"
        :error="list.error.value"
        what="the instructions"
        @retry="list.refetch()"
      />
      <DataTable v-else :value="rows" size="small" data-key="id" :loading="!list.data.value">
        <template #empty><span class="muted">None in the last 30 days.</span></template>
        <Column header="What">
          <template #body="{ data }">
            <div class="line">
              <Tag :value="instructionKindLabel(data.kind)" severity="secondary" />
              <span v-if="instructionTarget(data)" class="target">{{ instructionTarget(data) }}</span>
            </div>
            <div v-if="data.text" class="muted small wrap">{{ data.text }}</div>
          </template>
        </Column>
        <Column header="State" style="width: 7rem">
          <template #body="{ data }">
            <Tag :value="stateOf(data)" :severity="instructionStateSeverity(stateOf(data))" />
          </template>
        </Column>
        <Column header="Until" style="width: 13rem">
          <template #body="{ data }">
            <div class="small nowrap">{{ formatAbsolute(data.until) }}</div>
            <RelativeTime :value="data.until" class="muted small" />
          </template>
        </Column>
        <Column header="By" style="width: 14rem">
          <template #body="{ data }">
            <div class="small">{{ data.createdByName || '–' }}, {{ instructionSourceLabel(data.source) }}</div>
            <div class="muted small"><RelativeTime :value="data.createdAt" /></div>
            <div v-if="data.endedAt" class="muted small">
              ended{{ data.endedByName ? ` by ${data.endedByName}` : '' }}, <RelativeTime :value="data.endedAt" />
            </div>
          </template>
        </Column>
        <Column header="" style="width: 8rem">
          <template #body="{ data }">
            <Button
              v-if="stateOf(data) === 'active' || stateOf(data) === 'draft'"
              :label="stateOf(data) === 'draft' ? 'Discard' : 'End now'"
              size="small"
              severity="secondary"
              outlined
              :loading="ending === data.id"
              :disabled="ending !== null"
              @click="end(data)"
            />
          </template>
        </Column>
      </DataTable>
      <p class="panel-body muted small note">
        A draft comes from the chat: Cookie filled it in from someone's message, and it counts once that person confirms
        it there. One from a lesson is a rule the lessons summariser found in people's ratings; it lasts 30 days.
      </p>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 1100px;
}

.intro {
  margin: 0;
  max-width: 90ch;
}

.what {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-3);
  align-items: flex-start;
}

.grow {
  flex: 1;
  min-width: 14rem;
}

.scope {
  width: 12rem;
}

.span {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
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

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.normal {
  font-weight: 400;
}

.note {
  margin: 0;
  border-top: 1px solid var(--pnmt-border);
}

.small {
  font-size: 0.857rem;
}
</style>
