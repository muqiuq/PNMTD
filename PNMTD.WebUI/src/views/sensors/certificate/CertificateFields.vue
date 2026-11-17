<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import CheckEveryField from './CheckEveryField.vue'
import TrustField from './TrustField.vue'
import {
  ADDRESS_HINT,
  ISSUER_MATCH_OPTIONS,
  parseAddress,
  trustHint,
  withTrust,
  type CertificateDraft,
  type Trust,
} from './certificate'

/**
 * The SSL certificate section of *Add sensor* (docs/ssl-certificate-plan.md §8.2): the address, the trust (choosing a mode
 * sets §5.4's defaults), *Check every* in place of the interval in seconds, when it fails (the three change rules with
 * §5.3's inclusion shown, the warning and *Check expiry*, the name, an incomplete chain), and, collapsed, the issuer's
 * comparison, the server name and the address to connect to.
 */
const draft = defineModel<CertificateDraft>({ required: true })
const address = defineModel<string>('address', { required: true })
const interval = defineModel<number | undefined>('interval', { required: true })
defineProps<{ errors: Record<string, string | undefined> }>()

const set = (patch: Partial<CertificateDraft>) => (draft.value = { ...draft.value, ...patch })
const setTrust = (trust: Trust) => (draft.value = withTrust(draft.value, trust))
const pinned = computed(() => draft.value.trust === 'Pinned')
const parsed = computed(() => (address.value.trim() ? parseAddress(address.value) : null))
const endpoint = computed(() => (parsed.value && !('error' in parsed.value) ? parsed.value : null))
const reads = computed(() => endpoint.value?.normalized ?? null)
const advanced = ref(false)
const issuerHint = computed(() => ISSUER_MATCH_OPTIONS.find((o) => o.value === draft.value.issuerMatch)?.hint ?? '')
</script>

