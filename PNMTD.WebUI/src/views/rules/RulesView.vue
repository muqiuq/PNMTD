<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import { useRouter } from 'vue-router'
import { api, keys, ruleRequest, type S } from '@/api/resources'
import InlineField from '@/components/InlineField.vue'
import { useActions } from '@/composables/useActions'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import { humanize } from '@/composables/useLookups'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'
import { providerIcon, providerLabel } from './ruleHelp'

/** Who hears about what (§5.6): type, recipient, the provider it goes to, how many sensors. */
const session = useSession()
const router = useRouter()
const { invalidate } = useActions()

/** The list has no recipient for a Viewer's mask; the switch reads the rule first. */
async function setEnabled(item: S['NotificationRuleItem'], enabled: boolean) {
  const rule = await api.rules.get(item.id)
  await api.rules.update(item.id, ruleRequest(rule, { enabled }))
  await invalidate([keys.rules, keys.rule(item.id)])
}

const table = useServerTable<S['NotificationRuleItem']>({
  key: keys.rules,
  fetch: (q) => api.rules.list(q),
  defaultSort: 'name',
  syncUrl: true,
})
</script>

<template>
  <div class="page">
    <PageHeader title="Notification rules">
      <template #actions>
        <Button
          v-if="session.can('Monitoring', 'Edit')"
          label="New rule"
          icon="pi pi-plus"
          size="small"
          @click="router.push({ name: 'notification-rule-new' })"
        />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search name or recipient" />
    <ListTable :table="table" what="notification rules" :row-class="(r) => (r.enabled ? undefined : 'row-muted')">
      <Column field="name" header="Name" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'notification-rule', params: { id: data.id } }" class="strong">{{
            data.name
          }}</RouterLink>
          <Tag v-if="!data.enabled" value="off" severity="secondary" class="off" />
        </template>
      </Column>
      <Column field="type" header="Sends" sortable>
        <template #body="{ data }">{{ humanize(data.type) }}</template>
      </Column>
      <Column field="recipient" header="Recipient" sortable>
        <template #body="{ data }">
          <span class="recipient" :title="providerLabel(data.provider)">
            <i :class="providerIcon(data.provider)" class="muted" aria-hidden="true" />
            <span class="mono ellipsis">{{ data.recipient }}</span>
          </span>
        </template>
      </Column>
      <Column header="On" style="width: 4.5rem">
        <template #body="{ data }">
          <InlineField
            :value="data.enabled"
            type="bool"
            :label="`Enabled ${data.name}`"
            :readonly="!session.can('Monitoring', 'Edit')"
            :save="(v) => setEnabled(data, Boolean(v))"
          />
        </template>
      </Column>
      <Column header="Sensors" style="width: 6rem">
        <template #body="{ data }">{{ data.sensorCount }}</template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'notification-rule', params: { id: item.id } }" class="mobile-row">
          <i :class="providerIcon(item.provider)" class="muted" />
          <span class="strong">{{ item.name }}</span>
          <span class="muted right">{{ item.sensorCount }} sensors</span>
        </RouterLink>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.strong {
  font-weight: 500;
}

.off {
  margin-left: var(--pnmt-space-2);
}

.recipient {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  max-width: 26rem;
  min-width: 0;
}

.mobile-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  color: var(--pnmt-text);
}

.right {
  margin-left: auto;
}
</style>
