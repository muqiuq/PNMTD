<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { licenseKeys, licensesApi, typeLabels, typePlurals, typeTabs } from '@/api/licenses'
import type { ListQuery } from '@/api/resources'
import AuditHistory from '@/components/AuditHistory.vue'
import ErrorState from '@/components/ErrorState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import CancelDialog from './dialogs/CancelDialog.vue'
import ChargeDialog from './dialogs/ChargeDialog.vue'
import RenewDialog, { type RenewTarget } from './dialogs/RenewDialog.vue'
import ReviewDialog from './dialogs/ReviewDialog.vue'
import AssignDialog from './item/AssignDialog.vue'
import DeleteDialog from './item/DeleteDialog.vue'
import ItemCosts from './item/ItemCosts.vue'
import ItemDocuments from './item/ItemDocuments.vue'
import ItemHosts from './item/ItemHosts.vue'
import ItemOverview from './item/ItemOverview.vue'
import ItemRenewals from './item/ItemRenewals.vue'
import RetireDialog from './item/RetireDialog.vue'
import VerdictTag from './VerdictTag.vue'

/**
 * A licence's, service's or piece of hardware's page (docs/licences-plan.md §8.7), by its number: `SVC-31 · Business Internet 1 Gbit/s`
 * with its type, state and status; for Edit *Renew…*, *Mark reviewed…*, *Assign to hosts…* and a menu (*Record a cost…*, *Duplicate*,
 * *Cancel…* or *Reactivate*, *Retire…*, and *Delete…* for Manage). The tabs are routes: *Overview*, *Hosts & sensors*, *Costs*,
 * *Renewals*, *Documents*, *History*. A new item arrives with `?assign`, which offers *Assign to hosts…*.
 */
const props = defineProps<{ number: string; tab?: string }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run } = useActions()
const details = computed(() => session.can('Licenses', 'Details'))
const edit = computed(() => session.can('Licenses', 'Edit'))
const manage = computed(() => session.can('Licenses', 'Manage'))

const query = useQuery({
  queryKey: computed(() => licenseKeys.item(Number(props.number))),
  queryFn: () => licensesApi.byNumber(Number(props.number)),
})
const item = computed(() => query.data.value)

type Tab = 'overview' | 'hosts' | 'costs' | 'renewals' | 'documents' | 'history'
const tab = computed<Tab>(
  () => (['hosts', 'costs', 'renewals', 'documents', 'history'] as const).find((t) => t === props.tab) ?? 'overview',
)
const tabItem = (key: Tab, label: string, badge?: number | null) => ({
  key,
  label,
  badge: badge || null,
  to: { name: 'licence', params: { number: props.number, tab: key === 'overview' ? undefined : key } },
})
const tabs = computed(() => {
  const i = item.value
  return [
    tabItem('overview', 'Overview'),
    tabItem('hosts', 'Hosts & sensors', i ? i.hosts + i.links : null),
    tabItem('costs', 'Costs'),
    tabItem('renewals', 'Renewals', i?.renewals),
    tabItem('documents', 'Documents', i?.documents),
    tabItem('history', 'History'),
  ]
})

// --- the dialogs ----------------------------------------------------------------------------------------------------------
const renewing = ref(false)
const reviewing = ref(false)
const assigning = ref(false)
const charging = ref(false)
const cancelling = ref(false)
const retiring = ref(false)
const deleting = ref(false)
const renewTarget = computed<RenewTarget | null>(() => {
  const i = item.value
  return i
    ? {
        id: i.id,
        display: i.display,
        name: i.name,
        expiresOn: i.expiresOn,
        termMonths: i.termMonths,
        priceAmount: i.priceAmount,
        currency: i.currency,
        status: i.status,
        recordCharges: i.recordCharges,
      }
    : null
})

// A new item (the form sends it here with ?assign): offer its hosts once.
const offerAssign = ref(false)
watch(
  () => route.query.assign,
  (value) => {
    if (value === undefined) return
    offerAssign.value = true
    void router.replace({ query: { ...route.query, assign: undefined } })
  },
  { immediate: true },
)
function assignNow() {
  offerAssign.value = false
  assigning.value = true
}

async function duplicate() {
  const i = item.value!
  const copy = await run(() => licensesApi.duplicate(i.id), {
    success: `${i.display} duplicated`,
    failure: 'Not duplicated',
    invalidate: [licenseKeys.all],
  })
  if (copy) await router.push({ name: 'licence-edit', params: { number: String(copy.number) } })
}
const reactivate = () =>
  run(() => licensesApi.reactivate(item.value!.id), {
    success: `${item.value!.display} reactivated`,
    failure: 'Not reactivated',
    invalidate: [licenseKeys.all],
  })

