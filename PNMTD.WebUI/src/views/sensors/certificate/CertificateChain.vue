<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed, ref, useId } from 'vue'
import type { S } from '@/api/resources'
import { formatAbsolute } from '@/format'
import { fingerprint, shortFingerprint, sourceLabel, type Trust } from './certificate'

/**
 * The chain (docs/ssl-certificate-plan.md §4.4, §8.3), from the leaf to the root, each row opening its own facts and
 * marked *sent by the server*, *public root* or *private CA: …*; an incomplete chain and a root sent along as hints; and the
 * fix as a link: *Trust this root as a private CA…* for a root PNMT doesn't know (Certificates: Manage), *Switch to
 * pinned…* for a self-signed certificate in a CA mode.
 */
const props = withDefaults(
  defineProps<{
    chain: S['CertificateChainEntry'][]
    validation?: S['CertificateValidation'] | null
    trust: Trust
    selfSigned: boolean
    canManage?: boolean
    canEdit?: boolean
  }>(),
  { validation: null, canManage: false, canEdit: false },
)
defineEmits<{ 'trust-root': [entry: S['CertificateChainEntry']]; pin: [] }>()
const uid = useId()
const open = ref<Record<number, boolean>>({})
const caMode = computed(() => props.trust !== 'Pinned')
const v = computed(() => props.validation)
// A root the server sends that PNMT doesn't know: a private CA to trust, in a CA mode.
const unknownRoot = computed(() => {
  if (!caMode.value || !v.value || v.value.trusted || props.selfSigned) return null
  const last = props.chain[props.chain.length - 1]
  return last && last.depth > 0 && last.selfSigned && last.source === 'Server' ? last : null
})
const sourceSeverity = (s: S['CertificateSource']) =>
  s === 'Server' ? 'secondary' : s === 'PrivateCa' ? 'info' : s === 'PublicRoot' ? 'success' : 'contrast'
</script>

<template>
  <section class="panel" :aria-labelledby="`${uid}-chain`">
    <div :id="`${uid}-chain`" class="panel-header">Chain</div>
    <div class="panel-body chain">
      <ol class="rows">
        <li v-for="c in chain" :key="`${c.depth}-${c.sha256}`" class="row">
          <button
            type="button"
            class="head"
            :aria-expanded="!!open[c.depth]"
            @click="open = { ...open, [c.depth]: !open[c.depth] }"
          >
            <i :class="open[c.depth] ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true" />
            <span class="muted depth">{{ c.depth === 0 ? 'leaf' : c.depth }}</span>
            <strong class="ellipsis">{{ c.subject.commonName ?? c.subject.organization ?? c.subject.dn }}</strong>
            <Tag :value="sourceLabel(c.source, c.sourceName)" :severity="sourceSeverity(c.source)" />
            <Tag v-if="c.trustAnchor" value="trust anchor" severity="success" />
            <Tag v-if="c.selfSigned && c.depth > 0" value="root" severity="secondary" />
            <Tag v-if="c.selfSigned && c.depth === 0" value="self-signed" severity="warn" />
          </button>
          <dl v-if="open[c.depth]" class="facts detail">
            <dt>Subject</dt>
            <dd class="mono small dn">{{ c.subject.dn }}</dd>
            <dt>Issuer</dt>
            <dd class="mono small dn">{{ c.issuer.dn }}</dd>
            <dt>Valid</dt>
            <dd>{{ formatAbsolute(c.notBefore) }} to {{ formatAbsolute(c.notAfter) }}</dd>
            <dt>Key</dt>
            <dd>{{ c.key }}</dd>
            <dt>SHA-256</dt>
            <dd class="mono small dn" :title="fingerprint(c.sha256)">{{ fingerprint(c.sha256) }}</dd>
          </dl>
          <div v-else class="muted small sub">
            valid until {{ formatAbsolute(c.notAfter) }} · {{ c.key }} ·
            <span class="mono">{{ shortFingerprint(c.sha256) }}</span>
          </div>
        </li>
      </ol>
      <template v-if="v && caMode">
        <Message v-if="!v.complete" severity="warn" :closable="false" size="small">
          Incomplete chain: the server doesn't send its intermediate. Browsers fetch it themselves; curl, Java, Python
          and Firefox without a cached copy fail. Configure the server to send the whole chain.
        </Message>
        <Message v-if="v.sendsRoot" severity="info" :closable="false" size="small">
          The server sends the root too: harmless, clients have it or don't trust it anyway.
        </Message>
      </template>
      <p v-if="unknownRoot && canManage" class="fix">
        <i class="pi pi-wrench muted" aria-hidden="true" />
        The chain ends at a root PNMT doesn't know, {{ unknownRoot.subject.commonName ?? unknownRoot.subject.dn }}.
        <Button
          label="Trust this root as a private CA…"
          link
          size="small"
          class="link"
          @click="$emit('trust-root', unknownRoot)"
        />
      </p>
      <p v-if="selfSigned && caMode && canEdit" class="fix">
        <i class="pi pi-wrench muted" aria-hidden="true" />
        A self-signed certificate: no CA can vouch for it.
        <Button label="Switch to pinned…" link size="small" class="link" @click="$emit('pin')" />
      </p>
    </div>
  </section>
</template>

<style scoped>
.chain {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.row {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
}

.head {
  all: unset;
  box-sizing: border-box;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  cursor: pointer;
  min-height: 28px;
}

.head:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.depth {
  font-size: 0.786rem;
  width: 2rem;
}

.sub {
  padding-left: calc(2rem + var(--pnmt-space-4) + 0.8rem);
}

.detail {
  margin: var(--pnmt-space-2) 0 var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}

.dn {
  overflow-wrap: anywhere;
}

.fix {
  margin: 0;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.link {
  padding: 0 var(--pnmt-space-1);
}
</style>
