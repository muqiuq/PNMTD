<script setup lang="ts">
import Chip from 'primevue/chip'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
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

const types = [
  'LoginSucceeded',
  'LoginFailed',
  'LockedOut',
  'TotpFailed',
  'Logout',
  'TotpEnabled',
  'TotpDisabled',
  'TotpReset',
  'PasswordChanged',
  'PasswordReset',
  'SessionsRevoked',
  'UserCreated',
  'UserUpdated',
  'UserUnlocked',
  'RoleChanged',
  'UserDeleted',
]
const label = (t: string) => t.replace(/([a-z])([A-Z])/g, '$1 $2').replace('Totp', '2FA')
const severity = (t: string) =>
  /Failed|LockedOut/.test(t)
    ? 'danger'
    : /Deleted|Reset|Revoked|Disabled/.test(t)
      ? 'warn'
      : /Succeeded|Enabled|Created/.test(t)
        ? 'success'
        : 'secondary'

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
const table = useServerTable<S['AuthLogItem']>({
  key: [...keys.users, 'log'],
  fetch: (q) => api.users.log(q),
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
        :options="types.map((t) => ({ value: t, label: label(t) }))"
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
    <ListTable :table="table" what="entries">
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
          ><span class="ellipsis cell">{{ data.detail }}</span></template
        >
      </Column>
      <Column header="Address" style="width: 9rem">
        <template #body="{ data }"
          ><span class="mono">{{ data.ipAddress }}</span></template
        >
      </Column>
      <Column header="Browser" style="width: 14rem">
        <template #body="{ data }"
          ><span class="ellipsis cell narrow muted" :title="data.userAgent ?? ''">{{ data.userAgent }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <Tag :value="label(item.eventType)" :severity="severity(item.eventType)" />
          <span>{{ item.username }}</span>
          <RelativeTime :value="item.createdAt" class="muted right" />
        </div>
        <div v-if="item.detail" class="muted ellipsis">{{ item.detail }}</div>
      </template>
    </ListTable>
  </div>
</template>

<style scoped>
.type-select {
  width: 12rem;
}

.cell {
  display: block;
  max-width: 24rem;
}

.cell.narrow {
  max-width: 14rem;
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
