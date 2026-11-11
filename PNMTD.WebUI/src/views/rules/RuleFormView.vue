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
import { recipientHint } from './ruleHelp'

/** A new notification rule (§5.6); sensors are linked on its page. */
const router = useRouter()
const ruleTypes = useRuleTypes()
const { invalidate, notify } = useActions()

const name = ref('')
const recipient = ref('')
const type = ref<RuleType>('ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK')
const enabled = ref(true)
const errors = ref<Record<string, string | undefined>>({})
const busy = ref(false)
const description = computed(() => ruleTypes.list.value.find((t) => t.type === type.value)?.description)

async function submit() {
  errors.value = {
    name: name.value.trim() ? undefined : 'A rule needs a name.',
    recipient: recipient.value.trim() ? undefined : recipientHint,
  }
  if (errors.value.name || errors.value.recipient) {
    return
  }
  busy.value = true
  try {
    const rule = await api.rules.create({
      name: name.value.trim(),
      recipient: recipient.value.trim(),
      type: type.value,
      enabled: enabled.value,
    })
    await invalidate([keys.rules])
    notify.success(`Rule ${rule.name} created`, 'Link sensors to it on its page.')
    await router.replace({ name: 'notification-rule', params: { id: rule.id } })
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { name: e.field('name'), recipient: e.field('recipient'), type: e.field('type') }
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
      <div class="field">
        <label for="rule-recipient">Recipient</label>
        <InputText id="rule-recipient" v-model="recipient" class="mono" :invalid="!!errors.recipient" />
        <small v-if="errors.recipient" class="field-error">{{ errors.recipient }}</small>
        <small v-else class="field-hint">{{ recipientHint }}</small>
      </div>
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

.mono {
  font-family: var(--pnmt-mono);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
