<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, certificateRequest, keys, sensorRequest, type S } from '@/api/resources'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useCopy } from '@/composables/useCopy'
import { useNow } from '@/composables/useNow'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute, formatDay } from '@/format'
import AddAuthorityDialog from '@/views/admin/certificates/AddAuthorityDialog.vue'
import CertificateChain from './CertificateChain.vue'
import CertificateFacts from './CertificateFacts.vue'
import ValidityBar from './ValidityBar.vue'
import VersionDrawer from './VersionDrawer.vue'
import {
  acceptedText,
  pemOf,
  shortFingerprint,
  validityHeadline,
  warningLengthNote,
  type Tone,
  type Trust,
} from './certificate'

/**
 * An SSL certificate sensor's *Certificate* tab (docs/ssl-certificate-plan.md §8.3), for Certificates: View: the headline
 * (the days left, a validity bar with today and the warning marked, the trust, who accepted it, §5.4's warnings about the
 * warning's length), everything the certificate says, the chain with its hints and fixes, the connection, the validation,
 * the PEM, and the certificates seen with a drawer each. After a failed check the last certificate seen stays shown.
 */
const props = defineProps<{ sensor: S['SensorDetail']; canEdit: boolean; canManage: boolean }>()
const queryClient = useQueryClient()
const { run, ask } = useActions()
const copy = useCopy()
const now = useNow()

const id = props.sensor.id
const detail = useQuery({
  queryKey: keys.certificate(id),
  queryFn: () => api.certificates.get(id),
  refetchInterval: 30_000,
})
const d = computed(() => detail.data.value ?? null)
const s = computed(() => d.value?.summary ?? props.sensor.certificate!)
const current = computed(() => d.value?.current ?? null)
const v = computed(() => d.value?.validation ?? null)
const pinned = computed(() => s.value.trust === 'Pinned')
const expiresAt = computed(() => v.value?.chainExpiresAt ?? current.value?.facts.notAfter ?? null)
const headline = computed(() => validityHeadline(expiresAt.value, s.value.warnDays, now.value))
const lengthNote = computed(() =>
  s.value.checkExpiry ? warningLengthNote(s.value.warnDays, current.value?.facts.lifetimeDays) : null,
)

// --- the trust and the baseline --------------------------------------------------------------------------------------
const trust = computed<{ text: string; tone: Tone } | null>(() => {
  if (!v.value) return null
  if (pinned.value) {
    const by = s.value.acceptedBy
    const since = s.value.acceptedAt ? formatDay(new Date(s.value.acceptedAt)) : null
    const who = !by
      ? ''
      : by === 'first check'
        ? ', at the first check'
        : by.startsWith('automatically')
          ? `, ${by}`
          : ` by ${by}`
    const also = v.value.publiclyTrusted ? '; also trusted by public CAs' : v.value.selfSigned ? '; self-signed' : ''
    return { text: `Pinned${since ? ` since ${since}` : ''}${who}${also}`, tone: 'ok' }
  }
  if (v.value.trusted) {
    return {
      text:
        s.value.trust === 'Private'
          ? `Trusted by the private CA ${v.value.trustedBy ?? ''}`.trim()
          : `Trusted by public CAs${v.value.trustedBy ? ` (${v.value.trustedBy})` : ''}`,
      tone: 'ok',
    }
  }
  return { text: `Not trusted: ${v.value.problems.join('; ') || "the chain doesn't verify"}`, tone: 'fail' }
})
const accepted = computed(() =>
  !pinned.value && s.value.acceptedAt
    ? `${acceptedText(s.value.acceptedBy, false)}, ${formatAbsolute(s.value.acceptedAt)}`
    : null,
)

