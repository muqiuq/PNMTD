<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import type { S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { newId, withUnknown } from './cookieAdmin'
import { minutesOf } from './hours'
import ScopeValuePicker from './ScopeValuePicker.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useGroupOptions, useUserOptions } from './usePickers'
import { textOrNull, useSection } from './useSection'

/**
 * Notifications (docs/cookie-plan.md §8, §19.1): each person's contact details, which PNMT's users don't have (an
 * e-mail address, a Pushover key, the name Redmine shows as the assignee, quiet hours); and who gets what, by scope,
 * urgency and time, through which channels. The duties' reminders go to the assignee and the escalation group anyway;
 * these rules add people.
 */
const s = useSection('notifications', {
  prepare: (v) => ({
    contacts: v.contacts.map((c) => ({
      userId: c.userId,
      email: textOrNull(c.email),
      pushoverKey: textOrNull(c.pushoverKey),
      redmineName: textOrNull(c.redmineName),
      quietFrom: textOrNull(c.quietFrom),
      quietTo: textOrNull(c.quietTo),
    })),
    rules: v.rules.map((r) => ({
      ...r,
      name: r.name.trim(),
      value: r.scope === 'Everything' ? null : textOrNull(r.value),
    })),
  }),
})
const users = useUserOptions()
const groups = useGroupOptions()

// --- contacts -------------------------------------------------------------------------------------------------------
const userName = (id: string) => users.options.value.find((o) => o.value === id)?.label ?? users.unknown(id)
const newContact = ref<string | null>(null)
const addable = computed(() =>
  users.options.value.filter((o) => !(s.draft.value?.contacts ?? []).some((c) => c.userId === o.value)),
)
function addContact(userId: string | null) {
  if (!userId || !s.draft.value) return
  s.draft.value.contacts.push({
    userId,
    email: null,
    pushoverKey: null,
    redmineName: null,
    quietFrom: null,
    quietTo: null,
  })
  newContact.value = null
}
function removeContact(userId: string) {
  if (s.draft.value) s.draft.value.contacts = s.draft.value.contacts.filter((c) => c.userId !== userId)
}
/** The checks the server makes on a contact, as a hint while typing. */
function contactHints(c: S['CookieContact']): string[] {
  const hints: string[] = []
  if (c.pushoverKey?.trim() && !/^[A-Za-z0-9]{30}$/.test(c.pushoverKey.trim()))
    hints.push('A Pushover user key has 30 letters and digits.')
  if (c.email?.trim() && !/^[^\s@]+@[^\s@]+$/.test(c.email.trim())) hints.push('Not an e-mail address.')
  if ([c.quietFrom, c.quietTo].some((t) => t?.trim() && minutesOf(t) === null)) hints.push('Quiet hours as HH:mm.')
  return [...new Set([...hints, ...(s.errors.value[`contacts.${c.userId}`] ?? [])])]
}

// --- rules ----------------------------------------------------------------------------------------------------------
const scopes: { label: string; value: S['RecipientScope'] }[] = [
  { label: 'Everything', value: 'Everything' },
  { label: 'A location', value: 'Location' },
  { label: 'A host', value: 'Host' },
  { label: 'A Redmine project', value: 'Project' },
  { label: 'A kind of case', value: 'CaseKind' },
]
const urgencies: { label: string; value: S['Urgency'] }[] = [
  { label: 'Info', value: 'Info' },
  { label: 'Important', value: 'Important' },
  { label: 'Urgent', value: 'Urgent' },
]
const whens: { label: string; value: S['RecipientWhen'] }[] = [
  { label: 'Always', value: 'Always' },
  { label: 'In working hours', value: 'WorkingHours' },
  { label: 'Outside working hours', value: 'OutsideWorkingHours' },
]
const channelOptions: { label: string; value: S['RecipientChannel'] }[] = [
  { label: 'Dashboard', value: 'Dashboard' },
  { label: 'E-mail', value: 'Email' },
  { label: 'Pushover', value: 'Pushover' },
]
const userOptions = computed(() =>
  withUnknown(users.options.value, s.draft.value?.rules.flatMap((r) => r.users) ?? [], users.unknown),
)
const groupOptions = computed(() =>
  withUnknown(groups.options.value, s.draft.value?.rules.flatMap((r) => r.groups) ?? [], groups.unknown),
)
function addRule() {
  s.draft.value?.rules.push({
    id: newId(),
    name: '',
    scope: 'Everything',
    value: null,
    minUrgency: 'Urgent',
    when: 'Always',
    users: [],
    groups: [],
    channels: ['Email'],
  })
}
function removeRule(id: string) {
  if (s.draft.value) s.draft.value.rules = s.draft.value.rules.filter((r) => r.id !== id)
}
function changeScope(r: S['RecipientRule'], scope: S['RecipientScope']) {
  r.scope = scope
  r.value = null
}
const placed = (f: string) => f.startsWith('contacts.') || f.startsWith('rules.')
</script>

<template>
  <div class="tab">
    <ErrorState
      v-if="s.settings.isError.value && !s.draft.value"
      :error="s.settings.error.value"
      what="the settings"
      @retry="s.settings.refetch()"
    />
    <template v-else-if="s.draft.value">
      <Message v-if="!users.listed.value" severity="info" :closable="false" size="small">
        Only admins can list PNMT's users and groups: you see the chosen ones by their ids, and can't add others.
      </Message>

      <section class="panel" aria-labelledby="contacts-title">
        <div id="contacts-title" class="panel-header">
          Contacts
          <Select
            v-if="users.listed.value"
            v-model="newContact"
            :options="addable"
            option-label="label"
            option-value="value"
            filter
            placeholder="Add a contact for…"
            size="small"
            class="end"
            aria-label="Add a contact for"
            @update:model-value="addContact"
          />
        </div>
        <p class="muted small intro">
          How Cookie and PNMT's duties reach each person. Pushover only for urgent things, and never in quiet hours
          (which may span midnight). The Redmine name finds a ticket's assignee.
        </p>
        <DataTable :value="s.draft.value.contacts" size="small" data-key="userId" class="contacts">
          <template #empty><span class="muted">No contacts yet.</span></template>
          <Column header="User" style="width: 13rem">
            <template #body="{ data }">
              <span class="ellipsis cell" :title="userName(data.userId)">{{ userName(data.userId) }}</span>
              <div v-if="contactHints(data).length" class="field-error small">{{ contactHints(data).join(' ') }}</div>
            </template>
          </Column>
          <Column header="E-mail">
            <template #body="{ data }">
              <InputText
                v-model="data.email"
                size="small"
                fluid
                type="email"
                :aria-label="`${userName(data.userId)}: e-mail`"
              />
            </template>
          </Column>
          <Column header="Pushover user key">
            <template #body="{ data }">
              <InputText
                v-model="data.pushoverKey"
                size="small"
                fluid
                class="mono"
                maxlength="30"
                :aria-label="`${userName(data.userId)}: Pushover user key`"
              />
            </template>
          </Column>
          <Column header="Redmine name">
            <template #body="{ data }">
              <InputText
                v-model="data.redmineName"
                size="small"
                fluid
                placeholder="As Redmine shows the assignee"
                :aria-label="`${userName(data.userId)}: Redmine name`"
              />
            </template>
          </Column>
          <Column header="Quiet hours" style="width: 11rem">
            <template #body="{ data }">
              <span class="period">
                <InputText
                  v-model="data.quietFrom"
                  size="small"
                  class="time mono"
                  placeholder="22:00"
                  maxlength="5"
                  :aria-label="`${userName(data.userId)}: quiet from`"
                />
                <span>–</span>
                <InputText
                  v-model="data.quietTo"
                  size="small"
                  class="time mono"
                  placeholder="07:00"
                  maxlength="5"
                  :aria-label="`${userName(data.userId)}: quiet until`"
                />
              </span>
            </template>
          </Column>
          <Column style="width: 3rem">
            <template #body="{ data }">
              <Button
                icon="pi pi-trash"
                size="small"
                severity="secondary"
                text
                :aria-label="`Remove ${userName(data.userId)}'s contact`"
                @click="removeContact(data.userId)"
              />
            </template>
          </Column>
        </DataTable>
      </section>

      <section class="panel" aria-labelledby="rules-title">
        <div id="rules-title" class="panel-header">
          Who gets what
          <Button
            label="Add a rule"
            icon="pi pi-plus"
            size="small"
            severity="secondary"
            text
            class="end"
            @click="addRule"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            Each rule adds people for what it covers, from an urgency on, at the times it names, by its channels. PNMT
            picks the people from these rules; Cookie only ranks what matters most.
          </p>
          <p v-if="!s.draft.value.rules.length" class="muted">No rules yet.</p>
          <div v-for="r in s.draft.value.rules" :key="r.id" class="rule">
            <div class="form-grid">
              <div class="field">
                <label :for="`rule-${r.id}-name`">Name</label>
                <InputText :id="`rule-${r.id}-name`" v-model="r.name" size="small" />
              </div>
              <div class="field">
                <label :for="`rule-${r.id}-scope`">Covers</label>
                <div class="inline">
                  <Select
                    :model-value="r.scope"
                    :input-id="`rule-${r.id}-scope`"
                    :options="scopes"
                    option-label="label"
                    option-value="value"
                    size="small"
                    @update:model-value="(v: S['RecipientScope']) => changeScope(r, v)"
                  />
                  <ScopeValuePicker v-model="r.value" :scope="r.scope" />
                </div>
              </div>
              <div class="field">
                <label :id="`rule-${r.id}-urgency`">From the urgency</label>
                <SelectButton
                  v-model="r.minUrgency"
                  :options="urgencies"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                  size="small"
                  :aria-labelledby="`rule-${r.id}-urgency`"
                />
              </div>
              <div class="field">
                <label :for="`rule-${r.id}-when`">When</label>
                <Select
                  v-model="r.when"
                  :input-id="`rule-${r.id}-when`"
                  :options="whens"
                  option-label="label"
                  option-value="value"
                  size="small"
                />
              </div>
              <div class="field">
                <label :for="`rule-${r.id}-users`">Users</label>
                <MultiSelect
                  v-model="r.users"
                  :input-id="`rule-${r.id}-users`"
                  :options="userOptions"
                  option-label="label"
                  option-value="value"
                  filter
                  display="chip"
                  size="small"
                  placeholder="None"
                />
              </div>
              <div class="field">
                <label :for="`rule-${r.id}-groups`">Groups</label>
                <MultiSelect
                  v-model="r.groups"
                  :input-id="`rule-${r.id}-groups`"
                  :options="groupOptions"
                  option-label="label"
                  option-value="value"
                  filter
                  display="chip"
                  size="small"
                  placeholder="None"
                />
              </div>
              <div class="field wide">
                <label :id="`rule-${r.id}-channels`">Channels</label>
                <div class="inline">
                  <SelectButton
                    v-model="r.channels"
                    :options="channelOptions"
                    option-label="label"
                    option-value="value"
                    multiple
                    size="small"
                    :aria-labelledby="`rule-${r.id}-channels`"
                  />
                  <Button
                    label="Remove the rule"
                    icon="pi pi-trash"
                    size="small"
                    severity="secondary"
                    text
                    class="end"
                    @click="removeRule(r.id)"
                  />
                </div>
              </div>
            </div>
            <small v-if="s.error(`rules.${r.id}`)" class="field-error">{{
              (s.errors.value[`rules.${r.id}`] ?? []).join(' ')
            }}</small>
          </div>
        </div>
      </section>

      <SectionActions
        :busy="s.busy.value"
        :dirty="s.dirty.value"
        :errors="s.errors.value"
        :placed="placed"
        @save="s.save()"
        @reset="s.reset()"
      />
      <SettingsHistory section="notifications" title="the notifications" @rolled-back="s.reload()" />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro {
  margin: 0;
}

.panel > .intro {
  padding: var(--pnmt-space-2) var(--pnmt-space-3) 0;
}

.contacts {
  margin-top: var(--pnmt-space-2);
}

.cell {
  display: block;
}

.period,
.inline {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

.time {
  width: 4.25rem;
  text-align: center;
}

.rule {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding-bottom: var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.rule:last-child {
  border-bottom: none;
  padding-bottom: 0;
}

.end {
  margin-left: auto;
}

.small {
  font-size: 0.857rem;
}
</style>
