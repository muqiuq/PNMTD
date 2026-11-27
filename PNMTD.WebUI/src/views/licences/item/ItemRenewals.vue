<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import { computed } from 'vue'
import { licenseKeys, licensesApi, money, type LicenseDetail, type LicenseRenewalItem } from '@/api/licenses'
import EmptyState from '@/components/EmptyState.vue'
import ErrorState from '@/components/ErrorState.vue'
import InlineField from '@/components/InlineField.vue'
import { useActions } from '@/composables/useActions'
import { formatDay } from '@/format'

/**
 * The item's *Renewals* tab (docs/licences-plan.md §3.7, §5.6, §8.7): newest first, the day, by a person or automatically, the old and
 * the new expiry, the price from then on, the reference and note (corrected with Edit), who, and the paper. Deleting one is Manage's;
 * neither moves the expiry back: the expiry is the item's own field.
 */
const props = defineProps<{ item: LicenseDetail; edit: boolean; manage: boolean; details: boolean }>()
const emit = defineEmits<{ renew: [] }>()
const queryClient = useQueryClient()
const { done, ask } = useActions()

const renewals = useQuery({
  queryKey: computed(() => licenseKeys.renewals(props.item.id)),
  queryFn: () => licensesApi.renewals(props.item.id),
})
const documents = useQuery({
  queryKey: computed(() => licenseKeys.documents(props.item.id)),
  queryFn: () => licensesApi.documents(props.item.id),
})
const rows = computed(() =>
  [...(renewals.data.value ?? [])].sort((a, b) => b.on.localeCompare(a.on) || b.createdAt.localeCompare(a.createdAt)),
)
const papers = (r: LicenseRenewalItem) => (documents.data.value ?? []).filter((d) => r.documentIds.includes(d.fileId))
const text = (v: unknown) => (v === null || v === undefined ? null : String(v).trim() || null)

async function change(r: LicenseRenewalItem, patch: { reference?: string | null; note?: string | null }) {
  await licensesApi.updateRenewal(
    props.item.id,
    r.id,
    patch.reference !== undefined ? patch.reference : (r.reference ?? null),
    patch.note !== undefined ? patch.note : (r.note ?? null),
  )
  await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
}

async function remove(r: LicenseRenewalItem) {
  if (
    !(await ask(
      `Delete the renewal of ${formatDay(r.on)}?`,
      `The expiry stays ${props.item.expiresOn ? formatDay(props.item.expiresOn) : 'as it is'}: deleting a renewal doesn't move it back. Its documents stay on the item.`,
    ))
  )
    return
  await done(() => licensesApi.deleteRenewal(props.item.id, r.id), {
    success: 'Renewal deleted',
    failure: 'Not deleted',
    invalidate: [licenseKeys.all],
  })
}
</script>

<template>
  <section class="panel" aria-labelledby="renewals-title">
    <div class="panel-header">
      <span id="renewals-title">Renewals</span>
      <span class="spacer" />
      <Button v-if="edit" label="Renew…" icon="pi pi-refresh" size="small" text @click="emit('renew')" />
    </div>
    <ErrorState
      v-if="renewals.isError.value"
      :error="renewals.error.value"
      what="the renewals"
      @retry="renewals.refetch()"
    />
    <EmptyState
      v-else-if="renewals.data.value && !rows.length"
      icon="pi pi-refresh"
      title="Not renewed yet."
      :text="item.renewal === 'Automatic' ? 'PNMT renews it when its term ends, unless it is cancelled.' : undefined"
    />
    <DataTable v-else :value="rows" :loading="renewals.isPending.value" data-key="id" size="small" data-test="renewals">
      <Column header="Day" style="width: 8rem">
        <template #body="{ data }"
          ><span class="nowrap">{{ formatDay(data.on) }}</span></template
        >
      </Column>
      <Column header="Kind">
        <template #body="{ data }">{{ data.kind === 'AutomaticRenewal' ? 'automatically' : 'by a person' }}</template>
      </Column>
      <Column header="Expiry">
        <template #body="{ data }">
          <span class="nowrap"
            ><span class="muted">{{ data.previousExpiresOn ? formatDay(data.previousExpiresOn) : 'none' }} →</span>
            {{ formatDay(data.newExpiresOn) }}</span
          >
        </template>
      </Column>
      <Column header="Price from then on">
        <template #body="{ data }">
          <span v-if="data.priceAmount !== null && data.currency" class="nowrap">{{
            money(data.priceAmount, data.currency)
          }}</span>
          <span v-else class="muted">unchanged</span>
        </template>
      </Column>
      <Column header="Reference">
        <template #body="{ data }">
          <InlineField
            :value="data.reference"
            label="Reference"
            field="reference"
            :readonly="!edit"
            :save="(v) => change(data, { reference: text(v) })"
          />
        </template>
      </Column>
      <Column header="Note">
        <template #body="{ data }">
          <InlineField
            :value="data.note"
            label="Note"
            field="note"
            :readonly="!edit"
            :save="(v) => change(data, { note: text(v) })"
          />
        </template>
      </Column>
      <Column header="Paper">
        <template #body="{ data }">
          <span class="papers">
            <template v-for="d in papers(data)" :key="d.fileId">
              <a v-if="details" :href="licensesApi.documentUrl(item.id, d.fileId, d.name)" download
                ><i class="pi pi-file" aria-hidden="true" /> {{ d.name }}</a
              >
              <span v-else class="muted">{{ d.name }}</span>
            </template>
          </span>
        </template>
      </Column>
      <Column header="By">
        <template #body="{ data }"
          ><span class="muted">{{ data.createdBy ?? '–' }}</span></template
        >
      </Column>
      <Column v-if="manage" style="width: 3rem">
        <template #body="{ data }">
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Delete the renewal of ${formatDay(data.on)}`"
            @click="remove(data)"
          />
        </template>
      </Column>
    </DataTable>
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.papers {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}
</style>
