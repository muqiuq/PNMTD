<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { useActions } from '@/composables/useActions'
import { cryptoAvailable, NO_CRYPTO, newBackupKey, recipientOf } from '@/composables/useBackupKey'
import { saveFile } from '@/views/sensors/mikrotik/backupReading'

/**
 * Setting up, or replacing, the routers' backup key (docs/mikrotik-backup-plan.md §3.5), once. Either the browser
 * makes a key pair: the key file is downloaded, and loaded back before the key can be activated, so a key that was
 * never saved cannot be. Or an age public key made elsewhere (age-keygen) is pasted. PNMT only ever gets the public key.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ replacing: boolean }>()
const secure = cryptoAvailable()
const way = ref<'generate' | 'own'>(secure ? 'generate' : 'own')
const generated = ref<{ recipient: string; file: string } | null>(null)
const saved = ref(false)
const confirmed = ref(false)
const confirmError = ref<string | null>(null)
const own = ref('')
watch(visible, (open) => {
  if (open) {
    way.value = secure ? 'generate' : 'own'
    generated.value = null
    saved.value = false
    confirmed.value = false
    confirmError.value = null
    own.value = ''
  }
})
const recipient = computed(() =>
  way.value === 'generate' ? (confirmed.value ? (generated.value?.recipient ?? null) : null) : own.value.trim() || null,
)
const ownError = computed(() =>
  way.value === 'own' && own.value.trim() && !/^age1[0-9a-z]{58}$/.test(own.value.trim())
    ? 'An age public key: age1… with 58 more letters and digits.'
    : null,
)
const { run, busy } = useActions()

async function generate() {
  generated.value = await newBackupKey()
  saved.value = false
  confirmed.value = false
}

function download() {
  if (!generated.value) return
  const day = new Date().toISOString().slice(0, 10).replaceAll('-', '')
  saveFile(new Blob([generated.value.file], { type: 'text/plain' }), `pnmt-backups-${day}.age-key`)
  saved.value = true
}

async function confirm(text: string) {
  confirmError.value = null
  try {
    confirmed.value = (await recipientOf(text)) === generated.value?.recipient
    if (!confirmed.value) confirmError.value = 'That is another key: load the file just downloaded.'
  } catch (e) {
    confirmError.value = e instanceof Error ? e.message : String(e)
  }
}

async function readFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (file) await confirm(await file.text())
}

async function activate() {
  if (!recipient.value || ownError.value) return
  const done = await run(() => api.mikrotik.setBackupKey(recipient.value!), {
    success: props.replacing ? 'Backup key replaced' : 'Backup key set',
    invalidate: [keys.mikrotik],
  })
  if (done) visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="replacing ? 'Replace the backup key' : 'Set up the backup key'"
    modal
    :style="{ width: '44rem' }"
    :breakpoints="{ '760px': '95vw' }"
  >
    <Message severity="warn" :closable="false" size="small" class="block">
      PNMT only gets the public key: it can encrypt, never decrypt. Losing the private key loses every backup encrypted
      with it, and PNMT cannot help. Keep two copies, for example one in a password manager and one offline.
      <template v-if="replacing"> Backups made so far keep the key they were made with.</template>
    </Message>
    <SelectButton
      v-model="way"
      :options="[
        { label: 'Generate a key in this browser', value: 'generate' },
        { label: 'Use my own key', value: 'own' },
      ]"
      option-label="label"
      option-value="value"
      :allow-empty="false"
      aria-label="How"
      class="block"
    />
    <template v-if="way === 'generate'">
      <Message v-if="!secure" severity="warn" :closable="false" size="small">{{ NO_CRYPTO }}</Message>
      <ol v-else class="steps">
        <li>
          <Button
            :label="generated ? 'Generate another' : 'Generate a key'"
            icon="pi pi-key"
            size="small"
            severity="secondary"
            @click="generate"
          />
          <CopyField v-if="generated" :value="generated.recipient" label="Public key" class="mono" />
        </li>
        <li v-if="generated">
          <Button label="Download the key file" icon="pi pi-download" size="small" @click="download" />
          <span class="muted small"> Store it safely: PNMT does not keep it.</span>
        </li>
        <li v-if="generated && saved">
          <label for="confirm-file">Load the saved file, to show that it was kept</label>
          <input id="confirm-file" type="file" accept=".age-key,.txt,text/plain" @change="readFile" />
          <Message v-if="confirmed" severity="success" :closable="false" size="small">The key file matches.</Message>
          <Message v-else-if="confirmError" severity="error" :closable="false" size="small">{{ confirmError }}</Message>
        </li>
      </ol>
    </template>
    <div v-else class="field">
      <label for="own-key">The public key, from age-keygen</label>
      <Textarea id="own-key" v-model="own" rows="2" class="mono" placeholder="age1…" auto-resize />
      <small v-if="ownError" class="field-error">{{ ownError }}</small>
      <small v-else class="field-hint"
        >age-keygen -o pnmt-backups.age-key on your own machine prints it. The private key never touches a
        browser.</small
      >
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        :label="replacing ? 'Replace' : 'Activate'"
        icon="pi pi-check"
        :loading="busy"
        :disabled="!recipient || !!ownError"
        @click="activate"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.block {
  margin-bottom: var(--pnmt-space-3);
}
.steps {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  padding-left: 1.2rem;
}
.steps li {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  align-items: flex-start;
}
.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}
.mono {
  font-family: var(--pnmt-mono);
}
.small {
  font-size: 0.857rem;
}
</style>
