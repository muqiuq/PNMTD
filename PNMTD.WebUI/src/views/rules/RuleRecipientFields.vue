<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import { computed, useId } from 'vue'
import { hoursOptions, recipientHint, recipientKinds, standInOptions, type RecipientDraft } from './ruleHelp'
import { useRecipientPickers, type PickerOption } from './useRecipientPickers'

/**
 * Whom a rule tells (docs/notification-recipients-plan.md §7.1): an address as always (preselected: the same field as before),
 * a user, a group, or the responsible with the hours and a stand-in; the channels for the person kinds. Shared by the new rule's
 * form and the rule page's Change… dialog. Errors come by the server's field names.
 */
const draft = defineModel<RecipientDraft>('draft', { required: true })
defineProps<{ errors: Record<string, string | undefined> }>()
const uid = useId()
const person = computed(() => draft.value.recipientKind !== 'Address')
const pickers = useRecipientPickers(person)
const standInUser = computed(() => draft.value.recipientKind === 'Responsible' && draft.value.standIn === 'user')
const standInGroup = computed(() => draft.value.recipientKind === 'Responsible' && draft.value.standIn === 'group')

/** A chosen user or group the list no longer has: shown as removed, so the choice can be read and changed. */
function withChosen(options: PickerOption[], id: string | null, removed: string): PickerOption[] {
  return id && !options.some((o) => o.value === id) ? [...options, { label: removed, value: id }] : options
}
const userOptions = computed(() => withChosen(pickers.users.value, draft.value.userId, 'A removed user'))
const groupOptions = computed(() => withChosen(pickers.groups.value, draft.value.groupId, 'A removed group'))
</script>

<template>
  <div class="field">
    <label :id="`${uid}-kind`">Send to</label>
    <SelectButton
      v-model="draft.recipientKind"
      :options="recipientKinds"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      size="small"
      :aria-labelledby="`${uid}-kind`"
    />
  </div>

  <div v-if="draft.recipientKind === 'Address'" class="field">
    <label :for="`${uid}-recipient`">Recipient</label>
    <InputText :id="`${uid}-recipient`" v-model="draft.recipient" class="mono" :invalid="!!errors.recipient" />
    <small v-if="errors.recipient" class="field-error">{{ errors.recipient }}</small>
    <small v-else class="field-hint">{{ recipientHint }}</small>
  </div>

  <div v-if="draft.recipientKind === 'User'" class="field">
    <label :for="`${uid}-user`">User</label>
    <Select
      v-model="draft.userId"
      :input-id="`${uid}-user`"
      :options="userOptions"
      option-label="label"
      option-value="value"
      filter
      placeholder="Choose a user"
      :loading="pickers.query.isLoading.value"
      :invalid="!!errors.userId"
    >
      <template #option="{ option }">
        <span class="option"
          >{{ option.label }} <span class="muted small">{{ option.hint }}</span></span
        >
      </template>
    </Select>
    <small v-if="errors.userId" class="field-error">{{ errors.userId }}</small>
    <small v-else class="field-hint">Reached at the e-mail address and Pushover key of their account.</small>
  </div>

  <div v-if="draft.recipientKind === 'Group'" class="field">
    <label :for="`${uid}-group`">Group</label>
    <Select
      v-model="draft.groupId"
      :input-id="`${uid}-group`"
      :options="groupOptions"
      option-label="label"
      option-value="value"
      filter
      placeholder="Choose a group"
      :loading="pickers.query.isLoading.value"
      :invalid="!!errors.groupId"
    >
      <template #option="{ option }">
        <span class="option"
          >{{ option.label }} <span class="muted small">{{ option.hint }}</span></span
        >
      </template>
    </Select>
    <small v-if="errors.groupId" class="field-error">{{ errors.groupId }}</small>
    <small v-else class="field-hint"
      >Every active member at the time of the send, each at their own contact details.</small
    >
  </div>

  <template v-if="draft.recipientKind === 'Responsible'">
    <p class="field-hint explain">
      Whom the availability plan makes responsible today: the day's responsible if they work, else the fallback, else
      the plan's global fallback. Alerts go out whatever the hour, unless the rule says working hours only.
    </p>
    <div class="field">
      <label :for="`${uid}-hours`">Hours</label>
      <Select
        v-model="draft.hours"
        :input-id="`${uid}-hours`"
        :options="hoursOptions"
        option-label="label"
        option-value="value"
      />
      <small class="field-hint"
        >In working hours only: the night and the closed days are the stand-in's, or nobody's.</small
      >
    </div>
    <div class="field">
      <label :id="`${uid}-standin`">When nobody is responsible</label>
      <SelectButton
        v-model="draft.standIn"
        :options="standInOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        :aria-labelledby="`${uid}-standin`"
      />
      <Select
        v-if="standInUser"
        v-model="draft.userId"
        :options="userOptions"
        option-label="label"
        option-value="value"
        filter
        placeholder="Choose the stand-in user"
        aria-label="Stand-in user"
        :loading="pickers.query.isLoading.value"
        :invalid="!!errors.userId"
      >
        <template #option="{ option }">
          <span class="option"
            >{{ option.label }} <span class="muted small">{{ option.hint }}</span></span
          >
        </template>
      </Select>
      <Select
        v-if="standInGroup"
        v-model="draft.groupId"
        :options="groupOptions"
        option-label="label"
        option-value="value"
        filter
        placeholder="Choose the stand-in group"
        aria-label="Stand-in group"
        :loading="pickers.query.isLoading.value"
        :invalid="!!errors.groupId"
      >
        <template #option="{ option }">
          <span class="option"
            >{{ option.label }} <span class="muted small">{{ option.hint }}</span></span
          >
        </template>
      </Select>
      <small v-if="errors.userId || errors.groupId" class="field-error">{{ errors.userId ?? errors.groupId }}</small>
      <small v-else class="field-hint"
        >Told whether or not they work: a stand-in is for when the plan has nobody.</small
      >
    </div>
  </template>

  <div v-if="person" class="field">
    <label :id="`${uid}-channels`">Channels</label>
    <div class="channels" role="group" :aria-labelledby="`${uid}-channels`">
      <span class="checkbox">
        <Checkbox v-model="draft.byEmail" binary :input-id="`${uid}-email`" :invalid="!!errors.byEmail" />
        <label :for="`${uid}-email`">E-mail</label>
      </span>
      <span class="checkbox">
        <Checkbox v-model="draft.byPushover" binary :input-id="`${uid}-pushover`" :invalid="!!errors.byEmail" />
        <label :for="`${uid}-pushover`">Pushover</label>
      </span>
    </div>
    <small v-if="errors.byEmail" class="field-error">{{ errors.byEmail }}</small>
    <small v-else class="field-hint">People without the chosen details are skipped.</small>
  </div>
</template>

<style scoped>
.mono {
  font-family: var(--pnmt-mono);
}

.explain {
  margin: 0;
}

.option {
  display: inline-flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
}

.channels {
  display: flex;
  gap: var(--pnmt-space-4);
}

.checkbox {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
