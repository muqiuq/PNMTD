<script setup lang="ts">
import Button from 'primevue/button'
import Password from 'primevue/password'
import { computed, ref } from 'vue'
import { authApi } from '@/api/auth'
import { ApiError } from '@/api/client'
import { useNotify } from '@/composables/useNotify'

/**
 * The own password (§4.4): the current one and a new one of 10 to 256 characters, typed twice. The
 * server checks the same and stays the judge; its messages appear at the fields.
 */
const props = withDefaults(defineProps<{ submitLabel?: string; currentLabel?: string }>(), {
  submitLabel: 'Change password',
  currentLabel: 'Current password',
})
const emit = defineEmits<{ changed: [] }>()
const notify = useNotify()

const current = ref('')
const next = ref('')
const repeat = ref('')
const busy = ref(false)
const errors = ref<Record<string, string | undefined>>({})

const nextHint = computed(() =>
  next.value && next.value.length < 10 ? `${10 - next.value.length} more characters` : undefined,
)

function validate(): boolean {
  errors.value = {
    currentPassword: current.value ? undefined : 'Enter your current password.',
    newPassword:
      next.value.length < 10
        ? 'At least 10 characters.'
        : next.value.length > 256
          ? 'At most 256 characters.'
          : next.value === current.value
            ? 'Choose a password other than the current one.'
            : undefined,
    repeat: repeat.value === next.value ? undefined : 'The two passwords differ.',
  }
  return Object.values(errors.value).every((e) => !e)
}

async function submit() {
  if (!validate()) {
    return
  }
  busy.value = true
  try {
    await authApi.changePassword(current.value, next.value)
    current.value = next.value = repeat.value = ''
    errors.value = {}
    notify.success('Password changed', 'Your other sessions were signed out.')
    emit('changed')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { currentPassword: e.field('currentPassword'), newPassword: e.field('newPassword') }
    } else {
      notify.error(e, 'Password not changed')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form class="form" novalidate @submit.prevent="submit">
    <!-- For password managers: which account this is. -->
    <input type="text" name="username" autocomplete="username" hidden />
    <div class="field">
      <label for="current-password">{{ props.currentLabel }}</label>
      <Password
        v-model="current"
        input-id="current-password"
        :feedback="false"
        toggle-mask
        fluid
        :invalid="!!errors.currentPassword"
        :input-props="{ autocomplete: 'current-password' }"
      />
      <small v-if="errors.currentPassword" class="field-error">{{ errors.currentPassword }}</small>
    </div>
    <div class="field">
      <label for="new-password">New password</label>
      <Password
        v-model="next"
        input-id="new-password"
        :feedback="false"
        toggle-mask
        fluid
        :invalid="!!errors.newPassword"
        :input-props="{ autocomplete: 'new-password', 'aria-describedby': 'new-password-hint' }"
      />
      <small v-if="errors.newPassword" class="field-error">{{ errors.newPassword }}</small>
      <small v-else id="new-password-hint" class="field-hint">{{
        nextHint ?? '10 to 256 characters. A passphrase of a few words works well.'
      }}</small>
    </div>
    <div class="field">
      <label for="repeat-password">New password again</label>
      <Password
        v-model="repeat"
        input-id="repeat-password"
        :feedback="false"
        toggle-mask
        fluid
        :invalid="!!errors.repeat"
        :input-props="{ autocomplete: 'new-password' }"
      />
      <small v-if="errors.repeat" class="field-error">{{ errors.repeat }}</small>
    </div>
    <div class="form-actions">
      <Button type="submit" :label="props.submitLabel" :loading="busy" />
      <slot name="actions" />
    </div>
  </form>
</template>