// --- the validation, each check with whether the sensor fails on it --------------------------------------------------
const checks = computed(() => {
  const x = v.value
  const c = current.value
  if (!x || !c) return []
  const notBefore = Date.parse(c.facts.notBefore)
  const until = Date.parse(x.chainExpiresAt)
  return [
    {
      label: pinned.value
        ? x.publiclyTrusted
          ? 'Also trusted by public CAs'
          : x.selfSigned
            ? 'Self-signed: no CA vouches for it'
            : 'Not trusted by public CAs'
        : x.trusted
          ? `Trusted by ${x.trustedBy ?? 'a CA'}`
          : 'Not trusted',
      ok: pinned.value ? null : x.trusted,
      rule: pinned.value ? 'the pin is what counts' : 'fails the sensor',
    },
    {
      label: x.nameMatches ? 'The name matches' : `The name doesn't match ${s.value.serverName || s.value.host}`,
      ok: x.nameMatches,
      rule: s.value.checkName ? 'fails the sensor' : 'not checked by this sensor',
    },
    {
      label: x.complete ? 'The chain is complete' : "The chain is incomplete: the server doesn't send its intermediate",
      ok: x.complete,
      rule: pinned.value
        ? 'not checked when pinned'
        : s.value.incompleteChainFails
          ? 'fails the sensor'
          : 'a finding only',
    },
    {
      label:
        now.value < notBefore
          ? `Not valid before ${formatAbsolute(c.facts.notBefore)}`
          : now.value >= until
            ? `Expired on ${formatAbsolute(x.chainExpiresAt)}`
            : 'Within its validity',
      ok: now.value >= notBefore && now.value < until,
      rule: s.value.checkExpiry ? 'fails the sensor' : 'expiry not checked',
    },
  ]
})
const tickClass = (ok: boolean | null) =>
  ok === null ? 'pi pi-minus-circle muted' : ok ? 'pi pi-check-circle tone-ok' : 'pi pi-times-circle tone-fail'
const answered = computed(() => {
  const c = d.value?.connection
  if (!c) return null
  return `${c.address.includes(':') ? `[${c.address}]` : c.address}:${c.port}`
})

// --- PEM ---------------------------------------------------------------------------------------------------------------
async function copyPem() {
  if (!current.value) return
  const versionId = current.value.id
  const pem = await run(() => api.certificates.pem(id, versionId), { failure: 'Not read' })
  if (pem) await copy(pem, 'The certificate (PEM)')
}

// --- the fixes: trust the server's root, pin a self-signed certificate ----------------------------------------------
async function saveTrust(mode: Trust, authorityIds: string[], success: string) {
  const sensor = props.sensor
  const updated = await run(
    () =>
      api.sensors.update(
        sensor.id,
        sensorRequest(sensor, {
          certificate: certificateRequest(sensor.certificate!, {
            trust: mode,
            authorityIds,
            checkName: null,
            checkExpiry: null,
            incompleteChainFails: null,
            alertOnCertificateChange: null,
            alertOnKeyChange: null,
            alertOnIssuerChange: null,
          }),
        }),
      ),
    { success, failure: 'Not changed', invalidate: [keys.host(sensor.host.id), keys.hosts, keys.certificate(id)] },
  )
  if (updated) queryClient.setQueryData(keys.sensor(id), updated)
}

async function pin() {
  if (
    await ask(
      'Switch to pinned?',
      "The sensor then trusts no CA, only the certificate it reads at the next check: a new certificate fails it until it is accepted. Compare the SHA-256 once with the device's own display.",
      'Switch to pinned',
    )
  ) {
    await saveTrust('Pinned', [], 'Pinned: the next check pins the certificate')
  }
}

const addingAuthority = ref(false)
const authorityPem = ref<string | null>(null)
const authorityName = ref<string | null>(null)
async function trustRoot(entry: S['CertificateChainEntry']) {
  if (!current.value) return
  const versionId = current.value.id
  const pem = await run(
    async () => {
      const one = await pemOf(await api.certificates.pem(id, versionId, true), entry.sha256)
      if (!one) throw new Error("The chain the server sent doesn't hold this root.")
      return one
    },
    { failure: 'Not read' },
  )
  if (!pem) return
  authorityPem.value = pem
  authorityName.value = entry.subject.commonName ?? entry.subject.organization ?? null
  addingAuthority.value = true
}
async function authorityAdded(item: S['CertificateAuthorityItem']) {
  if (
    props.canEdit &&
    (await ask(
      `Trust ${item.name} for this sensor?`,
      'The sensor switches to the private mode with this CA, takes its default rules, and is checked again.',
      'Trust it',
    ))
  ) {
    await saveTrust('Private', [item.id], `${item.name} is the sensor's trust now`)
  }
}

