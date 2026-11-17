<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Dialog from 'primevue/dialog'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import { formatAbsolute, formatDay } from '@/format'
import { useSession } from '@/stores/session'
import { daysLeftText, fingerprint, shortFingerprint } from '@/views/sensors/certificate/certificate'
import AddAuthorityDialog from './certificates/AddAuthorityDialog.vue'

/**
 * *Admin → Certificates* (docs/ssl-certificate-plan.md §8.5): PNMT's private CAs, each with its root's subject and SHA-256,
 * valid until with the days left (in warn colours from 90 days), its intermediates, the sensors that trust it, and who added
 * it. Certificates: View reads; its Manage adds a CA (§4.5's preview first), removes an unused one, and is told which
 * sensors still trust one in use. A private CA is never edited: a new root is a new CA.
 */
const session = useSession()
const canManage = computed(() => session.can('Certificates', 'Manage'))
const { ask, notify, invalidate } = useActions()
const now = useNow()
const list = useQuery({ queryKey: keys.certificateAuthorities, queryFn: api.certificateAuthorities.list })

const DAY = 86_400_000
const tone = (a: S['CertificateAuthorityItem']) => {
  const left = Date.parse(a.rootNotAfter) - now.value
  return left <= 0 ? 'fail' : left <= 90 * DAY ? 'warn' : 'ok'
}

const adding = ref(false)
const inUse = ref<{ name: string; message: string } | null>(null)
async function remove(a: S['CertificateAuthorityItem']) {
  const used = a.usedBy > 0
  if (
    !(await ask(
      `Remove ${a.name}?`,
      used
        ? `${a.usedBy === 1 ? 'A sensor trusts' : `${a.usedBy} sensors trust`} it: PNMT removes only an unused CA, and names the sensors otherwise.`
        : 'No sensor trusts it any more. Its PEM goes; the audit log keeps its name and SHA-256.',
      'Remove',
    ))
  )
    return
  try {
    await api.certificateAuthorities.remove(a.id)
    notify.success(`${a.name} removed`)
    await invalidate([keys.certificateAuthorities])
  } catch (e) {
    if (e instanceof ApiError && e.code === 'authority_in_use') {
      inUse.value = { name: a.name, message: e.message }
      await invalidate([keys.certificateAuthorities])
    } else {
      notify.error(e, 'Not removed')
    }
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Certificates">
      <template #actions>
        <Button v-if="canManage" label="Add a private CA…" icon="pi pi-plus" size="small" @click="adding = true" />
      </template>
    </PageHeader>
    <p class="muted intro">
      Private CAs: roots PNMT trusts for the SSL certificate sensors that name them, in the private mode. Public CAs
      come with PNMT's image; a self-signed certificate needs no CA, it is pinned. A CA is never edited: a new root is a
      new CA, the sensors move to it, and the old one is removed once unused.
    </p>
    <ErrorState v-if="list.isError.value" :error="list.error.value" what="the private CAs" @retry="list.refetch()" />
    <DataTable
      v-else
      :value="list.data.value ?? []"
      :loading="list.isLoading.value"
      size="small"
      class="panel"
      data-key="id"
      :table-props="{ 'aria-label': 'Private CAs' }"
    >
      <Column header="Private CA">
        <template #body="{ data }">
          <strong>{{ data.name }}</strong>
          <div class="muted small dn" :title="data.rootSubject">{{ data.rootSubject }}</div>
        </template>
      </Column>
      <Column header="Root SHA-256" style="width: 17rem">
        <template #body="{ data }">
          <CopyField
            :value="fingerprint(data.rootSha256)"
            :display="shortFingerprint(data.rootSha256)"
            :label="`SHA-256 of ${data.name}`"
          />
        </template>
      </Column>
      <Column header="Valid until" style="width: 11rem">
        <template #body="{ data }">
          <span :title="formatAbsolute(data.rootNotAfter)">{{ formatDay(new Date(data.rootNotAfter)) }}</span>
          <div class="small" :class="`tone-${tone(data)}`">{{ daysLeftText(data.rootNotAfter, now) }}</div>
        </template>
      </Column>
      <Column header="Intermediates" style="width: 7rem">
        <template #body="{ data }">{{ data.intermediateCount }}</template>
      </Column>
      <Column header="Used by" style="width: 8rem">
        <template #body="{ data }">
          <span :class="{ muted: !data.usedBy }">{{ data.usedBy }} {{ data.usedBy === 1 ? 'sensor' : 'sensors' }}</span>
        </template>
      </Column>
      <Column header="Added" style="width: 10rem">
        <template #body="{ data }">
          <RelativeTime :value="data.createdAt" />
          <div v-if="data.createdBy" class="muted small">by {{ data.createdBy }}</div>
        </template>
      </Column>
      <Column header="" style="width: 9rem">
        <template #body="{ data }">
          <div class="row-actions">
            <Button
              v-tooltip.top="'Download the PEM'"
              as="a"
              :href="api.certificateAuthorities.pemUrl(data.id)"
              icon="pi pi-download"
              size="small"
              text
              severity="secondary"
              :aria-label="`Download the PEM of ${data.name}`"
            />
            <Button
              v-if="canManage"
              label="Remove…"
              size="small"
              text
              severity="danger"
              :aria-label="`Remove ${data.name}…`"
              @click="remove(data)"
            />
          </div>
        </template>
      </Column>
      <template #empty>
        <EmptyState
          icon="pi pi-verified"
          title="No private CA yet."
          text="Add the root of an internal CA, and sensors in the private mode can trust the certificates it signs."
        />
      </template>
    </DataTable>

    <AddAuthorityDialog v-if="canManage" v-model:visible="adding" />
    <Dialog
      :visible="inUse !== null"
      :header="`${inUse?.name ?? ''} is still in use`"
      modal
      :style="{ width: '34rem' }"
      @update:visible="(v: boolean) => !v && (inUse = null)"
    >
      <p class="dialog-text">{{ inUse?.message }}</p>
      <template #footer>
        <Button label="Close" severity="secondary" text @click="inUse = null" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.intro {
  margin: 0;
  max-width: 60rem;
}

.small {
  font-size: 0.857rem;
}

.dn {
  overflow-wrap: anywhere;
}

.row-actions {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  justify-content: flex-end;
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

.dialog-text {
  margin-top: 0;
}
</style>
