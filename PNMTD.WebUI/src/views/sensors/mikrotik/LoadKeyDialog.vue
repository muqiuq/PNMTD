<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { api, keys } from '@/api/resources'
import { cryptoAvailable, loadBackupKey, NO_CRYPTO } from '@/composables/useBackupKey'

/**
 * Load key… (docs/mikrotik-backup-plan.md §3.6): the backup key's file or the pasted key, into this tab's memory only.
 * It is never sent to PNMT and gone on reload, on sign-out, or after 15 minutes without use. `needs` names the key
 * a version was encrypted with, to say whether this one opens it.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ needs?: string | null }>()
const emit = defineEmits<{ loaded: [] }>()
const settings = useQuery({ queryKey: [...keys.mikrotik, 'backup-settings'], queryFn: api.mikrotik.backupSettings })
const text = ref('')
const error = ref<string | null>(null)
const busy = ref(false)
watch(visible, (open) => {
  if (open) {
    text.value = ''
    error.value = null
  }
})
const needed = computed(() => settings.data.value?.keys?.find((k) => k.fingerprint === props.needs) ?? null)

async function readFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]
  if (file) text.value = await file.text()
}

async function load() {
  busy.value = true
  error.value = null
  try {
    const recipient = await loadBackupKey(text.value)
    if (needed.value && needed.value.recipient !== recipient) {
      const loaded = settings.data.value?.keys?.find((k) => k.recipient === recipient)
      error.value = `This is ${loaded ? `the key ${loaded.fingerprint}` : 'another key'}; the version needs the key ${needed.value.fingerprint}.`
      return
    }
    visible.value = false
    emit('loaded')
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Load the backup key"
    modal
    :style="{ width: '36rem' }"
    :breakpoints="{ '600px': '95vw' }"
  >
    <p class="muted">
      The key stays in this tab's memory: it is never sent to PNMT, and it is gone when you reload, sign out, or after
      15 minutes without use.
      <template v-if="needs">
        This version was encrypted with the key <span class="mono">{{ needs }}</span>.</template
      >
    </p>
    <Message v-if="!cryptoAvailable()" severity="warn" :closable="false" size="small">{{ NO_CRYPTO }}</Message>
    <div class="field">
      <label for="key-file">Key file</label>
      <input id="key-file" type="file" accept=".age-key,.txt,text/plain" @change="readFile" />
    </div>
    <div class="field">
      <label for="key-text">Or paste the key</label>
      <Textarea id="key-text" v-model="text" rows="4" class="mono" placeholder="AGE-SECRET-KEY-1…" auto-resize />
    </div>
    <Message v-if="error" severity="error" :closable="false" size="small">{{ error }}</Message>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button label="Load" icon="pi pi-key" :loading="busy" :disabled="!text.trim()" @click="load" />
    </template>
  </Dialog>
</template>

<style scoped>
.field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  margin-bottom: var(--pnmt-space-3);
}
.mono {
  font-family: var(--pnmt-mono);
}
</style>