const more = computed<MenuItem[]>(() => {
  const i = item.value
  if (!i) return []
  const status = i.status
  return [
    {
      label: 'Edit in the form…',
      icon: 'pi pi-pencil',
      command: () => router.push({ name: 'licence-edit', params: { number: props.number } }),
    },
    { label: 'Record a cost…', icon: 'pi pi-wallet', command: () => (charging.value = true) },
    { label: 'Duplicate', icon: 'pi pi-copy', command: duplicate },
    { separator: true },
    status === 'Active'
      ? { label: 'Cancel…', icon: 'pi pi-ban', command: () => (cancelling.value = true) }
      : { label: 'Reactivate', icon: 'pi pi-replay', command: reactivate },
    ...(status !== 'Retired'
      ? [{ label: 'Retire…', icon: 'pi pi-inbox', command: () => (retiring.value = true) }]
      : []),
    ...(manage.value
      ? [{ label: 'Delete…', icon: 'pi pi-trash', class: 'danger', command: () => (deleting.value = true) }]
      : []),
  ]
})

const history = (q: ListQuery) => licensesApi.history(item.value!.id, q.page, q.pageSize)

async function deleted() {
  const type = item.value?.type ?? 'License'
  await router.push({ name: 'licences', params: { tab: typeTabs[type] } })
  queryClient.removeQueries({ queryKey: licenseKeys.item(Number(props.number)) })
  await queryClient.invalidateQueries({ queryKey: licenseKeys.all })
}
function retireInstead() {
  deleting.value = false
  retiring.value = true
}
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="query.isError.value"
      :error="query.error.value"
      what="the licence or service"
      @retry="query.refetch()"
    />
    <div v-else-if="!item" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <PageHeader
        :title="`${item.display} · ${item.name}`"
        :crumbs="[
          { label: 'Licences & services', to: { name: 'licences' } },
          { label: typePlurals[item.type], to: { name: 'licences', params: { tab: typeTabs[item.type] } } },
        ]"
      >
        <template #status>
          <Tag :value="typeLabels[item.type]" severity="secondary" />
          <VerdictTag v-if="item.status !== 'Retired'" :code="item.verdictCode" />
          <Tag v-if="item.status === 'Cancelled'" value="cancelled" severity="warn" data-test="status-cancelled" />
          <Tag v-if="item.status === 'Retired'" value="retired" severity="secondary" data-test="status-retired" />
        </template>
        <template v-if="edit" #actions>
          <Button
            label="Renew…"
            icon="pi pi-refresh"
            size="small"
            severity="secondary"
            data-test="renew"
            @click="renewing = true"
          />
          <Button
            label="Mark reviewed…"
            icon="pi pi-check-square"
            size="small"
            severity="secondary"
            data-test="review"
            @click="reviewing = true"
          />
          <Button
            v-if="item.status !== 'Retired'"
            label="Assign to hosts…"
            icon="pi pi-server"
            size="small"
            severity="secondary"
            data-test="assign"
            @click="assigning = true"
          />
          <MoreMenu :items="more" :label="`More actions for ${item.display}`" />
        </template>
      </PageHeader>
      <Message
        v-if="offerAssign && edit && item.status !== 'Retired'"
        severity="success"
        closable
        @close="offerAssign = false"
      >
        {{ item.display }} saved.
        <Button label="Assign to hosts…" size="small" text data-test="offer-assign" @click="assignNow" />
      </Message>
      <TabNav :items="tabs" :active="tab" :label="item.display" />

      <ItemOverview v-if="tab === 'overview'" :item="item" :edit="edit" :details="details" />
      <ItemHosts v-else-if="tab === 'hosts'" :item="item" :edit="edit" @assign="assigning = true" />
      <ItemCosts v-else-if="tab === 'costs'" :item="item" :edit="edit" :manage="manage" :details="details" />
      <ItemRenewals
        v-else-if="tab === 'renewals'"
        :item="item"
        :edit="edit"
        :manage="manage"
        :details="details"
        @renew="renewing = true"
      />
      <ItemDocuments v-else-if="tab === 'documents'" :item="item" :edit="edit" :details="details" />
      <AuditHistory
        v-else
        :subject-id="item.id"
        :name="item.display"
        :load="history"
        :load-key="licenseKeys.history(item.id)"
      />

      <template v-if="edit">
        <RenewDialog v-if="renewTarget" v-model:visible="renewing" :item="renewTarget" />
        <ReviewDialog v-model:visible="reviewing" :ids="[item.id]" :label="`${item.display} ${item.name}`" />
        <AssignDialog v-model:visible="assigning" :item="item" />
        <ChargeDialog
          v-model:visible="charging"
          :license-id="item.id"
          :display="item.display"
          :currency="item.currency"
        />
        <CancelDialog v-model:visible="cancelling" :item="item" />
        <RetireDialog v-model:visible="retiring" :item="item" />
        <DeleteDialog
          v-if="manage"
          v-model:visible="deleting"
          :item="item"
          @deleted="deleted"
          @retire="retireInstead"
        />
      </template>
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6, 2rem);
}
</style>
