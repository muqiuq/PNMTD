<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import { useRouter } from 'vue-router'
import { api, keys, mailRuleRequest, type S } from '@/api/resources'
import InlineField from '@/components/InlineField.vue'
import { useActions } from '@/composables/useActions'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useServerTable } from '@/composables/useServerTable'
import { useSession } from '@/stores/session'

/** Mail input rules (§5.6): which mails become events of which sensor. */
const session = useSession()
const router = useRouter()
const { invalidate } = useActions()
async function setEnabled(item: S['MailInputRule'], enabled: boolean) {
  await api.mail.updateRule(item.id, mailRuleRequest(item, { enabled }))
  await invalidate([keys.mail])
}

const table = useServerTable<S['MailInputRule']>({
  key: [...keys.mail, 'rules'],
  fetch: (q) => api.mail.rules(q),
  defaultSort: 'name',
  syncUrl: true,
})
</script>

<template>
  <div class="page">
    <PageHeader title="Mail inputs">
      <template #actions>
        <Button
          v-if="session.can('Monitoring', 'Edit')"
          label="New rule"
          icon="pi pi-plus"
          size="small"
          @click="router.push({ name: 'mail-input-new' })"
        />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search name" />
    <ListTable :table="table" what="mail input rules" :row-class="(r) => (r.enabled ? undefined : 'row-muted')">
      <Column field="name" header="Name" sortable>
        <template #body="{ data }">
          <RouterLink :to="{ name: 'mail-input', params: { id: data.id } }" class="strong">{{ data.name }}</RouterLink>
          <Tag v-if="!data.enabled" value="off" severity="secondary" class="off" />
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
      <Column header="Matches">
        <template #body="{ data }">
          <span class="tests">
            <span v-if="data.fromTest"
              ><span class="muted">from</span> <code>{{ data.fromTest }}</code></span
            >
            <span v-if="data.subjectTest"
              ><span class="muted">subject</span> <code>{{ data.subjectTest }}</code></span
            >
            <span v-if="data.bodyTest"
              ><span class="muted">body</span> <code>{{ data.bodyTest }}</code></span
            >
          </span>
        </template>
      </Column>
      <Column header="Codes" style="width: 10rem">
        <template #body="{ data }">
          <span class="muted nowrap"
            >ok {{ data.okCode ?? '–' }} · fail {{ data.failCode ?? '–' }} · else {{ data.defaultCode }}</span
          >
        </template>
      </Column>
      <Column header="Sensor">
        <template #body="{ data }">
          <RouterLink v-if="data.sensor" :to="{ name: 'sensor', params: { id: data.sensor.id } }"
            >{{ data.sensor.hostName }} / {{ data.sensor.name }}</RouterLink
          >
          <span v-else class="muted">none</span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'mail-input', params: { id: item.id } }" class="strong">{{ item.name }}</RouterLink>
        <div class="muted ellipsis">
          {{ item.sensor ? `${item.sensor.hostName} / ${item.sensor.name}` : 'no sensor' }}
        </div>
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

.tests {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
}
</style>
