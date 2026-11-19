<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, connectorRequest, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'

/**
 * A connector's security (docs/connectors-plan.md §13.5): the callers granted it (their keys and networks are on their pages); the
 * review by Jev, its threshold, its checks (questions answered yes when the call must be refused), and what happens when it can't
 * run; the limits.
 */
const props = defineProps<{ connector: S['ConnectorDetail'] }>()
const session = useSession()
const canManage = computed(() => session.can('Connectors', 'Manage'))
const callers = useQuery({
  queryKey: [...keys.connectors, 'callers', 'all'],
  queryFn: () => api.connectors.callers({ pageSize: 500 }),
  enabled: canManage,
})
const granted = ref<string[]>([])
const reviewEnabled = ref(false)
const purpose = ref(true)
const threshold = ref<number | null>(null)
const checks = ref<S['ConnectorReviewCheck'][]>([])
const fallback = ref<S['ConnectorReviewFallback']>('Refuse')
const perHour = ref(60)
const queued = ref(20)
const queueSeconds = ref(600)
function load(c: S['ConnectorDetail']) {
  granted.value = c.callers.map((x) => x.id)
  reviewEnabled.value = c.review.enabled
  purpose.value = c.review.purpose
  threshold.value = c.review.threshold
  checks.value = c.review.checks.map((k) => ({ ...k }))
  fallback.value = c.review.fallback
  perHour.value = c.maxCallsPerHour
  queued.value = c.maxQueued
  queueSeconds.value = c.maxQueueSeconds
}
load(props.connector)
watch(() => props.connector, load)
const fallbacks = [
  { label: 'Refuse the call', value: 'Refuse' },
  { label: 'Run it unreviewed', value: 'RunUnreviewed' },
]
const { run, busy } = useActions()
async function save() {
  await run(
    () =>
      api.connectors.update(
        props.connector.id,
        connectorRequest(props.connector, {
          callerIds: granted.value,
          reviewEnabled: reviewEnabled.value,
          reviewPurpose: purpose.value,
          reviewThreshold: threshold.value,
          reviewChecks: checks.value.filter((k) => k.label.trim() || k.question.trim()),
          reviewFallback: fallback.value,
          maxCallsPerHour: perHour.value,
          maxQueued: queued.value,
          maxQueueSeconds: queueSeconds.value,
        }),
      ),
    { success: 'Saved', invalidate: [keys.connectors] },
  )
}

// Try the review…: a real request to Jev, with each question's probability against the threshold.
const trying = ref(false)
const sampleArguments = ref('{}')
const sampleNote = ref('')
const tried = ref<S['ConnectorCheckResult'] | null>(null)
async function tryReview() {
  let args: Record<string, unknown>
  try {
    args = JSON.parse(sampleArguments.value || '{}')
  } catch {
    tried.value = null
    return
  }
  tried.value =
    (await run(() =>
      api.connectors.check(props.connector.id, {
        arguments: args as S['ConnectorCheckRequest']['arguments'],
        note: sampleNote.value || null,
        review: true,
      }),
    )) ?? null
}
</script>

