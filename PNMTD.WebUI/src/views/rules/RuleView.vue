<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import AutoComplete from 'primevue/autocomplete'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, ruleRequest, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { humanize, typeIcon, useRuleTypes } from '@/composables/useLookups'
import { useSession } from '@/stores/session'
import RuleRecipientFields from './RuleRecipientFields.vue'
import RuleScopes from './RuleScopes.vue'
import {
  providerIcon,
  providerLabel,
  reachText,
  recipientDetails,
  recipientDraft,
  recipientErrors,
  recipientRequest,
} from './ruleHelp'

/**
 * A notification rule (§5.6): its properties edited in place, with what each type sends; whom it tells, changed in a dialog,
 * with whom it reaches now and whom it last reached (docs/notification-recipients-plan.md §7.2); and its sensors, by host and
 * name, linked and unlinked from here.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const ruleTypes = useRuleTypes()
const queryClient = useQueryClient()
const { run, ask, invalidate, done, notify } = useActions()

const id = computed(() => String(route.params.id))
const query = useQuery({ queryKey: computed(() => keys.rule(id.value)), queryFn: () => api.rules.get(id.value) })
const rule = computed(() => query.data.value)
const canWrite = computed(() => session.can('Monitoring', 'Edit'))
const typeOptions = computed(() => ruleTypes.list.value.map((t) => ({ label: humanize(t.type), value: t.type })))
const description = computed(() => ruleTypes.list.value.find((t) => t.type === rule.value?.type)?.description)
const isAddress = computed(() => (rule.value?.recipientKind ?? 'Address') === 'Address')

async function save(patch: Partial<S['NotificationRuleRequest']>) {
  const updated = await api.rules.update(id.value, ruleRequest(rule.value!, patch))
  queryClient.setQueryData(keys.rule(id.value), updated)
  await invalidate([keys.rules, keys.notifications])
}

// Whom it tells: the same block as the form, in a dialog, with the server's field errors.
const changing = ref(false)
const draft = ref(recipientDraft())
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)
watch(changing, (open) => {
  if (open) {
    draft.value = recipientDraft(rule.value)
    errors.value = {}
  }
})
async function saveRecipient() {
  errors.value = recipientErrors(draft.value)
  if (Object.values(errors.value).some((e) => e)) return
  saving.value = true
  try {
    await save(recipientRequest(draft.value))
    changing.value = false
    notify.success('Recipient changed')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(
        ['recipient', 'recipientKind', 'userId', 'groupId', 'byEmail', 'hours'].map((f) => [f, e.field(f)]),
      )
    } else {
      notify.error(e, 'Not changed')
    }
  } finally {
    saving.value = false
  }
}

// Linking: sensors found by name (hosts are listed with them).
const pick = ref<S['SearchSensor'] | string | null>(null)
const found = ref<S['SearchSensor'][]>([])
async function search(event: { query: string }) {
  const result = await api.search(event.query, 15)
  const linked = new Set(rule.value?.sensors.map((s) => s.sensorId))
  found.value = result.sensors.filter((s) => !linked.has(s.id))
}
async function link(event: { value: S['SearchSensor'] }) {
  pick.value = null
  await run(() => api.rules.link(id.value, event.value.id), {
    success: `${event.value.hostName} / ${event.value.name} linked`,
    invalidate: [keys.rule(id.value), keys.rules],
  })
}
async function unlink(sensor: S['LinkedSensor']) {
  await run(() => api.rules.unlink(id.value, sensor.sensorId), {
    success: `${sensor.hostName} / ${sensor.sensorName} unlinked`,
    invalidate: [keys.rule(id.value), keys.rules],
  })
}

async function remove() {
  const r = rule.value!
  const ok = await ask(
    `Delete ${r.name}?`,
    `Its ${r.sensors.length} sensors stop notifying ${r.recipient}. This cannot be undone.`,
  )
  if (ok && (await done(() => api.rules.remove(r.id), { success: `${r.name} deleted`, invalidate: [keys.rules] }))) {
    await router.replace({ name: 'notification-rules' })
  }
}
const more = computed(() => [{ label: 'Delete rule…', icon: 'pi pi-trash', command: remove }])
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="query.isError.value && !rule"
      :error="query.error.value"
      what="the rule"
      @retry="query.refetch()"
    />
    <div v-else-if="!rule" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <PageHeader :title="rule.name" :crumbs="[{ label: 'Notification rules', to: { name: 'notification-rules' } }]">
        <template #status>
          <span class="muted provider"
            ><i :class="providerIcon(rule.provider)" /> {{ providerLabel(rule.provider) }}</span
          >
          <span v-if="!rule.enabled" class="muted">· off</span>
        </template>
        <template #actions>
          <MoreMenu v-if="canWrite" :items="more" label="More actions for the rule" />
        </template>
      </PageHeader>
      <div class="two-columns">
        <section class="panel" aria-labelledby="props-title">
          <div id="props-title" class="panel-header">Properties</div>
          <dl class="panel-body facts">
            <dt>Name</dt>
            <dd>
              <InlineField
                :value="rule.name"
                label="Name"
                field="name"
                :readonly="!canWrite"
                :save="(v) => save({ name: String(v) })"
              />
            </dd>
            <dt>Send to</dt>
            <dd>
              <span class="recipient">
                <i :class="providerIcon(rule.provider)" class="muted" aria-hidden="true" />
                <span :class="{ mono: isAddress }">{{ rule.recipient }}</span>
                <Button
                  v-if="canWrite"
                  label="Change…"
                  size="small"
                  text
                  aria-label="Change whom the rule tells"
                  @click="changing = true"
                />
              </span>
              <small v-if="recipientDetails(rule)" class="field-hint block">{{ recipientDetails(rule) }}</small>
            </dd>
            <dt>Reaches now</dt>
            <dd>
              <span v-if="rule.reach?.why" class="nobody"
                ><i class="pi pi-exclamation-triangle" aria-hidden="true" /> {{ rule.reach.why }}</span
              >
              <span v-else>{{ reachText(rule) }}</span>
            </dd>
            <dt>Last sent</dt>
            <dd>
              <RelativeTime :value="rule.lastSentAt" empty="never" />
              <small v-if="rule.lastSentTo" class="field-hint block">to {{ rule.lastSentTo }}</small>
            </dd>
            <dt>Sends</dt>
            <dd>
              <InlineField
                :value="rule.type"
                type="enum"
                :options="typeOptions"
                label="Sends"
                :readonly="!canWrite"
                :save="(v) => save({ type: v as S['NotificationRuleType'] })"
              />
              <small class="field-hint block">{{ description }}</small>
            </dd>
            <dt>Enabled</dt>
            <dd>
              <InlineField
                :value="rule.enabled"
                type="bool"
                label="Enabled"
                :readonly="!canWrite"
                :save="(v) => save({ enabled: Boolean(v) })"
              />
            </dd>
          </dl>
        </section>
        <section class="panel" aria-labelledby="linked-title">
          <div id="linked-title" class="panel-header">
            Sensors <span class="muted">{{ rule.sensors.length }}</span>
          </div>
          <div v-if="canWrite" class="panel-body link">
            <AutoComplete
              v-model="pick"
              :suggestions="found"
              option-label="name"
              placeholder="Link a sensor: type its name…"
              fluid
              size="small"
              :delay="200"
              :pt="{ pcInputText: { root: { 'aria-label': 'Sensor to link' } } }"
              @complete="search"
              @option-select="link"
            >
              <template #option="{ option }">
                <span class="suggestion">
                  <i :class="typeIcon(option.type)" class="muted" />
                  <span
                    >{{ option.hostName }} / <strong>{{ option.name }}</strong></span
                  >
                </span>
              </template>
            </AutoComplete>
          </div>
          <EmptyState
            v-if="!rule.sensors.length"
            icon="pi pi-bell-slash"
            title="No sensor linked."
            text="It notifies nobody yet."
          />
          <DataTable
            v-else
            :value="rule.sensors"
            size="small"
            data-key="sensorId"
            sort-field="hostName"
            :sort-order="1"
          >
            <Column field="hostName" header="Host" sortable>
              <template #body="{ data }"
                ><RouterLink :to="{ name: 'host', params: { id: data.hostId } }">{{
                  data.hostName
                }}</RouterLink></template
              >
            </Column>
            <Column field="sensorName" header="Sensor" sortable>
              <template #body="{ data }">
                <span class="sensor"
                  ><i :class="typeIcon(data.type)" class="muted" />
                  <RouterLink :to="{ name: 'sensor', params: { id: data.sensorId } }">{{
                    data.sensorName
                  }}</RouterLink></span
                >
              </template>
            </Column>
            <Column v-if="canWrite" header="" style="width: 3rem">
              <template #body="{ data }">
                <Button
                  icon="pi pi-times"
                  text
                  rounded
                  size="small"
                  severity="secondary"
                  :aria-label="`Unlink ${data.hostName} / ${data.sensorName}`"
                  @click="unlink(data)"
                />
              </template>
            </Column>
          </DataTable>
        </section>
      </div>
      <RuleScopes :rule="rule" :can-write="canWrite" class="scopes" />

      <Dialog
        v-model:visible="changing"
        header="Whom the rule tells"
        modal
        :style="{ width: '34rem' }"
        :breakpoints="{ '600px': '95vw' }"
      >
        <form class="form" novalidate @submit.prevent="saveRecipient">
          <RuleRecipientFields v-model:draft="draft" :errors="errors" />
        </form>
        <template #footer>
          <Button label="Cancel" severity="secondary" text @click="changing = false" />
          <Button label="Save" :loading="saving" @click="saveRecipient" />
        </template>
      </Dialog>
    </template>
  </div>
</template>

<style scoped>
.scopes {
  margin-top: var(--pnmt-space-3);
}
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.provider,
.recipient {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.recipient {
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.mono {
  font-family: var(--pnmt-mono);
}

.nobody {
  color: var(--pnmt-warn);
}

.block {
  display: block;
}

.link {
  border-bottom: 1px solid var(--pnmt-border);
}

.suggestion,
.sensor {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
