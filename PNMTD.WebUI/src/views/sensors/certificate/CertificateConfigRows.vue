<script setup lang="ts">
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { certificateRequest, type S } from '@/api/resources'
import InlineField from '@/components/InlineField.vue'
import CheckEveryField from './CheckEveryField.vue'
import TrustField from './TrustField.vue'
import { intervalError, intervalLabel, ISSUER_MATCH_OPTIONS, TRUST_OPTIONS, type Trust } from './certificate'

/**
 * An SSL certificate sensor's rows in the configuration panel (docs/ssl-certificate-plan.md §8.3), each saved where it is
 * shown through sensorRequest(…, { certificate }): the address, the trust and its CAs, the server name, the address to
 * connect to, *Check every*, the change rules with §5.3's inclusion, the issuer's comparison, the warning and the checks.
 * Editable with Certificates: Edit and Monitoring: Edit; another trust mode takes its default rules (§5.4).
 */
const props = defineProps<{
  sensor: S['SensorDetail']
  readonly: boolean
  save: (patch: Partial<S['SensorRequest']>) => Promise<unknown>
}>()
const c = computed(() => props.sensor.certificate!)
const pinned = computed(() => c.value.trust === 'Pinned')
const rules = (patch: Partial<S['CertificateSensorRequest']>) =>
  props.save({ certificate: certificateRequest(c.value, patch) })

// --- the trust and its CAs, edited together ---------------------------------------------------------------------------
const trustDraft = ref<Trust>('Public')
const authorityDraft = ref<string[]>([])
const trustText = computed(() => {
  if (c.value.trust !== 'Private') return TRUST_OPTIONS.find((o) => o.value === c.value.trust)?.label ?? c.value.trust
  const names = c.value.authorities.map((a) => a.name)
  return `${names.length === 1 ? 'Private CA' : 'Private CAs'}: ${names.join(', ') || 'none'}`
})
function editTrust() {
  trustDraft.value = c.value.trust
  authorityDraft.value = c.value.authorities.map((a) => a.id)
}
async function saveTrust() {
  const other = trustDraft.value !== c.value.trust
  await rules({
    trust: trustDraft.value,
    authorityIds: authorityDraft.value,
    // Another mode: the rules it leaves out take its defaults on the server.
    ...(other
      ? {
          checkName: null,
          checkExpiry: null,
          incompleteChainFails: null,
          alertOnCertificateChange: null,
          alertOnKeyChange: null,
          alertOnIssuerChange: null,
        }
      : {}),
  })
}

// --- the interval -------------------------------------------------------------------------------------------------------
const intervalDraft = ref<number | undefined>(undefined)
async function saveInterval() {
  const error = intervalError(intervalDraft.value)
  if (error) throw new ApiError(400, { code: 'validation_failed', errors: { interval: [error] } })
  await props.save({ interval: intervalDraft.value })
}
</script>

