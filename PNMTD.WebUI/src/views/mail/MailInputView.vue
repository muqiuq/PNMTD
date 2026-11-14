<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'

/**
 * A mail input rule, new or edited (§5.6). Left: which mails it takes (regular expressions on
 * sender, subject and body). Right: what becomes of them (the code by the OK and fail tests, the
 * message, the sensor). The tests are checked as they are typed, and against the latest mails.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const { ask, invalidate, notify, done } = useActions()

const id = computed(() => (route.params.id ? String(route.params.id) : null))
const canWrite = computed(() => session.can('Monitoring', 'Edit'))
const existing = useQuery({
  queryKey: computed(() => [...keys.mail, 'rule', id.value]),
  queryFn: () => api.mail.rule(id.value!),
  enabled: computed(() => !!id.value),
})

const form = ref({
  name: '',
  enabled: true,
  fromTest: '',
  subjectTest: '',
  bodyTest: '',
  okTest: '',
  okCode: 200 as number | null,
  failTest: '',
  failCode: 500 as number | null,
  defaultCode: 500,
  extractMessageRegex: '',
  action: 'Event' as S['MailRuleAction'],
  askAiWhenUnsure: false,
  verifyAi: true,
})
/** Event and AI rules write events; Ignore and Delete rules only act when no event rule matched (docs/ai-operations-plan.md §6.4). */
const actions: { label: string; value: S['MailRuleAction']; hint: string }[] = [
  { label: 'An event on the sensor', value: 'Event', hint: 'As set below: OK, failed or otherwise.' },
  {
    label: 'The AI decides every time',
    value: 'AskAi',
    hint: 'Each mail goes to the AI, which says OK or failure; unclear ones come to Decisions.',
  },
  {
    label: 'Ignore such mails',
    value: 'Ignore',
    hint: 'Processed, without an event; only when no event rule matched.',
  },
  {
    label: 'Delete such mails',
    value: 'Delete',
    hint: 'Deleted from the mailbox; the log keeps them. Only when no event rule matched.',
  },
]
const writesEvents = computed(() => form.value.action === 'Event' || form.value.action === 'AskAi')
const sensor = ref<S['SensorRef'] | S['SearchSensor'] | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)

watch(
  () => existing.data.value,
  (r) => {
    if (!r) return
    form.value = {
      name: r.name,
      enabled: r.enabled,
      fromTest: r.fromTest ?? '',
      subjectTest: r.subjectTest ?? '',
      bodyTest: r.bodyTest ?? '',
      okTest: r.okTest ?? '',
      okCode: r.okCode,
      failTest: r.failTest ?? '',
      failCode: r.failCode,
      defaultCode: r.defaultCode,
      extractMessageRegex: r.extractMessageRegex ?? '',
      action: r.action ?? 'Event',
      askAiWhenUnsure: r.askAiWhenUnsure ?? false,
      verifyAi: r.verifyAi ?? true,
    }
    sensor.value = r.sensor
  },
  { immediate: true },
)

/** A quick check in the browser; the server (.NET) has the last word. */
function regexError(pattern: string): string | undefined {
  if (!pattern) return undefined
  try {
    new RegExp(pattern)
    return undefined
  } catch (e) {
    return `Not a valid regular expression: ${(e as Error).message.replace(/^Invalid regular expression: /, '')}`
  }
}
const regexFields = ['fromTest', 'subjectTest', 'bodyTest', 'okTest', 'failTest', 'extractMessageRegex'] as const
const liveErrors = computed(() => Object.fromEntries(regexFields.map((f) => [f, regexError(form.value[f])])))

// The sensor: found by name.
const found = ref<S['SearchSensor'][]>([])
async function searchSensors(event: { query: string }) {
  found.value = (await api.search(event.query, 15)).sensors
}
const sensorLabel = (s: S['SensorRef'] | S['SearchSensor'] | string | null) =>
  !s || typeof s === 'string' ? (s ?? '') : `${s.hostName} / ${s.name}`

// The latest mails, and which of them this rule would take (sender and subject; the body is on the server).
const recent = useQuery({
  queryKey: [...keys.mail, 'log', 'recent'],
  queryFn: () => api.mail.log({ pageSize: 12 }),
  enabled: canWrite,
})
function matches(pattern: string, text: string) {
  if (!pattern) return true
  try {
    return new RegExp(pattern).test(text)
  } catch {
    return false
  }
}

