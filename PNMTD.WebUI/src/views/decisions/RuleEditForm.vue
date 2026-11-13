<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'

/**
 * "Edit, then create" (docs/ai-operations-plan.md §5.3): the proposed rule in a form. Saving creates the rule with the
 * same validation as the rule form, and accepts the decision.
 */
const props = defineProps<{ proposal: S['MailRuleProposal']; busy: boolean }>()
const emit = defineEmits<{ create: [rule: S['MailInputRuleRequest']]; cancel: [] }>()

const p = props.proposal
const actionOf = (a: string): S['MailRuleAction'] =>
  a === 'ai' ? 'AskAi' : a === 'delete' ? 'Delete' : a === 'ignore' ? 'Ignore' : 'Event'
const name = ref(p.name)
const action = ref<S['MailRuleAction']>(actionOf(p.action))
const fromTest = ref(p.fromTest)
const subjectTest = ref(p.subjectTest ?? '')
const okTest = ref(p.okTest ?? '')
const failTest = ref(p.failTest ?? '')
const sensorId = ref<string | null>(p.sensorId ?? null)
const otherwise = ref<'ok' | 'fail'>(p.defaultOutcome === 'fail' ? 'fail' : 'ok')
const needsSensor = computed(() => action.value === 'Event' || action.value === 'AskAi')

const sensors = useQuery({
  queryKey: [...keys.sensors, 'all-for-rules'],
  queryFn: () => api.sensors.list({ pageSize: 500, sort: 'host' }),
})
const sensorOptions = computed(() =>
  (sensors.data.value?.items ?? []).map((s) => ({ label: `${s.host.name} · ${s.name}`, value: s.id })),
)
const actions: { label: string; value: S['MailRuleAction'] }[] = [
  { label: 'An event on a sensor', value: 'Event' },
  { label: 'The AI decides every time', value: 'AskAi' },
  { label: 'Ignore such mails', value: 'Ignore' },
  { label: 'Delete such mails', value: 'Delete' },
]

function submit() {
  const event = needsSensor.value
  emit('create', {
    name: name.value.trim(),
    enabled: true,
    fromTest: fromTest.value,
    subjectTest: subjectTest.value || null,
    okTest: event ? okTest.value || null : null,
    okCode: event && okTest.value ? 200 : null,
    failTest: event ? failTest.value || null : null,
    failCode: event && failTest.value ? 500 : null,
    defaultCode: otherwise.value === 'fail' ? 500 : 200,
    extractMessageRegex: event ? (p.extractMessageRegex ?? null) : null,
    sensorId: event ? sensorId.value : null,
    action: action.value,
    askAiWhenUnsure: false,
    verifyAi: true,
  })
}
</script>

<template>
  <form class="form edit" novalidate @submit.prevent="submit">
    <div class="field">
      <label for="edit-name">Name</label>
      <InputText id="edit-name" v-model="name" />
    </div>
    <div class="field">
      <label for="edit-action">Does</label>
      <Select v-model="action" input-id="edit-action" :options="actions" option-label="label" option-value="value" />
    </div>
    <div class="field">
      <label for="edit-from">Sender (regular expression)</label>
      <InputText id="edit-from" v-model="fromTest" class="mono" />
    </div>
    <div class="field">
      <label for="edit-subject">Subject (regular expression)</label>
      <InputText id="edit-subject" v-model="subjectTest" class="mono" />
    </div>
    <template v-if="needsSensor">
      <div class="field">
        <label for="edit-sensor">Sensor</label>
        <Select
          v-model="sensorId"
          input-id="edit-sensor"
          :options="sensorOptions"
          option-label="label"
          option-value="value"
          filter
          placeholder="Choose a sensor"
        />
      </div>
      <div class="field">
        <label for="edit-ok">OK test</label>
        <InputText id="edit-ok" v-model="okTest" class="mono" />
      </div>
      <div class="field">
        <label for="edit-fail">FAIL test</label>
        <InputText id="edit-fail" v-model="failTest" class="mono" />
      </div>
      <div class="field">
        <label for="edit-otherwise">When neither matches</label>
        <Select
          v-model="otherwise"
          input-id="edit-otherwise"
          :options="[
            { label: 'OK (200)', value: 'ok' },
            { label: 'Failure (500)', value: 'fail' },
          ]"
          option-label="label"
          option-value="value"
        />
      </div>
    </template>
    <div class="form-actions">
      <Button
        type="submit"
        label="Create the rule"
        :loading="busy"
        :disabled="!name.trim() || !fromTest.trim() || (needsSensor && !sensorId)"
      />
      <Button label="Cancel" severity="secondary" text @click="emit('cancel')" />
    </div>
  </form>
</template>

<style scoped>
.edit {
  margin-top: var(--pnmt-space-2);
}
</style>