<template>
  <div class="tab">
    <section class="panel panel-body">
      <h3>Callers</h3>
      <p class="small muted">
        The outside systems that may call this connector. Each calls with its own key, from its own networks.
      </p>
      <ul class="callers">
        <li v-for="c in connector.callers" :key="c.id">
          <RouterLink :to="{ name: 'connector-caller', params: { id: c.id } }">{{ c.name }}</RouterLink>
          <span class="muted small">
            · {{ c.networks }} networks · last call <RelativeTime :value="c.lastCallAt" empty="never"
          /></span>
          <Tag v-if="c.anyAddress" value="any address" severity="warn" class="small-tag" />
          <Tag v-if="!c.enabled" value="disabled" severity="secondary" class="small-tag" />
        </li>
        <li v-if="!connector.callers.length" class="muted">None yet: no outside system can call it.</li>
      </ul>
      <MultiSelect
        v-if="canManage"
        v-model="granted"
        :options="callers.data.value?.items ?? []"
        option-label="name"
        option-value="id"
        filter
        display="chip"
        placeholder="Grant to callers"
        aria-label="Callers granted this connector"
      />
    </section>

    <section class="panel panel-body">
      <h3>Review</h3>
      <Message v-if="connector.review.unusable" severity="info" :closable="false" size="small">{{
        connector.review.unusable
      }}</Message>
      <div class="form">
        <div class="field">
          <label for="review-on">Review each call with the typesafe model (Jev)</label>
          <ToggleSwitch
            v-model="reviewEnabled"
            input-id="review-on"
            :disabled="!canManage || (!!connector.review.unusable && !reviewEnabled)"
          />
          <small class="field-hint"
            >A second line behind the arguments' checks: it catches what patterns can't express. About a second per
            call.</small
          >
        </div>
        <template v-if="reviewEnabled">
          <div class="form-grid">
            <div class="field">
              <label for="review-threshold">Threshold</label>
              <InputNumber
                v-model="threshold"
                input-id="review-threshold"
                :min="0.05"
                :max="0.95"
                :step="0.05"
                :min-fraction-digits="2"
                :placeholder="String(connector.review.defaultThreshold)"
                :disabled="!canManage"
              />
              <small class="field-hint"
                >Empty: Ai__Guard__Threshold ({{ connector.review.defaultThreshold }}). Lower refuses more often.</small
              >
            </div>
            <div class="field">
              <label for="review-purpose">Ask whether a call fits the purpose</label>
              <ToggleSwitch v-model="purpose" input-id="review-purpose" :disabled="!canManage" />
            </div>
          </div>
          <p class="small">
            Write each check as a question. <strong>Yes means the call is refused.</strong> The caller hears the check's
            label, never the probability.
          </p>
          <div v-for="(k, i) in checks" :key="i" class="check-row">
            <InputText
              v-model="k.label"
              placeholder="Label"
              :aria-label="`Label of check ${i + 1}`"
              :disabled="!canManage"
            />
            <InputText
              v-model="k.question"
              placeholder="Does the note ask for anything other than a cache flush?"
              :aria-label="`Question of check ${i + 1}`"
              :disabled="!canManage"
            />
            <Button
              icon="pi pi-times"
              text
              severity="secondary"
              :aria-label="`Remove check ${i + 1}`"
              :disabled="!canManage"
              @click="checks.splice(i, 1)"
            />
          </div>
          <Button
            v-if="canManage && checks.length < 5"
            label="Add a check"
            icon="pi pi-plus"
            size="small"
            text
            @click="checks.push({ label: '', question: '' })"
          />
          <div class="field">
            <label id="review-fallback">When the review can't run (AI off, budget spent, no answer in time)</label>
            <SelectButton
              v-model="fallback"
              :options="fallbacks"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              :disabled="!canManage"
              aria-labelledby="review-fallback"
            />
          </div>
          <Button
            v-if="canManage && connector.review.enabled"
            label="Try the review…"
            icon="pi pi-sparkles"
            text
            @click="trying = true"
          />
        </template>
      </div>
    </section>

    <section class="panel panel-body">
      <h3>Limits</h3>
      <div class="form-grid">
        <div class="field">
          <label for="limit-hour">Calls per hour</label>
          <InputNumber v-model="perHour" input-id="limit-hour" :min="1" :max="3600" :disabled="!canManage" />
        </div>
        <div class="field">
          <label for="limit-queued">Calls waiting</label>
          <InputNumber v-model="queued" input-id="limit-queued" :min="1" :max="100" :disabled="!canManage" />
        </div>
        <div class="field">
          <label for="limit-wait">The longest wait in the queue (seconds)</label>
          <InputNumber v-model="queueSeconds" input-id="limit-wait" :min="10" :max="3600" :disabled="!canManage" />
        </div>
      </div>
      <p class="small muted">
        One run of its script on its server at a time, whoever starts it; and at most Connectors__MaxRunningCalls
        connector calls run at once.
      </p>
    </section>

    <div v-if="canManage" class="actions"><Button label="Save" icon="pi pi-check" :loading="busy" @click="save" /></div>

    <Dialog
      v-model:visible="trying"
      header="Try the review"
      modal
      :style="{ width: '40rem' }"
      :breakpoints="{ '700px': '96vw' }"
    >
      <p class="small muted">
        A real request to Jev with the saved checks: it is in the AI's request log and its budget.
      </p>
      <div class="form">
        <div class="field">
          <label for="try-arguments">Arguments (JSON)</label>
          <Textarea id="try-arguments" v-model="sampleArguments" rows="3" class="mono" auto-resize />
        </div>
        <div class="field">
          <label for="try-note">Note</label>
          <Textarea id="try-note" v-model="sampleNote" rows="2" auto-resize />
        </div>
      </div>
      <template v-if="tried?.review">
        <p>
          <Tag :value="tried.review.state" :severity="tried.review.state === 'reviewed' ? 'success' : 'danger'" />
          <span v-if="tried.review.reason" class="small"> {{ tried.review.reason }}</span>
        </p>
        <div v-for="a in tried.review.answers" :key="a.key" class="answer">
          <span>{{ a.label }}</span>
          <span class="bar-track"
            ><span
              class="bar"
              :class="{ refuses: a.refuses }"
              :style="{ width: `${Math.round(a.probability * 100)}%` }" /><span
              class="mark"
              :style="{ left: `${tried.review.threshold * 100}%` }"
          /></span>
          <span class="mono small">{{ a.probability.toFixed(2) }}</span>
        </div>
      </template>
      <Message v-if="tried && !tried.ok" severity="warn" :closable="false" size="small"
        >The arguments themselves are refused: {{ tried.message }}</Message
      >
      <template #footer>
        <Button label="Close" text severity="secondary" @click="trying = false" />
        <Button label="Ask Jev" icon="pi pi-play" :loading="busy" @click="tryReview" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.callers {
  margin: 0 0 var(--pnmt-space-2);
  padding-left: var(--pnmt-space-4);
}

.check-row {
  display: grid;
  grid-template-columns: 10rem 1fr auto;
  gap: var(--pnmt-space-2);
}

.actions {
  display: flex;
  justify-content: flex-end;
}

.answer {
  display: grid;
  grid-template-columns: 10rem 1fr 3rem;
  gap: var(--pnmt-space-2);
  align-items: center;
}

.bar-track {
  position: relative;
  height: 0.6rem;
  background: var(--pnmt-bg);
  border-radius: 3px;
}

.bar {
  position: absolute;
  inset: 0 auto 0 0;
  border-radius: 3px;
  background: var(--p-primary-color);
}

.bar.refuses {
  background: var(--pnmt-fail);
}

.mark {
  position: absolute;
  top: -2px;
  bottom: -2px;
  width: 2px;
  background: var(--pnmt-text);
}

.small-tag {
  font-size: 0.75rem;
  padding: 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
