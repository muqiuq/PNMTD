<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { computed, ref } from 'vue'
import { api, type S } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'

/**
 * Admin → Debug (docs/ai-operations-plan.md §8): tools to check what PNMT sends through. Each card sends through
 * the same provider as the notifications and shows what the provider's server answered. At most 10 sends a
 * minute; every send is in the audit log, with a rule's name or "a typed key", never the key.
 */
const status = useQuery({ queryKey: ['debug', 'status'], queryFn: api.debug.status })
const s = computed(() => status.data.value)

const target = ref<string>('typed')
const userKey = ref('')
const device = ref('')
const priority = ref(0)
const title = ref('')
const message = ref('')
const pushoverResult = ref<S['DebugResult'] | null>(null)
const pushoverRun = useActions()
const targets = computed(() => [
  { label: 'A user key, typed in', value: 'typed' },
  ...(s.value?.pushoverRules ?? []).map((r) => ({ label: `Rule: ${r.name}`, value: r.id })),
])
const priorities = [
  { label: 'Low (−1, quiet)', value: -1 },
  { label: 'Normal (0)', value: 0 },
  { label: 'High (1, bypasses quiet hours)', value: 1 },
]

async function sendPushover() {
  const typed = target.value === 'typed'
  pushoverResult.value =
    (await pushoverRun.run(
      () =>
        api.debug.pushover({
          ruleId: typed ? null : target.value,
          userKey: typed ? userKey.value.trim() : null,
          device: device.value.trim() || null,
          priority: priority.value,
          title: title.value.trim() || null,
          message: message.value.trim() || null,
        }),
      { failure: 'Not sent' },
    )) ?? null
}

const recipient = ref('')
const subject = ref('')
const body = ref('')
const mailResult = ref<S['DebugResult'] | null>(null)
const mailRun = useActions()

async function sendMail() {
  mailResult.value =
    (await mailRun.run(
      () =>
        api.debug.mail({
          recipient: recipient.value.trim(),
          subject: subject.value.trim() || null,
          body: body.value.trim() || null,
        }),
      { failure: 'Not sent' },
    )) ?? null
}

// Test the AI (docs/ai-operations-plan.md §8.4): a fixed prompt to each configured model.
const aiResults = ref<S['DebugAiResult'][] | null>(null)
const aiRun = useActions()
async function testAi() {
  aiResults.value = (await aiRun.run(() => api.debug.ai(), { failure: 'Not tested' })) ?? null
}
</script>

