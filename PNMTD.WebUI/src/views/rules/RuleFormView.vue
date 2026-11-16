<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { api, keys, type RuleType } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import { useActions } from '@/composables/useActions'
import { humanize, useRuleTypes } from '@/composables/useLookups'
import RuleRecipientFields from './RuleRecipientFields.vue'
import { recipientDraft, recipientErrors, recipientRequest } from './ruleHelp'

/**
 * A new notification rule (§5.6): whom it tells, an address, a user, a group or the responsible (docs/notification-recipients-plan.md
 * §7.2), and when; sensors are linked on its page.
 */
const router = useRouter()
const ruleTypes = useRuleTypes()
const { invalidate, notify } = useActions()

const name = ref('')
const recipient = ref(recipientDraft())
const type = ref<RuleType>('ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK')
const enabled = ref(true)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)
const description = computed(() => ruleTypes.list.value.find((t) => t.type === type.value)?.description)

async function submit() {
  errors.value = {
    name: name.value.trim() ? undefined : 'A rule needs a name.',
    ...recipientErrors(recipient.value),
  }
  if (Object.values(errors.value).some((e) => e)) {
    return
  }
  busy.value = true
  try {
    const rule = await api.rules.create({
      name: name.value.trim(),
      type: type.value,
      enabled: enabled.value,
      ...recipientRequest(recipient.value),
    })
    await invalidate([keys.rules])
    notify.success(`Rule ${rule.name} created`, 'Link sensors to it on its page.')
    await router.replace({ name: 'notification-rule', params: { id: rule.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(
        ['name', 'recipient', 'type', 'recipientKind', 'userId', 'groupId', 'byEmail', 'hours'].map((f) => [
          f,
          e.field(f),
        ]),
      )
    } else {
      notify.error(e, 'Not created')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <PageHeader
      title="New notification rule"
      :crumbs="[{ label: 'Notification rules', to: { name: 'notification-rules' } }]"
    />
    <form class="panel panel-body form narrow" novalidate @submit.prevent="submit">
      <div class="field">
        <label for="rule-name">Name</label>
        <InputText id="rule-name" v-model="name" autofocus :invalid="!!errors.name" placeholder="Ops on-call" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <RuleRecipientFields v-model:draft="recipient" :errors="errors" />
      <div class="field">
        <label for="rule-type">Sends</label>
        <Select
          v-model="type"
          input-id="rule-type"
          :options="ruleTypes.list.value"
          :option-label="(o: { type: string }) => humanize(o.type)"
          option-value="type"
        />
        <small class="field-hint">{{ description }}</small>
      </div>
      <div class="switch-row">
        <ToggleSwitch v-model="enabled" input-id="rule-enabled" />
        <label for="rule-enabled">Enabled</label>
      </div>
      <div class="form-actions">
        <Button type="submit" label="Create rule" :loading="busy" />
        <Button label="Cancel" severity="secondary" text @click="router.back()" />
      </div>
    </form>
  </div>
</template>

<style scoped>
.narrow {
  max-width: 560px;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