<template>
  <dt>Address</dt>
  <dd>
    <InlineField
      :value="sensor.parameters"
      label="Address"
      field="parameters"
      mono
      :readonly="readonly"
      :save="(v) => save({ parameters: v ? String(v) : null })"
    />
    <small v-if="!readonly" class="field-hint">Another address starts over: the next check pins anew.</small>
  </dd>
  <dt>Trust</dt>
  <dd>
    <InlineField
      :value="trustText"
      type="custom"
      label="Trust"
      field="certificate.authorityIds"
      :readonly="readonly"
      :save="saveTrust"
      @edit="editTrust"
    >
      <template #editor>
        <TrustField v-model:trust="trustDraft" v-model:authority-ids="authorityDraft" />
      </template>
    </InlineField>
  </dd>
  <dt>Server name (SNI)</dt>
  <dd>
    <InlineField
      :value="c.serverName"
      label="Server name"
      field="certificate.serverName"
      mono
      :placeholder="`the host (${c.host})`"
      :readonly="readonly"
      :save="(v) => rules({ serverName: v ? String(v) : null })"
    />
  </dd>
  <dt>Connect to</dt>
  <dd>
    <InlineField
      :value="c.connectTo"
      label="Connect to"
      field="certificate.connectTo"
      mono
      placeholder="the host"
      :readonly="readonly"
      :save="(v) => rules({ connectTo: v ? String(v) : null })"
    />
  </dd>
  <dt>Check every</dt>
  <dd>
    <InlineField
      :value="intervalLabel(sensor.interval)"
      type="custom"
      label="Check every"
      field="interval"
      :readonly="readonly"
      :save="saveInterval"
      @edit="intervalDraft = sensor.interval || 86_400"
    >
      <template #editor>
        <CheckEveryField v-model="intervalDraft" input-id="cert-row-every" />
      </template>
    </InlineField>
  </dd>
  <dt>Fail on a new certificate</dt>
  <dd>
    <InlineField
      :value="c.alertOnCertificateChange"
      type="bool"
      label="Fail on a new certificate"
      field="certificate.alertOnCertificateChange"
      :readonly="readonly"
      :save="(v) => rules({ alertOnCertificateChange: Boolean(v) })"
    />
  </dd>
  <dt>Fail on a new key</dt>
  <dd>
    <InlineField
      :value="c.alertOnCertificateChange || c.alertOnKeyChange"
      type="bool"
      label="Fail on a new key"
      field="certificate.alertOnCertificateChange"
      :readonly="readonly || c.alertOnCertificateChange"
      :save="(v) => rules({ alertOnKeyChange: Boolean(v) })"
    />
    <small v-if="c.alertOnCertificateChange" class="field-hint">included in a new certificate</small>
  </dd>
  <template v-if="!pinned">
    <dt>Fail on a new issuer</dt>
    <dd>
      <InlineField
        :value="c.alertOnCertificateChange || c.alertOnIssuerChange"
        type="bool"
        label="Fail on a new issuer"
        :readonly="readonly || c.alertOnCertificateChange"
        :save="(v) => rules({ alertOnIssuerChange: Boolean(v) })"
      />
      <small v-if="c.alertOnCertificateChange" class="field-hint">included in a new certificate</small>
    </dd>
    <dt>Compare the issuer by</dt>
    <dd>
      <InlineField
        :value="c.issuerMatch"
        type="enum"
        :options="ISSUER_MATCH_OPTIONS"
        label="Compare the issuer by"
        field="certificate.issuerMatch"
        :readonly="readonly"
        :save="(v) => rules({ issuerMatch: v as S['CertificateIssuerMatch'] })"
      />
    </dd>
  </template>
  <dt>Warn before expiry</dt>
  <dd>
    <InlineField
      :value="c.warnDays"
      type="number"
      label="Warn before expiry"
      field="certificate.warnDays"
      :readonly="readonly"
      :save="(v) => rules({ warnDays: v === null ? null : Number(v) })"
    />
    <small class="field-hint">days<template v-if="!c.checkExpiry">, while expiry isn't checked: none</template></small>
  </dd>
  <dt>Check expiry</dt>
  <dd>
    <InlineField
      :value="c.checkExpiry"
      type="bool"
      label="Check expiry"
      :readonly="readonly"
      :save="(v) => rules({ checkExpiry: Boolean(v) })"
    />
  </dd>
  <dt>Check the name</dt>
  <dd>
    <InlineField
      :value="c.checkName"
      type="bool"
      label="Check the name"
      :readonly="readonly"
      :save="(v) => rules({ checkName: Boolean(v) })"
    />
  </dd>
  <template v-if="!pinned">
    <dt>An incomplete chain fails</dt>
    <dd>
      <InlineField
        :value="c.incompleteChainFails"
        type="bool"
        label="An incomplete chain fails"
        :readonly="readonly"
        :save="(v) => rules({ incompleteChainFails: Boolean(v) })"
      />
    </dd>
  </template>
</template>

<style scoped>
.field-hint {
  margin-left: var(--pnmt-space-2);
}
</style>