<template>
  <div class="page">
    <PageHeader title="Debug" />
    <p class="muted intro">
      Tools that check what PNMT sends, through the same providers as the notifications. Each shows what the provider's
      server answered. At most 10 sends a minute, and each is in the audit log.
    </p>
    <div class="two-columns">
      <section class="panel" aria-labelledby="pushover-title">
        <div id="pushover-title" class="panel-header">Send a test Pushover</div>
        <form class="panel-body form" novalidate @submit.prevent="sendPushover">
          <p class="muted small">
            <template v-if="s?.pushover.configured"
              >API key …{{ s.pushover.apiKeyEnd }} · <span class="mono">{{ s.pushover.endpoint }}</span></template
            >
            <template v-else-if="s">No API key: set <span class="mono">Pushover__ApiKey</span> in .env.</template>
          </p>
          <div class="field">
            <label for="pushover-target">To</label>
            <Select
              v-model="target"
              input-id="pushover-target"
              :options="targets"
              option-label="label"
              option-value="value"
            />
          </div>
          <div v-if="target === 'typed'" class="field">
            <label for="pushover-key">User key</label>
            <InputText id="pushover-key" v-model="userKey" class="mono" autocomplete="off" />
          </div>
          <div class="pair">
            <div class="field">
              <label for="pushover-device">Device (optional)</label>
              <InputText id="pushover-device" v-model="device" autocomplete="off" />
            </div>
            <div class="field">
              <label for="pushover-priority">Priority</label>
              <Select
                v-model="priority"
                input-id="pushover-priority"
                :options="priorities"
                option-label="label"
                option-value="value"
              />
            </div>
          </div>
          <div class="field">
            <label for="pushover-title-input">Title (optional)</label>
            <InputText id="pushover-title-input" v-model="title" placeholder="PNMT test from …" />
          </div>
          <div class="field">
            <label for="pushover-message">Message (optional)</label>
            <Textarea
              id="pushover-message"
              v-model="message"
              rows="2"
              auto-resize
              placeholder="PNMT test from … at …"
            />
          </div>
          <div class="form-actions">
            <Button
              type="submit"
              label="Send"
              icon="pi pi-send"
              :loading="pushoverRun.busy.value"
              :disabled="target === 'typed' && !userKey.trim()"
            />
          </div>
          <Message
            v-if="pushoverResult"
            :severity="pushoverResult.ok ? 'success' : 'error'"
            :closable="false"
            size="small"
            data-testid="pushover-result"
          >
            <strong>{{ pushoverResult.ok ? 'Sent' : 'Failed' }}</strong> to {{ pushoverResult.target }} in
            {{ pushoverResult.durationMs }} ms: {{ pushoverResult.detail }}
          </Message>
        </form>
      </section>

      <section class="panel" aria-labelledby="mail-title">
        <div id="mail-title" class="panel-header">Send a test e-mail</div>
        <form class="panel-body form" novalidate @submit.prevent="sendMail">
          <p class="muted small">
            <template v-if="s?.mail.configured"
              ><span class="mono">{{ s.mail.host }}:{{ s.mail.port }}</span> as {{ s.mail.sender
              }}<template v-if="s.mail.username">, signs in as {{ s.mail.username }}</template
              ><template v-if="s.mail.username && !s.mail.passwordSet"> (no password set)</template></template
            >
            <template v-else-if="s"
              >Not configured: set <span class="mono">Smtp__Host</span> and <span class="mono">Smtp__Sender</span> in
              .env.</template
            >
          </p>
          <div class="field">
            <label for="mail-recipient">To</label>
            <InputText id="mail-recipient" v-model="recipient" type="email" autocomplete="off" />
          </div>
          <div class="field">
            <label for="mail-subject">Subject (optional)</label>
            <InputText id="mail-subject" v-model="subject" placeholder="PNMT test from …" />
          </div>
          <div class="field">
            <label for="mail-body">Text (optional)</label>
            <Textarea id="mail-body" v-model="body" rows="3" auto-resize placeholder="PNMT test from … at …" />
          </div>
          <div class="form-actions">
            <Button
              type="submit"
              label="Send"
              icon="pi pi-send"
              :loading="mailRun.busy.value"
              :disabled="!recipient.trim()"
            />
          </div>
          <Message
            v-if="mailResult"
            :severity="mailResult.ok ? 'success' : 'error'"
            :closable="false"
            size="small"
            data-testid="mail-result"
          >
            <strong>{{ mailResult.ok ? 'Sent' : 'Failed' }}</strong> to {{ mailResult.target }} in
            {{ mailResult.durationMs }} ms: {{ mailResult.detail }}
          </Message>
        </form>
      </section>
    </div>
    <section class="panel ai-panel" aria-labelledby="ai-title">
      <div id="ai-title" class="panel-header">Test the AI</div>
      <div class="panel-body form">
        <p class="muted small">
          A fixed prompt to the main and the verification model, and a harmless text to the guard. Each answer is in the
          AI request log, with its time and cost.
        </p>
        <div class="form-actions">
          <Button label="Test" icon="pi pi-sparkles" :loading="aiRun.busy.value" @click="testAi" />
        </div>
        <Message
          v-for="r in aiResults ?? []"
          :key="r.role"
          :severity="r.ok ? 'success' : 'error'"
          :closable="false"
          size="small"
        >
          <strong>{{ r.role }}</strong> <span class="mono">{{ r.modelServed ?? r.model }}</span> in
          {{ r.durationMs }} ms: {{ r.detail
          }}<template v-if="r.cost !== null && r.cost !== undefined"> (${{ r.cost }})</template>
        </Message>
      </div>
    </section>
  </div>
</template>

<style scoped>
.ai-panel {
  margin-top: var(--pnmt-space-3);
}
.intro {
  margin: 0 0 var(--pnmt-space-3);
}
.pair {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--pnmt-space-3);
}
.small {
  font-size: 0.857rem;
}
.form-actions {
  justify-content: flex-start;
}
</style>