<template>
  <fieldset class="certificate-fields wide">
    <legend>SSL certificate</legend>
    <div class="grid">
      <div class="field wide">
        <label for="cert-address">Address</label>
        <InputText
          id="cert-address"
          v-model="address"
          class="mono"
          placeholder="shop.example.com"
          spellcheck="false"
          :invalid="!!errors.parameters"
        />
        <small v-if="errors.parameters" class="field-error">{{ errors.parameters }}</small>
        <small v-else-if="parsed && 'error' in parsed" class="field-hint">{{ parsed.error }}</small>
        <small v-else-if="reads" class="field-hint"
          >PNMTD checks the certificate of <span class="mono">{{ reads }}</span>.</small
        >
        <small v-else class="field-hint">{{ ADDRESS_HINT }}</small>
      </div>

      <div class="field wide">
        <label for="cert-trust">Trust</label>
        <TrustField
          :trust="draft.trust"
          :authority-ids="draft.authorityIds"
          input-id="cert-trust"
          :invalid-trust="!!errors['certificate.trust']"
          :invalid-authorities="!!errors['certificate.authorityIds']"
          @update:trust="setTrust"
          @update:authority-ids="(ids: string[]) => set({ authorityIds: ids })"
        />
        <small v-if="errors['certificate.trust']" class="field-error">{{ errors['certificate.trust'] }}</small>
        <small v-if="errors['certificate.authorityIds']" class="field-error">{{
          errors['certificate.authorityIds']
        }}</small>
        <small class="field-hint">{{ trustHint(draft.trust) }}</small>
      </div>

      <div class="field">
        <label for="cert-every">Check every</label>
        <CheckEveryField v-model="interval" input-id="cert-every" :invalid="!!errors.interval" />
        <small v-if="errors.interval" class="field-error">{{ errors.interval }}</small>
        <small v-else class="field-hint"
          >How often PNMTD reads the certificate. Expiry is evaluated every minute, a failing sensor checked
          hourly.</small
        >
      </div>
      <div class="field">
        <label for="cert-warn">Warn before expiry (days)</label>
        <InputNumber
          :model-value="draft.warnDays"
          input-id="cert-warn"
          :min="1"
          :max="365"
          :use-grouping="false"
          :disabled="!draft.checkExpiry"
          placeholder="The installation's default"
          :invalid="!!errors['certificate.warnDays']"
          @update:model-value="(v: number | null) => set({ warnDays: v })"
        />
        <small v-if="errors['certificate.warnDays']" class="field-error">{{ errors['certificate.warnDays'] }}</small>
        <small v-else class="field-hint"
          >1 to 365; empty: the installation's default (Certificates__WarnDays, 14 unless set otherwise).</small
        >
      </div>

      <div class="subhead wide">When it fails</div>
      <div class="switch-row wide">
        <ToggleSwitch
          :model-value="draft.checkExpiry"
          input-id="cert-expiry"
          @update:model-value="(v: boolean) => set({ checkExpiry: v })"
        />
        <label for="cert-expiry">Check expiry</label>
      </div>
      <small class="field-hint wide hint"
        >About to expire (485) within the warning, expired (486), and not valid yet (488). Off for a device whose old
        certificate nobody will renew; its change is still watched.</small
      >
      <div class="switch-row wide">
        <ToggleSwitch
          :model-value="draft.alertOnCertificateChange"
          input-id="cert-on-certificate"
          @update:model-value="(v: boolean) => set({ alertOnCertificateChange: v })"
        />
        <label for="cert-on-certificate">Fail on a new certificate</label>
      </div>
      <small class="field-hint wide hint"
        >Any new certificate, a renewal too, fails it (487) until it is accepted. It includes a new key and a new
        issuer: those always come with a new certificate.</small
      >
      <div class="switch-row wide">
        <ToggleSwitch
          :model-value="draft.alertOnCertificateChange || draft.alertOnKeyChange"
          input-id="cert-on-key"
          :disabled="draft.alertOnCertificateChange"
          @update:model-value="(v: boolean) => set({ alertOnKeyChange: v })"
        />
        <label for="cert-on-key"
          >Fail on a new key<span v-if="draft.alertOnCertificateChange" class="muted">
            (included in a new certificate)</span
          ></label
        >
      </div>
      <small class="field-hint wide hint">A renewal that keeps its key passes; a new key fails it.</small>
      <template v-if="!pinned">
        <div class="switch-row wide">
          <ToggleSwitch
            :model-value="draft.alertOnCertificateChange || draft.alertOnIssuerChange"
            input-id="cert-on-issuer"
            :disabled="draft.alertOnCertificateChange"
            @update:model-value="(v: boolean) => set({ alertOnIssuerChange: v })"
          />
          <label for="cert-on-issuer"
            >Fail on a new issuer<span v-if="draft.alertOnCertificateChange" class="muted">
              (included in a new certificate)</span
            ></label
          >
        </div>
        <small class="field-hint wide hint"
          >A move to another CA, Let's Encrypt → ZeroSSL. Key and issuer together let a routine renewal pass and
          anything else fail.</small
        >
      </template>
      <small v-if="errors['certificate.alertOnCertificateChange']" class="field-error wide">{{
        errors['certificate.alertOnCertificateChange']
      }}</small>
      <div class="switch-row wide">
        <ToggleSwitch
          :model-value="draft.checkName"
          input-id="cert-name"
          @update:model-value="(v: boolean) => set({ checkName: v })"
        />
        <label for="cert-name">Check the name</label>
      </div>
      <small class="field-hint wide hint"
        >The certificate must name the server name (488 otherwise).<template v-if="pinned">
          Devices often carry localhost, a serial number or an old address: the pin is what counts.</template
        ></small
      >
      <template v-if="!pinned">
        <div class="switch-row wide">
          <ToggleSwitch
            :model-value="draft.incompleteChainFails"
            input-id="cert-chain"
            @update:model-value="(v: boolean) => set({ incompleteChainFails: v })"
          />
          <label for="cert-chain">An incomplete chain fails</label>
        </div>
        <small class="field-hint wide hint"
          >A server that doesn't send its intermediate: browsers fetch it themselves, curl, Java, Python and Firefox
          without a cached copy fail. Off, it is only a finding.</small
        >
      </template>

      <div class="wide">
        <Button
          :label="advanced ? 'Hide the advanced settings' : 'Advanced…'"
          :icon="advanced ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
          text
          size="small"
          :aria-expanded="advanced"
          aria-controls="cert-advanced"
          @click="advanced = !advanced"
        />
      </div>
      <div v-show="advanced" id="cert-advanced" class="grid wide">
        <div v-if="!pinned" class="field wide">
          <label id="cert-issuer-match">Compare the issuer by</label>
          <SelectButton
            :model-value="draft.issuerMatch"
            :options="ISSUER_MATCH_OPTIONS"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            aria-labelledby="cert-issuer-match"
            @update:model-value="(v: CertificateDraft['issuerMatch']) => set({ issuerMatch: v })"
          />
          <small class="field-hint">{{ issuerHint }}</small>
        </div>
        <div class="field">
          <label for="cert-sni">Server name (SNI) <span class="muted">(optional)</span></label>
          <InputText
            id="cert-sni"
            :model-value="draft.serverName"
            class="mono"
            spellcheck="false"
            :placeholder="endpoint?.host ?? 'the host'"
            :invalid="!!errors['certificate.serverName']"
            @update:model-value="(v?: string) => set({ serverName: v ?? '' })"
          />
          <small v-if="errors['certificate.serverName']" class="field-error">{{
            errors['certificate.serverName']
          }}</small>
          <small v-else class="field-hint">Sent in the handshake, and the name the certificate must match.</small>
        </div>
        <div class="field">
          <label for="cert-connect">Connect to <span class="muted">(optional)</span></label>
          <InputText
            id="cert-connect"
            :model-value="draft.connectTo"
            class="mono"
            spellcheck="false"
            placeholder="10.0.0.7"
            :invalid="!!errors['certificate.connectTo']"
            @update:model-value="(v?: string) => set({ connectTo: v ?? '' })"
          />
          <small v-if="errors['certificate.connectTo']" class="field-error">{{
            errors['certificate.connectTo']
          }}</small>
          <small v-else class="field-hint"
            >An address or name to connect to instead of the host: one backend behind a load balancer, split DNS.</small
          >
        </div>
      </div>
    </div>
  </fieldset>
</template>

<style scoped>
.certificate-fields {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pnmt-space-3);
  margin: 0;
  min-width: 0;
}

legend {
  font-weight: 600;
  padding: 0 var(--pnmt-space-1);
}

.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.grid .wide {
  grid-column: 1 / -1;
}

.subhead {
  font-weight: 600;
  margin-top: var(--pnmt-space-1);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-height: 32px;
}

.hint {
  margin-top: calc(-1 * var(--pnmt-space-2));
}

.mono :deep(input),
input.mono {
  font-family: var(--pnmt-mono);
}

@media (max-width: 599px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
