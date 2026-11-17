<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref, useId } from 'vue'
import type { S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import { formatAbsolute } from '@/format'
import { days, daysLeftText, fingerprint, keyUsageLabel, policyLabel, validationLevelLabel } from './certificate'

/**
 * What a certificate says (docs/ssl-certificate-plan.md §4.4, §8.3): the subject with its names as chips (the first 30,
 * the rest behind *+n more*, all of them found by the filter), the issuer with the validation level, the validity, the key
 * and signature, the fingerprints to copy, and the extensions, with every one as stored under *All extensions*. The
 * Certificate tab shows the current certificate with it, a version's drawer that version.
 */
const props = withDefaults(
  defineProps<{
    facts: S['CertificateFacts']
    now: number
    warnDays?: number | null
    checkExpiry?: boolean
    /** The chain's expiry, when it is the current certificate's: an intermediate that expires first is named. */
    validation?: S['CertificateValidation'] | null
  }>(),
  { warnDays: null, checkExpiry: true, validation: null },
)

// The tab and a version's drawer may show two at once.
const uid = useId()
const f = computed(() => props.facts)
const keyLabel = computed(() => {
  const k = f.value.key
  return k.label ?? (k.curve ? `${k.algorithm} ${k.curve}` : k.size ? `${k.algorithm} ${k.size}` : k.algorithm)
})
const parts = (n: S['CertificateName']) =>
  [
    ['Common name', n.commonName],
    ['Organisation', n.organization],
    ['Unit', n.organizationalUnit],
    ['Locality', n.locality],
    ['State', n.state],
    ['Country', n.country],
  ].filter((p): p is [string, string] => !!p[1])

// --- the names: 30 at first, all with +n more, each found by the filter ----------------------------------------------
const SHOWN = 30
const allNames = ref(false)
const find = ref('')
const matching = computed(() => {
  const q = find.value.trim().toLowerCase()
  return q ? f.value.names.filter((n) => n.toLowerCase().includes(q)) : f.value.names
})
const names = computed(() => (allNames.value || find.value.trim() ? matching.value : matching.value.slice(0, SHOWN)))
const more = computed(() => matching.value.length - names.value.length)

// --- the validity -----------------------------------------------------------------------------------------------------
const expiresAt = computed(() => props.validation?.chainExpiresAt ?? f.value.notAfter)
const warningFrom = computed(() =>
  props.warnDays && props.checkExpiry ? new Date(Date.parse(expiresAt.value) - props.warnDays * 86_400_000) : null,
)
const intermediateFirst = computed(() =>
  props.validation && !props.validation.chainExpiresIsLeaf ? props.validation : null,
)

const allExtensions = ref(false)
// Where the CA publishes: shown, never fetched (§5.1).
const urlRows = computed(() =>
  [
    { label: 'OCSP', urls: f.value.ocspUrls },
    { label: 'CA issuers', urls: f.value.caIssuerUrls },
    { label: 'CRL', urls: f.value.crlUrls },
  ].filter((r) => r.urls.length),
)
</script>

<template>
  <section class="panel" :aria-labelledby="`${uid}-subject`">
    <div :id="`${uid}-subject`" class="panel-header">Subject</div>
    <div class="panel-body">
      <dl class="facts">
        <template v-for="[label, value] in parts(f.subject)" :key="label">
          <dt>{{ label }}</dt>
          <dd>{{ value }}</dd>
        </template>
        <dt>Name</dt>
        <dd class="mono small dn">{{ f.subject.dn || '–' }}</dd>
      </dl>
      <div class="names-head">
        <span class="muted">Names ({{ f.names.length }})</span>
        <InputText
          v-if="f.names.length > SHOWN"
          v-model="find"
          size="small"
          placeholder="Find a name"
          aria-label="Find a name"
          class="find"
        />
      </div>
      <ul v-if="names.length" class="chips" aria-label="Names">
        <li v-for="n in names" :key="n" class="chip mono">{{ n }}</li>
        <li v-if="more > 0">
          <Button :label="`+${more} more`" text size="small" @click="allNames = true" />
        </li>
      </ul>
      <p v-else class="muted none">{{ find ? 'No name matches.' : 'No names (no subject alternative name).' }}</p>
    </div>
  </section>

  <section class="panel" :aria-labelledby="`${uid}-issuer`">
    <div :id="`${uid}-issuer`" class="panel-header">
      Issuer
      <Tag v-if="f.selfSigned" value="self-signed" severity="secondary" />
      <Tag
        v-else-if="f.validationLevel"
        v-tooltip.top="validationLevelLabel(f.validationLevel)"
        :value="f.validationLevel"
        severity="info"
      />
    </div>
    <div class="panel-body">
      <p v-if="f.selfSigned" class="muted none">Self-signed: it is its own issuer, and no CA vouches for it.</p>
      <dl class="facts">
        <template v-for="[label, value] in parts(f.issuer)" :key="label">
          <dt>{{ label }}</dt>
          <dd>{{ value }}</dd>
        </template>
        <dt>Name</dt>
        <dd class="mono small dn">{{ f.issuer.dn || '–' }}</dd>
        <template v-if="f.validationLevel">
          <dt>Validation</dt>
          <dd>{{ validationLevelLabel(f.validationLevel) }}</dd>
        </template>
      </dl>
    </div>
  </section>

  <section class="panel" :aria-labelledby="`${uid}-validity`">
    <div :id="`${uid}-validity`" class="panel-header">Validity</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>From</dt>
        <dd>{{ formatAbsolute(f.notBefore) }}</dd>
        <dt>Until</dt>
        <dd>{{ formatAbsolute(f.notAfter) }}</dd>
        <dt>Lifetime</dt>
        <dd>{{ days(f.lifetimeDays) }}</dd>
        <dt>Days left</dt>
        <dd>{{ daysLeftText(expiresAt, now) }}</dd>
        <template v-if="warningFrom">
          <dt>Warning from</dt>
          <dd>{{ formatAbsolute(warningFrom) }}</dd>
        </template>
      </dl>
      <Message v-if="intermediateFirst" severity="warn" :closable="false" size="small" class="note">
        The intermediate {{ intermediateFirst.chainExpiresSubject }} expires on
        {{ formatAbsolute(intermediateFirst.chainExpiresAt) }}, before the certificate.
      </Message>
    </div>
  </section>

  <section class="panel" :aria-labelledby="`${uid}-key`">
    <div :id="`${uid}-key`" class="panel-header">Key and signature</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Key</dt>
        <dd>{{ keyLabel }}</dd>
        <dt>Signature</dt>
        <dd>{{ f.signatureAlgorithm }}</dd>
        <dt>Serial</dt>
        <dd class="mono small dn">{{ f.serial }}</dd>
        <dt>Version</dt>
        <dd>X.509 v{{ f.version }}</dd>
      </dl>
    </div>
  </section>

  <section class="panel" :aria-labelledby="`${uid}-fingerprints`">
    <div :id="`${uid}-fingerprints`" class="panel-header">Fingerprints</div>
    <div class="panel-body fingerprints">
      <div class="field">
        <span class="muted small">SHA-256</span>
        <CopyField :value="fingerprint(f.sha256)" label="SHA-256" />
      </div>
      <div class="field">
        <span class="muted small">SHA-1</span>
        <CopyField :value="fingerprint(f.sha1)" label="SHA-1" />
      </div>
      <div class="field">
        <span class="muted small">SPKI SHA-256 (pin-sha256)</span>
        <CopyField :value="f.spkiSha256" label="SPKI SHA-256" />
      </div>
    </div>
  </section>

  <section class="panel" :aria-labelledby="`${uid}-extensions`">
    <div :id="`${uid}-extensions`" class="panel-header">Extensions</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Key usage</dt>
        <dd>{{ f.keyUsage.map(keyUsageLabel).join(', ') || '–' }}</dd>
        <dt>Extended key usage</dt>
        <dd>{{ f.extendedKeyUsage.join(', ') || '–' }}</dd>
        <dt>Basic constraints</dt>
        <dd>{{ f.isCa ? 'A CA' : 'Not a CA' }}</dd>
        <template v-if="f.subjectKeyId">
          <dt>Subject key id</dt>
          <dd class="mono small dn">{{ f.subjectKeyId }}</dd>
        </template>
        <template v-if="f.authorityKeyId">
          <dt>Authority key id</dt>
          <dd class="mono small dn">{{ f.authorityKeyId }}</dd>
        </template>
        <template v-for="row in urlRows" :key="row.label">
          <dt>{{ row.label }}</dt>
          <dd class="mono small">
            <div v-for="u in row.urls" :key="u" class="dn">{{ u }}</div>
          </dd>
        </template>
        <template v-if="f.policies.length">
          <dt>Policies</dt>
          <dd class="small">
            <div v-for="p in f.policies" :key="p">{{ policyLabel(p) }}</div>
          </dd>
        </template>
        <dt>Transparency</dt>
        <dd>{{ f.scts === null ? 'no SCTs' : `${f.scts} embedded SCT${f.scts === 1 ? '' : 's'}` }}</dd>
        <dt>OCSP must-staple</dt>
        <dd>{{ f.mustStaple ? 'Yes' : 'No' }}</dd>
      </dl>
      <Message v-if="f.truncated" severity="warn" :closable="false" size="small" class="note">
        Cut: the certificate holds more names, extensions or text than PNMT keeps from a server.
      </Message>
      <Button
        :label="allExtensions ? 'Hide all extensions' : `All extensions (${f.extensions.length})`"
        :icon="allExtensions ? 'pi pi-chevron-up' : 'pi pi-chevron-down'"
        text
        size="small"
        :aria-expanded="allExtensions"
        class="toggle"
        @click="allExtensions = !allExtensions"
      />
      <ul v-if="allExtensions" class="extensions" aria-label="All extensions">
        <li v-for="e in f.extensions" :key="e.oid">
          <div class="line">
            <strong>{{ e.name || e.oid }}</strong>
            <span class="muted mono small">{{ e.oid }}</span>
            <Tag v-if="e.critical" value="critical" severity="warn" />
          </div>
          <pre class="mono small value">{{ e.value }}</pre>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.small {
  font-size: 0.857rem;
}

.dn {
  overflow-wrap: anywhere;
}

.none {
  margin: 0 0 var(--pnmt-space-2);
}

.names-head {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  margin: var(--pnmt-space-3) 0 var(--pnmt-space-2);
}

.find {
  margin-left: auto;
  max-width: 14rem;
}

.chips {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.chip {
  padding: 0.1rem 0.5rem;
  border: 1px solid var(--pnmt-border);
  border-radius: 999px;
  background: var(--pnmt-bg);
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.fingerprints {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.note {
  margin-top: var(--pnmt-space-2);
}

.toggle {
  margin-top: var(--pnmt-space-2);
}

.extensions {
  list-style: none;
  margin: var(--pnmt-space-2) 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.value {
  margin: var(--pnmt-space-1) 0 0;
  padding: var(--pnmt-space-2);
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
</style>
