<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import type { S } from '@/api/resources'
import { numericParameter } from './scriptFormat'

/**
 * One parameter's input, as its type asks for it (docs/connectors-plan.md §13.5): a checkbox for a switch or a bool, a select for a
 * set, a number field with its range, a password field for a secret, a text field (or an area, for a multi-line text) otherwise. The
 * run dialog and a connector's fixed values use it alike.
 */
const value = defineModel<unknown>()
defineProps<{ parameter: S['ScriptParameter']; id: string; multiline?: boolean; disabled?: boolean }>()
</script>

<template>
  <Checkbox v-if="parameter.isSwitch" v-model="value" binary :input-id="id" :disabled="disabled" />
  <Select
    v-else-if="parameter.validateSet?.length"
    v-model="value"
    :input-id="id"
    :options="parameter.validateSet"
    :placeholder="parameter.default ?? ''"
    :disabled="disabled"
    show-clear
  />
  <Password
    v-else-if="parameter.isSecure"
    v-model="value as string"
    :input-id="id"
    :feedback="false"
    :disabled="disabled"
    toggle-mask
  />
  <InputNumber
    v-else-if="numericParameter(parameter)"
    v-model="value as number"
    :input-id="id"
    :min="parameter.rangeMin ?? undefined"
    :max="parameter.rangeMax ?? undefined"
    :max-fraction-digits="parameter.type === 'number' ? 6 : 0"
    :placeholder="parameter.default ?? ''"
    :disabled="disabled"
  />
  <Textarea
    v-else-if="multiline"
    :id="id"
    v-model="value as string"
    :placeholder="parameter.default ?? ''"
    :disabled="disabled"
    rows="3"
    auto-resize
  />
  <InputText v-else :id="id" v-model="value as string" :placeholder="parameter.default ?? ''" :disabled="disabled" />
</template>
