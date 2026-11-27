<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { licensesApi, type LicenseDetail } from '@/api/licenses'
import CopyField from '@/components/CopyField.vue'
import { useActions } from '@/composables/useActions'
import { maskedKey } from './item'
import { useItemSave } from './useItemSave'

/**
 * The keys (docs/licences-plan.md §3.4): masked with their last five characters, `•••••-CC9DR`; *Show* (Details, audited) reads
 * both and shows them to copy, until the page is left. Edit sets a key in a password-type field (a block of text in a visible one),
 * or clears it. A key that can't be read any more (409 key_lost) asks to be entered again.
 */
const props = defineProps<{ item: LicenseDetail; details: boolean; edit: boolean }>()
const { save } = useItemSave(() => props.item)
const { ask, notify } = useActions()

type Field = 'licenseKey' | 'productKey'
const rows: { field: Field; label: string }[] = [
  { field: 'licenseKey', label: 'Licence key' },
  { field: 'productKey', label: 'Product key' },
]
const has = (field: Field) => (field === 'licenseKey' ? props.item.hasLicenseKey : props.item.hasProductKey)
const hint = (field: Field) => (field === 'licenseKey' ? props.item.licenseKeyHint : props.item.productKeyHint)

// The keys shown live here only, never in the query cache: gone with the page.
const shown = ref<{ licenseKey?: string | null; productKey?: string | null } | null>(null)
const lost = ref(false)
const loading = ref(false)
// Another item, or a key changed: what was shown is forgotten.
watch(
  () =>
    `${props.item.id}|${props.item.licenseKeyHint}|${props.item.productKeyHint}|${has('licenseKey')}|${has('productKey')}`,
  () => {
    shown.value = null
    lost.value = false
  },
)

async function show() {
  loading.value = true
  try {
    shown.value = await licensesApi.keys(props.item.id)
  } catch (e) {
    if (e instanceof ApiError && e.code === 'key_lost') lost.value = true
    else notify.error(e, 'The keys weren’t shown')
  } finally {
    loading.value = false
  }
}

// --- setting and clearing -----------------------------------------------------------------------------------------------------
const editing = ref<Field | null>(null)
const draft = ref('')
const visible = ref(false)
const error = ref<string | null>(null)
const saving = ref(false)
function begin(field: Field) {
  editing.value = field
  draft.value = ''
  visible.value = false
  error.value = null
}
async function store(field: Field, value: string) {
  saving.value = true
  error.value = null
  try {
    await save({ [field]: value }, field)
    editing.value = null
    shown.value = null
    lost.value = false
    notify.success(value ? 'Key saved' : 'Key cleared')
  } catch (e) {
    if (e instanceof ApiError && e.field(field)) error.value = e.field(field)!
    else notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}
async function clear(field: Field, label: string) {
  if (
    !(await ask(
      `Clear the ${label.toLowerCase()}?`,
      `${props.item.display} keeps no ${label.toLowerCase()} after this.`,
      'Clear',
    ))
  )
    return
  await store(field, '')
}
</script>

<template>
  <section class="panel" aria-labelledby="keys-title">
    <div class="panel-header">
      <span id="keys-title">Keys</span>
      <span class="spacer" />
      <Button
        v-if="details && (item.hasLicenseKey || item.hasProductKey) && !shown"
        label="Show"
        icon="pi pi-eye"
        size="small"
        text
        :loading="loading"
        data-test="show-keys"
        @click="show"
      />
      <Button
        v-else-if="shown"
        label="Hide"
        icon="pi pi-eye-slash"
        size="small"
        text
        severity="secondary"
        @click="shown = null"
      />
    </div>
    <div class="panel-body">
      <Message v-if="lost" severity="warn" :closable="false" size="small" class="lost">
        This key can't be read: PNMT's key ring was replaced. Enter the key again.
      </Message>
      <dl class="facts">
        <template v-for="row in rows" :key="row.field">
          <dt>{{ row.label }}</dt>
          <dd>
            <form v-if="editing === row.field" class="key-edit" novalidate @submit.prevent="store(row.field, draft)">
              <InputText
                v-if="!visible"
                v-model="draft"
                type="password"
                autocomplete="off"
                size="small"
                class="grow"
                :invalid="!!error"
                :aria-label="row.label"
                autofocus
              />
              <Textarea
                v-else
                v-model="draft"
                rows="3"
                auto-resize
                class="grow mono"
                :invalid="!!error"
                :aria-label="row.label"
              />
              <Button
                v-tooltip.top="visible ? 'Hide what I type' : 'Show what I type (a key of several lines)'"
                :icon="visible ? 'pi pi-eye-slash' : 'pi pi-eye'"
                size="small"
                text
                severity="secondary"
                :aria-label="visible ? 'Hide what I type' : 'Show what I type'"
                @click="visible = !visible"
              />
              <Button
                type="submit"
                icon="pi pi-check"
                size="small"
                :loading="saving"
                :disabled="!draft.trim()"
                :aria-label="`Save ${row.label}`"
              />
              <Button
                icon="pi pi-times"
                size="small"
                severity="secondary"
                text
                :disabled="saving"
                :aria-label="`Discard ${row.label}`"
                @click="editing = null"
              />
            </form>
            <div v-else class="key-row">
              <CopyField v-if="shown?.[row.field]" :value="shown[row.field] ?? ''" :label="row.label" class="grow" />
              <span v-else-if="has(row.field)" class="mono" :data-test="`${row.field}-masked`">{{
                maskedKey(hint(row.field))
              }}</span>
              <span v-else class="muted">none</span>
              <template v-if="edit">
                <Button
                  :label="has(row.field) ? 'Change…' : 'Set…'"
                  size="small"
                  text
                  :data-test="`${row.field}-set`"
                  @click="begin(row.field)"
                />
                <Button
                  v-if="has(row.field)"
                  label="Clear"
                  size="small"
                  text
                  severity="danger"
                  :disabled="saving"
                  @click="clear(row.field, row.label)"
                />
              </template>
            </div>
            <small v-if="editing === row.field && error" class="field-error">{{ error }}</small>
          </dd>
        </template>
      </dl>
      <p class="muted hint">
        Encrypted at rest; shown only on request<template v-if="details">, and each time in the audit log</template>.
        <template v-if="!details">Showing them needs Licences &amp; services: Details.</template>
      </p>
    </div>
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.lost {
  margin-bottom: var(--pnmt-space-2);
}

.key-row,
.key-edit {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-width: 0;
}

.key-edit {
  align-items: flex-start;
}

.grow {
  flex: 1;
  min-width: 0;
}

.hint {
  margin: var(--pnmt-space-2) 0 0;
  font-size: 0.857rem;
}
</style>
