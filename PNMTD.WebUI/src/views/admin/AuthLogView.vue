<script setup lang="ts">
import Chip from 'primevue/chip'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { useQuery } from '@tanstack/vue-query'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, keys, type S } from '@/api/resources'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'

/** The audit log (§4.3): sign-ins, failures, lockouts, 2FA and account changes; newest first. */
const route = useRoute()
const router = useRouter()

// The server's catalog (docs/audit-redmine-zabbix-plan.md §3.9): every type, labelled, so the filter can't fall behind.
const catalog = useQuery({ queryKey: [...keys.audit, 'types'], queryFn: api.audit.types, staleTime: Infinity })
const byType = computed(() => new Map((catalog.data.value ?? []).map((t) => [t.type, t])))
const types = computed(() => (catalog.data.value ?? []).map((t) => ({ value: t.type, label: t.label })))
const label = (t: string) => byType.value.get(t)?.label ?? t
const tones = { Success: 'success', Warn: 'warn', Danger: 'danger', Neutral: 'secondary' } as const
const severity = (t: string) => tones[byType.value.get(t)?.tone ?? 'Neutral']

const type = ref(typeof route.query.type === 'string' ? route.query.type : '')
const userId = ref(typeof route.query.userId === 'string' ? route.query.userId : '')
const userName = ref(typeof route.query.user === 'string' ? route.query.user : '')
watch([type, userId], () =>
  router.replace({
    query: {
      ...route.query,
      type: type.value || undefined,
      userId: userId.value || undefined,
      user: userId.value ? userName.value : undefined,
      page: undefined,
    },
  }),
)
const filters = computed(() => ({ type: type.value || undefined, userId: userId.value || undefined }))
const table = useServerTable<S['AuditLogItem']>({
  key: [...keys.audit, 'log'],
  fetch: (q) => api.audit.log(q),
  filters,
  syncUrl: true,
})
</script>

<template>
  <div class="page">
    <PageHeader title="Auth log" />
    <FilterBar
      v-model:search="table.search.value"
      placeholder="Search user or detail"
      :active="(type ? 1 : 0) + (userId ? 1 : 0)"
    >
      <Select
        v-model="type"
        :options="types"
        option-label="label"
        option-value="value"
        placeholder="All events"
        show-clear
        size="small"
        class="type-select"
        aria-label="Event type"
      />
      <Chip v-if="userId" :label="userName || 'One user'" removable @remove="((userId = ''), (userName = ''))" />
    </FilterBar>
    <ListTable :table="table" what="entries" fixed>
      <Column header="Time" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.createdAt" /></template>
      </Column>
      <Column header="User" style="width: 12rem">
        <template #body="{ data }">
          <a v-if="data.userId" href="#" @click.prevent="((userName = data.username), (userId = data.userId))">{{
            data.username
          }}</a>
          <span v-else class="muted">{{ data.username || '–' }}</span>
        </template>
      </Column>
      <Column header="Event" style="width: 11rem">
        <template #body="{ data }"
          ><Tag :value="label(data.eventType)" :severity="severity(data.eventType)"
        /></template>
      </Column>
      <Column header="Detail">
        <template #body="{ data }"
          ><span class="ellipsis cell" :title="data.detail ?? ''">{{ data.detail }}</span></template
        >
      </Column>
      <Column header="Address" style="width: 9rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.ipAddress }}</span></template
        >
      </Column>
      <Column header="Browser" style="width: 14rem">
        <template #body="{ data }"
          ><span class="ellipsis cell muted" :title="data.userAgent ?? ''">{{ data.userAgent }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="label(item.eventType)" :severity="severity(item.eventType)" />
          <span>{{ item.username }}</span>
          <RelativeTime :value="item.createdAt" class="muted right" />
        </div>
        <div v-if="item.detail" class="muted clamp">{{ item.detail }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.type-select {
  width: 12rem;
}

/* Cut at the column's edge: the table's layout is fixed, and Detail takes what the others leave (§4.1 of
   docs/audit-redmine-zabbix-plan.md). */
.cell {
  display: block;
}

.clamp {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.right {
  margin-left: auto;
}
</style>