// --- the certificates seen -----------------------------------------------------------------------------------------------
const versions = useServerTable<S['CertificateVersionItem']>({
  key: [...keys.certificate(id), 'versions'],
  fetch: (q) => api.certificates.versions(id, q),
  pageSize: 10,
})
const openVersion = ref<string | null>(null)
</script>

<template>
  <div class="certificate-tab">
    <ErrorState
      v-if="detail.isError.value && !d"
      :error="detail.error.value"
      what="the certificate"
      @retry="detail.refetch()"
    />
    <div v-else-if="!d" class="center"><ProgressSpinner style="width: 40px; height: 40px" /></div>
    <template v-else>
      <Message v-if="s.consecutiveFailures > 0 && s.lastError" severity="error" :closable="false">
        The last check failed: {{ s.lastError
        }}<template v-if="s.firstFailureAt"> · since <RelativeTime :value="s.firstFailureAt" /></template>.<template
          v-if="current"
        >
          The certificate below is the last one seen<template v-if="s.lastSuccessAt"
            >, <RelativeTime :value="s.lastSuccessAt" /></template
          >.</template
        >
      </Message>
      <EmptyState
        v-if="!current"
        icon="pi pi-lock"
        title="No certificate read yet."
        :text="
          s.lastCheckAt
            ? 'No check has read one so far.'
            : 'The first check runs within 15 seconds of the sensor being created.'
        "
      />
      <template v-else>
        <section class="panel headline" aria-label="Validity">
          <div class="panel-body">
            <div class="title" :class="`tone-${headline.tone}`">{{ headline.text }}</div>
            <ValidityBar
              v-if="expiresAt"
              :not-before="current.facts.notBefore"
              :expires-at="expiresAt"
              :warn-days="s.warnDays"
              :check-expiry="s.checkExpiry"
              :now="now"
            />
            <p v-if="trust" class="line" :class="`tone-${trust.tone}`">
              <i :class="trust.tone === 'ok' ? 'pi pi-verified' : 'pi pi-times-circle'" aria-hidden="true" />
              {{ trust.text }}
            </p>
            <p v-if="accepted || d.acceptedNote" class="line muted">
              {{ accepted }}<template v-if="d.acceptedNote">{{ accepted ? ' · ' : '' }}“{{ d.acceptedNote }}”</template>
            </p>
            <Message v-if="lengthNote" severity="warn" :closable="false" size="small">{{ lengthNote }}</Message>
          </div>
        </section>

        <div class="two-columns">
          <div class="column">
            <CertificateFacts
              :facts="current.facts"
              :now="now"
              :warn-days="s.warnDays"
              :check-expiry="s.checkExpiry"
              :validation="v"
            />
          </div>
          <div class="column">
            <CertificateChain
              :chain="current.chain"
              :validation="v"
              :trust="s.trust"
              :self-signed="current.facts.selfSigned"
              :can-manage="canManage"
              :can-edit="canEdit"
              @trust-root="trustRoot"
              @pin="pin"
            />

            <section v-if="d.connection" class="panel" aria-labelledby="ct-connection">
              <div id="ct-connection" class="panel-header">Connection</div>
              <dl class="panel-body facts">
                <dt>Answered</dt>
                <dd class="mono">{{ answered }}</dd>
                <dt>Server name sent</dt>
                <dd>{{ d.connection.serverName ?? 'none (an IP address)' }}</dd>
                <dt>Protocol</dt>
                <dd>{{ d.connection.protocol }}</dd>
                <dt>Cipher suite</dt>
                <dd class="mono small dn">{{ d.connection.cipherSuite }}</dd>
                <dt>Handshake</dt>
                <dd>{{ d.connection.handshakeMs }} ms</dd>
                <dt>Read</dt>
                <dd><RelativeTime :value="s.lastSuccessAt" /></dd>
              </dl>
            </section>

            <section v-if="v" class="panel" aria-labelledby="ct-validation">
              <div id="ct-validation" class="panel-header">Validation</div>
              <div class="panel-body">
                <ul class="checks">
                  <li v-for="c in checks" :key="c.label">
                    <i :class="tickClass(c.ok)" aria-hidden="true" />
                    <span>{{ c.label }}</span>
                    <span class="muted small">· {{ c.rule }}</span>
                  </li>
                </ul>
                <ul v-if="v.problems.length" class="problems" aria-label="Problems">
                  <li v-for="p in v.problems" :key="p" class="small">{{ p }}</li>
                </ul>
              </div>
            </section>

            <section class="panel" aria-labelledby="ct-download">
              <div id="ct-download" class="panel-header">Download</div>
              <div class="panel-body actions">
                <Button
                  as="a"
                  :href="api.certificates.pemUrl(id, current.id)"
                  label="Certificate (PEM)"
                  icon="pi pi-download"
                  size="small"
                  outlined
                />
                <Button
                  as="a"
                  :href="api.certificates.pemUrl(id, current.id, true)"
                  label="Chain (PEM)"
                  icon="pi pi-download"
                  size="small"
                  outlined
                />
                <Button label="Copy PEM" icon="pi pi-copy" size="small" text @click="copyPem" />
              </div>
            </section>

            <section class="seen" aria-labelledby="ct-seen">
              <h2 id="ct-seen" class="seen-title">
                Certificates seen <span class="muted count">{{ d.versionCount }}</span>
              </h2>
              <ListTable :table="versions" what="certificates" selectable @select="(r) => (openVersion = r.id)">
                <Column header="First seen" style="width: 7rem">
                  <template #body="{ data }"><RelativeTime :value="data.firstSeenAt" /></template>
                </Column>
                <Column header="Last seen" style="width: 7rem">
                  <template #body="{ data }"><RelativeTime :value="data.lastSeenAt" /></template>
                </Column>
                <Column header="Certificate">
                  <template #body="{ data }">
                    <div class="ellipsis" :title="data.issuer">{{ data.issuer }}</div>
                    <div class="muted small mono ellipsis" :title="data.serial">
                      {{ shortFingerprint(data.sha256) }}
                    </div>
                  </template>
                </Column>
                <Column header="Valid until" style="width: 8rem">
                  <template #body="{ data }">{{ formatDay(new Date(data.notAfter)) }}</template>
                </Column>
                <Column header="" style="width: 9rem">
                  <template #body="{ data }">
                    <div class="tags">
                      <Tag v-if="data.current" value="current" severity="info" />
                      <Tag v-if="data.accepted" value="accepted" severity="success" />
                      <Tag v-if="data.current && !data.accepted" value="changed" severity="warn" />
                    </div>
                  </template>
                </Column>
                <template #mobile="{ item }">
                  <div class="mobile-row">
                    <span class="ellipsis">{{ item.issuer }}</span>
                    <span class="muted small">until {{ formatDay(new Date(item.notAfter)) }}</span>
                    <Tag v-if="item.current" value="current" severity="info" />
                    <Tag v-if="item.accepted" value="accepted" severity="success" />
                  </div>
                </template>
              </ListTable>
            </section>
          </div>
        </div>
      </template>
    </template>

    <VersionDrawer
      :sensor-id="id"
      :version-id="openVersion"
      :current="current"
      :trust="s.trust"
      @close="openVersion = null"
    />
    <AddAuthorityDialog
      v-if="canManage"
      v-model:visible="addingAuthority"
      :prefill-pem="authorityPem"
      :prefill-name="authorityName"
      @added="authorityAdded"
    />
  </div>
</template>

<style scoped>
.certificate-tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

.headline .panel-body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.title {
  font-size: 1.286rem;
  font-weight: 600;
}

.line {
  margin: 0;
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-fail {
  color: var(--pnmt-fail);
}

.tone-none {
  color: var(--pnmt-muted);
}

.checks,
.problems {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.checks li {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.problems {
  margin-top: var(--pnmt-space-2);
  padding-left: var(--pnmt-space-6);
  color: var(--pnmt-fail);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
}

.seen {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.seen-title {
  font-size: 1.071rem;
  margin: 0;
}

.count {
  font-weight: 400;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
}

.mobile-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.small {
  font-size: 0.857rem;
}

.dn {
  overflow-wrap: anywhere;
}
</style>
