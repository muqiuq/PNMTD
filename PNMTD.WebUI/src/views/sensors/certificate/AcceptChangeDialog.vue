<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import CompareTable from './CompareTable.vue'
import { useCopy } from '@/composables/useCopy'
import { useNotify } from '@/composables/useNotify'
import { fieldKind, fingerprint, opensslCommand } from './certificate'

/**
 * *Accept the new certificate?* (docs/ssl-certificate-plan.md §8.4, R14), never a one-click ask(): the warning first and in
 * full, the accepted certificate against the new one with the changed rows marked and the kinds that alarm tagged, the new
 * SHA-256 with the command that shows it on the server, and a checkbox the person must tick, unticked whenever the window
 * opens. It sends the SHA-256 shown and confirmed: true; when the certificate changed again meanwhile (409
 * certificate_changed_again), or while the window was open, it shows the newer comparison, unticks the box and says why.
 */
const visible = defineModel<boolean>('visible', { required: true })
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const queryClient = useQueryClient()
const notify = useNotify()
const copy = useCopy()

const id = computed(() => props.sensor.id)
const detail = useQuery({
  queryKey: computed(() => keys.certificate(id.value)),
  queryFn: () => api.certificates.get(id.value),
  enabled: visible,
  staleTime: 0,
})
const d = computed(() => detail.data.value ?? null)
const summary = computed(() => d.value?.summary ?? props.sensor.certificate!)
const change = computed(() => (d.value?.current && d.value.change ? d.value.change : null))
const sha256 = computed(() => d.value?.current?.facts.sha256 ?? null)
const endpointName = computed(() => summary.value.serverName || summary.value.host)
const command = computed(() => opensslCommand(summary.value))
// Which kinds of change fail this sensor (§5.3): a new certificate includes a new key and a new issuer.
const alarms = computed(() => ({
  certificate: summary.value.alertOnCertificateChange,
  key: summary.value.alertOnCertificateChange || summary.value.alertOnKeyChange,
  issuer: summary.value.alertOnCertificateChange || summary.value.alertOnIssuerChange,
}))
const tagOf = (field: string) => {
  const kind = fieldKind(field)
  if (!kind || !change.value?.[kind] || !alarms.value[kind]) return null
  return kind === 'certificate' ? 'new certificate' : kind === 'key' ? 'new key' : 'new issuer'
}

const confirmed = ref(false)
const note = ref('')
const again = ref<string | null>(null)
const busy = ref(false)
watch(
  visible,
  (open) => {
    if (!open) return
    confirmed.value = false
    note.value = ''
    again.value = null
  },
  { immediate: true },
)
// A newer certificate under the open window: its comparison, and the box to tick again.
watch(sha256, (now, before) => {
  if (visible.value && before && now !== before) {
    confirmed.value = false
    again.value ??= 'The certificate changed again while this window was open: review the new comparison.'
  }
})

const noteError = computed(() => (note.value.length > 500 ? `At most 500 characters (${note.value.length}).` : null))
const canAccept = computed(() => confirmed.value && !!change.value && !!sha256.value && !noteError.value && !busy.value)

async function accept() {
  if (!canAccept.value || !sha256.value) return
  busy.value = true
  try {
    const updated = await api.certificates.accept(id.value, {
      sha256: sha256.value,
      confirmed: true,
      note: note.value.trim() || null,
    })
    queryClient.setQueryData(keys.certificate(id.value), updated)
    await Promise.all(
      [keys.sensor(id.value), keys.host(props.sensor.host.id), keys.hosts, keys.events, keys.dashboard].map((k) =>
        queryClient.invalidateQueries({ queryKey: [...k] }),
      ),
    )
    notify.success('New certificate accepted')
    visible.value = false
  } catch (e) {
    if (e instanceof ApiError && e.code === 'certificate_changed_again') {
      confirmed.value = false
      again.value = e.message
      await detail.refetch()
    } else if (e instanceof ApiError && e.code === 'no_change') {
      notify.info('Nothing to accept', e.message)
      visible.value = false
      await queryClient.invalidateQueries({ queryKey: [...keys.sensor(id.value)] })
    } else {
      notify.error(e, 'Not accepted')
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Accept the new certificate?"
    modal
    :style="{ width: '60rem' }"
    :breakpoints="{ '960px': '96vw' }"
    class="accept-change"
  >
    <div class="form">
      <Message severity="warn" icon="pi pi-exclamation-triangle" :closable="false">
        Only accept this if you know why the certificate changed: a renewal, a new server, a move to another CA. An
        unexpected change can mean that someone is intercepting the connection to <em>{{ endpointName }}</em>. After
        accepting, PNMT compares every check against the new certificate, and this sensor turns OK.
      </Message>
      <Message v-if="again" severity="error" :closable="false" size="small" role="alert">{{ again }}</Message>

      <div v-if="!d && detail.isFetching.value" class="center">
        <ProgressSpinner style="width: 32px; height: 32px" />
      </div>
      <Message v-else-if="detail.isError.value && !d" severity="error" :closable="false" size="small">
        The comparison cannot be loaded: {{ (detail.error.value as Error | null)?.message }}
      </Message>
      <Message v-else-if="d && !change" severity="info" :closable="false" size="small">
        Nothing to accept: the certificate the last check read is the accepted one.
      </Message>
      <template v-else-if="change">
        <section aria-labelledby="accept-what">
          <h3 id="accept-what" class="heading">
            What changed
            <Tag v-for="kind in change.kinds" :key="kind" :value="kind" severity="warn" />
          </h3>
          <CompareTable :fields="change.fields" :tag="tagOf" label="What changed" />
        </section>

        <section aria-labelledby="accept-how">
          <h3 id="accept-how" class="heading">How to check it on the server</h3>
          <p class="muted small">The new certificate's SHA-256:</p>
          <CopyField :value="fingerprint(sha256)" label="The new SHA-256" />
          <p class="muted small">The command that shows it, for this endpoint:</p>
          <div class="command">
            <pre class="mono" aria-label="Command">{{ command }}</pre>
            <Button
              v-tooltip.top="'Copy'"
              icon="pi pi-copy"
              size="small"
              text
              severity="secondary"
              aria-label="Copy the command"
              @click="copy(command, 'The command')"
            />
          </div>
        </section>

        <div class="confirm">
          <Checkbox v-model="confirmed" binary input-id="accept-confirmed" />
          <label for="accept-confirmed"
            >I have checked this change and confirm that the new certificate is expected.</label
          >
        </div>
        <div class="field">
          <label for="accept-note">A note for the audit log <span class="muted">(optional)</span></label>
          <Textarea
            id="accept-note"
            v-model="note"
            rows="2"
            maxlength="500"
            placeholder="why, or who confirmed it"
            :invalid="!!noteError"
          />
          <small v-if="noteError" class="field-error">{{ noteError }}</small>
        </div>
      </template>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text autofocus @click="visible = false" />
      <Button
        label="Accept the new certificate"
        severity="warn"
        icon="pi pi-check"
        :loading="busy"
        :disabled="!canAccept"
        @click="accept"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.heading {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  font-size: 1rem;
  margin: 0 0 var(--pnmt-space-2);
}

.command {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-1);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding-left: var(--pnmt-space-2);
}

.command pre {
  flex: 1;
  margin: var(--pnmt-space-2) 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.confirm {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
  font-weight: 500;
}

.small {
  font-size: 0.857rem;
  margin: 0 0 var(--pnmt-space-1);
}
</style>