async function submit() {
  const f = form.value
  errors.value = { name: f.name.trim() ? undefined : 'A rule needs a name.', ...liveErrors.value }
  if (Object.values(errors.value).some((e) => e)) return
  busy.value = true
  const body: S['MailInputRuleRequest'] = {
    name: f.name.trim(),
    enabled: f.enabled,
    fromTest: f.fromTest || null,
    subjectTest: f.subjectTest || null,
    bodyTest: f.bodyTest || null,
    okTest: f.okTest || null,
    okCode: f.okCode,
    failTest: f.failTest || null,
    failCode: f.failCode,
    defaultCode: f.defaultCode,
    extractMessageRegex: f.extractMessageRegex || null,
    sensorId: sensor.value && typeof sensor.value !== 'string' ? sensor.value.id : null,
    action: f.action,
    askAiWhenUnsure: f.askAiWhenUnsure,
    verifyAi: f.verifyAi,
  }
  try {
    const rule = id.value ? await api.mail.updateRule(id.value, body) : await api.mail.createRule(body)
    await invalidate([keys.mail])
    notify.success(id.value ? `${rule.name} saved` : `${rule.name} created`)
    if (!id.value) await router.replace({ name: 'mail-input', params: { id: rule.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(Object.keys(e.errors).map((k) => [k, e.field(k)]))
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    busy.value = false
  }
}

async function remove() {
  const r = existing.data.value!
  const ok = await ask(
    `Delete ${r.name}?`,
    'Mails it would have taken are no longer turned into events. This cannot be undone.',
  )
  if (ok && (await done(() => api.mail.removeRule(r.id), { success: `${r.name} deleted`, invalidate: [keys.mail] }))) {
    await router.replace({ name: 'mail-inputs' })
  }
}
const more = computed(() => [{ label: 'Delete rule…', icon: 'pi pi-trash', command: remove }])
const err = (f: string) => errors.value[f] ?? liveErrors.value[f as (typeof regexFields)[number]]
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="id && existing.isError.value"
      :error="existing.error.value"
      what="the rule"
      @retry="existing.refetch()"
    />
    <div v-else-if="id && !existing.data.value" class="center">
      <ProgressSpinner style="width: 40px; height: 40px" />
    </div>
    <template v-else>
      <PageHeader
        :title="id ? form.name || 'Mail input' : 'New mail input'"
        :crumbs="[{ label: 'Mail inputs', to: { name: 'mail-inputs' } }]"
      >
        <template #actions>
          <MoreMenu v-if="id && canWrite" :items="more" label="More actions for the rule" />
        </template>
      </PageHeader>
      <form class="editor" novalidate @submit.prevent="submit">
        <fieldset class="panel" :disabled="!canWrite">
          <legend class="panel-header">Which mails</legend>
          <div class="panel-body form">
            <div class="field">
              <label for="mi-name">Name</label>
              <InputText id="mi-name" v-model="form.name" :invalid="!!err('name')" />
              <small v-if="err('name')" class="field-error">{{ err('name') }}</small>
            </div>
            <div class="field">
              <label for="mi-from">Sender (regex)</label>
              <InputText
                id="mi-from"
                v-model="form.fromTest"
                class="mono"
                placeholder="veeam@example\.org"
                :invalid="!!err('fromTest')"
              />
              <small v-if="err('fromTest')" class="field-error">{{ err('fromTest') }}</small>
            </div>
            <div class="field">
              <label for="mi-subject">Subject (regex)</label>
              <InputText id="mi-subject" v-model="form.subjectTest" class="mono" :invalid="!!err('subjectTest')" />
              <small v-if="err('subjectTest')" class="field-error">{{ err('subjectTest') }}</small>
            </div>
            <div class="field">
              <label for="mi-body">Body (regex)</label>
              <InputText id="mi-body" v-model="form.bodyTest" class="mono" :invalid="!!err('bodyTest')" />
              <small v-if="err('bodyTest')" class="field-error">{{ err('bodyTest') }}</small>
              <small v-else class="field-hint">A mail is taken when every test that is set matches.</small>
            </div>
            <div class="switch-row">
              <ToggleSwitch v-model="form.enabled" input-id="mi-enabled" />
              <label for="mi-enabled">Enabled</label>
            </div>
          </div>
        </fieldset>
        <fieldset class="panel" :disabled="!canWrite">
          <legend class="panel-header">What becomes of them</legend>
          <div class="panel-body form">
            <div class="field">
              <label for="mi-action">Action</label>
              <Select
                v-model="form.action"
                input-id="mi-action"
                :options="actions"
                option-label="label"
                option-value="value"
              />
              <small class="field-hint">{{ actions.find((a) => a.value === form.action)?.hint }}</small>
            </div>
            <div v-if="form.action === 'AskAi'" class="switch-row">
              <ToggleSwitch v-model="form.verifyAi" input-id="mi-verify" />
              <label for="mi-verify">Verify with the second model before an event</label>
            </div>
            <div v-if="writesEvents" class="pair">
              <div class="field grow">
                <label for="mi-ok">OK when (regex)</label>
                <InputText
                  id="mi-ok"
                  v-model="form.okTest"
                  class="mono"
                  placeholder="\[Success\]"
                  :invalid="!!err('okTest')"
                />
                <small v-if="err('okTest')" class="field-error">{{ err('okTest') }}</small>
              </div>
              <div class="field code">
                <label for="mi-okcode">Code</label>
                <InputNumber v-model="form.okCode" input-id="mi-okcode" :use-grouping="false" fluid />
              </div>
            </div>
            <div v-if="writesEvents" class="pair">
              <div class="field grow">
                <label for="mi-fail">Failed when (regex)</label>
                <InputText
                  id="mi-fail"
                  v-model="form.failTest"
                  class="mono"
                  placeholder="\[Failed\]"
                  :invalid="!!err('failTest')"
                />
                <small v-if="err('failTest')" class="field-error">{{ err('failTest') }}</small>
              </div>
              <div class="field code">
                <label for="mi-failcode">Code</label>
                <InputNumber v-model="form.failCode" input-id="mi-failcode" :use-grouping="false" fluid />
              </div>
            </div>
            <div v-if="form.action === 'Event'" class="switch-row">
              <ToggleSwitch v-model="form.askAiWhenUnsure" input-id="mi-unsure" />
              <label for="mi-unsure">Ask the AI when neither test matches: it proposes better tests</label>
            </div>
            <div v-if="writesEvents" class="field code-wide">
              <label for="mi-default">Otherwise code</label>
              <InputNumber v-model="form.defaultCode" input-id="mi-default" :use-grouping="false" fluid />
            </div>
            <div v-if="form.action === 'Event'" class="field">
              <label for="mi-extract">Message (regex, first group)</label>
              <InputText
                id="mi-extract"
                v-model="form.extractMessageRegex"
                class="mono"
                placeholder="Job (.+) finished"
                :invalid="!!err('extractMessageRegex')"
              />
              <small v-if="err('extractMessageRegex')" class="field-error">{{ err('extractMessageRegex') }}</small>
              <small v-else class="field-hint">Without it, the event's message names the rule.</small>
            </div>
            <div v-if="writesEvents" class="field">
              <label for="mi-sensor">Sensor</label>
              <AutoComplete
                v-model="sensor"
                input-id="mi-sensor"
                :suggestions="found"
                :option-label="sensorLabel"
                placeholder="Type a sensor's name…"
                fluid
                :delay="200"
                @complete="searchSensors"
              />
              <small v-if="err('sensorId')" class="field-error">{{ err('sensorId') }}</small>
            </div>
          </div>
        </fieldset>
        <div v-if="canWrite" class="form-actions actions">
          <Button type="submit" :label="id ? 'Save rule' : 'Create rule'" :loading="busy" />
          <Button label="Cancel" severity="secondary" text @click="router.back()" />
        </div>
      </form>

      <section v-if="canWrite && recent.data.value?.items.length" class="panel" aria-labelledby="recent-title">
        <div id="recent-title" class="panel-header">
          Latest mails <span class="muted">· sender and subject tested in the browser</span>
        </div>
        <ul class="recent">
          <li v-for="m in recent.data.value.items" :key="m.id">
            <i
              :class="
                matches(form.fromTest, m.from) && matches(form.subjectTest, m.subject)
                  ? 'pi pi-check-circle hit'
                  : 'pi pi-minus-circle muted'
              "
              :aria-label="
                matches(form.fromTest, m.from) && matches(form.subjectTest, m.subject)
                  ? 'would be taken'
                  : 'would not be taken'
              "
            />
            <span class="ellipsis from">{{ m.from }}</span>
            <span class="ellipsis subject">{{ m.subject }}</span>
            <RelativeTime :value="m.created" class="muted" />
          </li>
        </ul>
      </section>
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.editor {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3);
  align-items: start;
}

fieldset {
  margin: 0;
  padding: 0;
  min-width: 0;
}

legend.panel-header {
  float: left;
  width: 100%;
  box-sizing: border-box;
}

legend + .panel-body {
  clear: both;
}

.actions {
  grid-column: 1 / -1;
}

.mono {
  font-family: var(--pnmt-mono);
}

.pair {
  display: flex;
  gap: var(--pnmt-space-2);
}

.grow {
  flex: 1;
  min-width: 0;
}

.code {
  width: 6.5rem;
  flex: 0 0 auto;
}

.code-wide {
  width: 10rem;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.recent {
  list-style: none;
  margin: 0;
  padding: var(--pnmt-space-1) 0;
}

.recent li {
  display: grid;
  grid-template-columns: 1.25rem minmax(0, 14rem) minmax(0, 1fr) 7rem;
  gap: var(--pnmt-space-2);
  align-items: center;
  padding: 2px var(--pnmt-space-3);
}

.hit {
  color: var(--pnmt-ok);
}

@media (max-width: 959px) {
  .editor {
    grid-template-columns: minmax(0, 1fr);
  }

  .recent li {
    grid-template-columns: 1.25rem minmax(0, 1fr);
  }

  .recent .subject,
  .recent time {
    grid-column: 2;
  }
}
</style>
