<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import { useNotify } from '@/composables/useNotify'
import { formatAbsolute } from '@/format'
import { fingerprint, pemOfFile, shortFingerprint } from '@/views/sensors/certificate/certificate'

/**
 * *Add a private CA…* (docs/ssl-certificate-plan.md §8.5): a name and the PEM, pasted or from a file (a binary .cer or .crt
 * is wrapped as PEM). As you type, PNMTD reads it (§4.5: one root, and intermediates under it) and shows each certificate
 * with its fingerprint, or why it is refused; Add saves. The sensor form's *Add a CA…* and the Certificate tab's *Trust this
 * root as a private CA…* open it in place, the latter with the root filled in.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ prefillPem?: string | null; prefillName?: string | null }>()
const emit = defineEmits<{ added: [item: S['CertificateAuthorityItem']] }>()
const queryClient = useQueryClient()
const notify = useNotify()

const name = ref('')
const pem = ref('')
const preview = ref<S['CertificateAuthorityPreview'] | null>(null)
const reading = ref(false)
const busy = ref(false)
const errors = ref<{ name?: string; pem?: string }>({})
// The name follows the root's until someone types one.
let suggested = ''

let timer: ReturnType<typeof setTimeout> | undefined
let sequence = 0
async function read(value: string) {
  const mine = ++sequence
  if (!value.trim()) {
    preview.value = null
    return
  }
  reading.value = true
  try {
    const result = await api.certificateAuthorities.preview({ name: name.value.trim() || null, pem: value })
    if (mine !== sequence) return
    preview.value = result
    const root = result.certificates.find((c) => c.root)
    const rootName = root?.subject.commonName ?? root?.subject.organization ?? ''
    if (rootName && (!name.value.trim() || name.value === suggested)) {
      name.value = rootName.slice(0, 100)
      suggested = name.value
    }
  } catch (e) {
    if (mine === sequence) {
      preview.value = null
      notify.error(e, 'Not read')
    }
  } finally {
    if (mine === sequence) reading.value = false
  }
}
watch(pem, (value) => {
  errors.value = { ...errors.value, pem: undefined }
  clearTimeout(timer)
  timer = setTimeout(() => void read(value), 400)
})
watch(
  visible,
  (open) => {
    if (!open) return
    suggested = ''
    name.value = props.prefillName ?? ''
    pem.value = props.prefillPem ?? ''
    preview.value = null
    errors.value = {}
    if (pem.value) void read(pem.value)
  },
  { immediate: true },
)

async function readFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (file) pem.value = pemOfFile(new Uint8Array(await file.arrayBuffer()))
}

const ready = computed(() => !!preview.value?.valid && !!name.value.trim() && !reading.value && !busy.value)

async function add() {
  errors.value = { name: name.value.trim() ? undefined : 'A name, 1 to 100 characters.' }
  if (errors.value.name || !preview.value?.valid) return
  busy.value = true
  try {
    const item = await api.certificateAuthorities.create({ name: name.value.trim(), pem: pem.value })
    await queryClient.invalidateQueries({ queryKey: [...keys.certificateAuthorities] })
    notify.success(`${item.name} added`, 'Sensors in the private mode can trust it now.')
    emit('added', item)
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = { name: e.field('name'), pem: e.field('pem') }
    } else if (e instanceof ApiError && e.code === 'authority_exists') {
      errors.value = { pem: e.message }
    } else {
      notify.error(e, 'Not added')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Add a private CA"
    modal
    :style="{ width: '48rem' }"
    :breakpoints="{ '800px': '96vw' }"
  >
    <form id="add-authority" class="form" novalidate @submit.prevent="add">
      <div class="field">
        <label for="ca-pem">The root certificate, and its intermediates if wanted (PEM)</label>
        <Textarea
          id="ca-pem"
          v-model="pem"
          rows="6"
          class="code"
          spellcheck="false"
          :invalid="!!errors.pem"
          placeholder="-----BEGIN CERTIFICATE-----"
        />
        <small v-if="errors.pem" class="field-error">{{ errors.pem }}</small>
      </div>
      <div class="field">
        <label for="ca-file">Or from a file</label>
        <input id="ca-file" type="file" accept=".pem,.crt,.cer,application/x-pem-file" @change="readFile" />
        <small class="field-hint">PEM or a binary DER certificate (.pem, .crt, .cer).</small>
      </div>

      <p v-if="reading" class="muted small">Reading…</p>
      <Message v-else-if="preview && !preview.valid" severity="error" :closable="false" size="small">
        {{ preview.error ?? 'Not a CA.' }}
      </Message>
      <Message v-if="preview?.privateKeyLeftOut" severity="warn" :closable="false" size="small">
        The PEM holds a private key: PNMT leaves it out and stores only the certificates. A key that was pasted anywhere
        should be replaced.
      </Message>
      <ul v-if="preview?.certificates.length" class="certificates" aria-label="What the PEM holds">
        <li v-for="c in preview.certificates" :key="c.sha256">
          <div class="line">
            <Tag :value="c.root ? 'root' : 'intermediate'" :severity="c.root ? 'info' : 'secondary'" />
            <strong>{{ c.subject.commonName ?? c.subject.organization ?? c.subject.dn }}</strong>
          </div>
          <div class="muted small">{{ c.subject.dn }}</div>
          <div class="muted small">
            Valid {{ formatAbsolute(c.notBefore) }} to {{ formatAbsolute(c.notAfter) }}
            <template v-if="!c.root"> · signed by {{ c.issuer.commonName ?? c.issuer.dn }}</template>
          </div>
          <div class="mono small" :title="fingerprint(c.sha256)">SHA-256 {{ shortFingerprint(c.sha256) }}</div>
        </li>
      </ul>

      <div class="field">
        <label for="ca-name">Name</label>
        <InputText id="ca-name" v-model="name" maxlength="100" :invalid="!!errors.name" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
        <small v-else class="field-hint">How sensors and their trust name it, for example Contoso Root CA 2024.</small>
      </div>
      <Message severity="warn" :closable="false" size="small" icon="pi pi-exclamation-triangle">
        Every sensor that trusts this CA accepts any certificate it signs.
      </Message>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button type="submit" form="add-authority" label="Add" icon="pi pi-plus" :loading="busy" :disabled="!ready" />
    </template>
  </Dialog>
</template>

<style scoped>
.code {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  width: 100%;
}

.certificates {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.certificates li {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
