<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useCopy } from '@/composables/useCopy'
import { useServerTable } from '@/composables/useServerTable'
import { logonKindLabel, logonKinds, routerLogonKinds } from '@/views/admin/servers/agentFormat'
import AccessReviewBadge from '@/views/cookie/AccessReviewBadge.vue'
import { keyLabel } from '../linux/linuxFormat'
import NotExpectedDialog from './NotExpectedDialog.vue'

/**
 * The logins and connections of a server (docs/windows-sensor-plan.md §4.7; docs/linux-sensor-plan.md §5.6), with their
 * filters: every admin login, failures aggregated, the kinds of its platform. On the Access tab of either platform. Linux adds
 * the key each SSH login used (its fingerprint, short, with copy and a filter; names come with the key manager), sudo's and su's
 * target user and command, and the names the internet's scanners tried. A router's logins from its log (routerId instead of
 * server; docs/cookie-access-plan.md §4.2) on its own Access tab. Each reviewed login shows the access watch's verdict with a
 * link to its case, and a successful one can be marked This wasn't expected (§7). The User role: names and addresses.
 */
const props = defineProps<{ server?: S['AgentServerDetail']; routerId?: string; keyFingerprint?: string | null }>()
const sensorId = computed(() => props.server?.server.sensorId ?? props.routerId ?? '')
const linux = computed(() => props.server?.server.platform === 'Linux')
const outcome = ref<S['LogonOutcome'] | null>(null)
const kind = ref<S['LogonKind'] | null>(null)
const adminsOnly = ref(false)
// The key filter: typed (with or without SHA256:), or a click on a key in the list; applied after a pause in typing.
const keyInput = ref(props.keyFingerprint ?? '')
const keyFilter = ref(keyInput.value.trim())
let timer: ReturnType<typeof setTimeout> | undefined
watch(keyInput, (v) => {
  clearTimeout(timer)
  timer = setTimeout(() => (keyFilter.value = v.trim()), 300)
})
watch(
  () => props.keyFingerprint,
  (fp) => (keyInput.value = keyFilter.value = fp ?? ''),
)
const filterByKey = (fp: string) => (keyInput.value = keyFilter.value = fp)
const filters = computed(() => ({
  outcome: outcome.value ?? undefined,
  kind: kind.value ?? undefined,
  admin: adminsOnly.value ? true : undefined,
  keyFingerprint: linux.value && keyFilter.value ? keyFilter.value : undefined,
}))
const listKey = props.server
  ? ([...keys.agentServer(sensorId.value), 'logins'] as const)
  : ([...keys.sensor(sensorId.value), 'mikrotik-logins'] as const)
const table = useServerTable<S['AgentLoginItem']>({
  key: listKey,
  fetch: (q) =>
    props.server
      ? api.agents.logins(sensorId.value, q)
      : api.mikrotik.logins(sensorId.value, q).then((view) => view.logins),
  pageSize: 50,
  filters,
  refetchInterval: 15_000,
})
const kinds = computed(() => (props.server ? logonKinds[props.server.server.platform] : routerLogonKinds))
const who = (l: S['AgentLoginItem']) => (l.domain ? `${l.domain}\\${l.user}` : l.user)
/** An aggregate of failures for accounts that don't exist: the scanners (§5.6). */
const scanner = (l: S['AgentLoginItem']) => l.failureReason === 'invalid user'
const copy = useCopy()
/** The login This wasn't expected is asked for. */
const notExpected = ref<S['AgentLoginItem'] | null>(null)
const notExpectedLabel = "This wasn't expected"
</script>

<template>
  <section class="panel">
    <div class="panel-header">{{ server ? 'Logins and connections' : 'Logins' }}</div>
    <div class="panel-body toolbar">
      <Select v-model="outcome" :options="['Success', 'Failure']" show-clear size="small" placeholder="Every outcome" />
      <Select
        v-model="kind"
        :options="kinds"
        :option-label="(k: S['LogonKind']) => logonKindLabel(k)"
        show-clear
        size="small"
        placeholder="Every kind"
        aria-label="Kind"
      />
      <InputText
        v-if="linux"
        v-model="keyInput"
        size="small"
        placeholder="Key fingerprint"
        aria-label="Key fingerprint"
        class="key-filter mono"
      />
      <Button
        v-if="linux && keyFilter"
        label="Every key"
        icon="pi pi-times"
        size="small"
        text
        severity="secondary"
        @click="keyInput = keyFilter = ''"
      />
      <label class="check"><input v-model="adminsOnly" type="checkbox" /> admins only</label>
    </div>
    <ListTable
      :table="table"
      what="logins"
      :empty-text="server ? 'No login recorded yet.' : 'No login read from its log yet.'"
    >
      <Column header="When" style="width: 9rem">
        <template #body="{ data }">
          <RelativeTime :value="data.lastAt ?? data.at" />
          <div v-if="data.count > 1" class="muted small">{{ data.count }}× since <RelativeTime :value="data.at" /></div>
        </template>
      </Column>
      <Column header="Who">
        <template #body="{ data }">
          <div class="tag-line">
            <strong v-if="!scanner(data)">{{ who(data) }}</strong>
            <Tag v-if="data.isAdmin" value="admin" severity="warn" class="small" />
            <Tag
              v-if="data.outcome === 'Failure'"
              :value="data.failureReason ?? 'failed'"
              :severity="scanner(data) ? 'secondary' : 'danger'"
              class="small"
            />
          </div>
          <div v-if="data.triedUsers?.length" class="muted small">
            tried {{ data.triedUsers.join(', ') }}<template v-if="data.count > data.triedUsers.length">, …</template>
          </div>
        </template>
      </Column>
      <Column header="How" :style="{ width: linux ? '14rem' : '12rem' }">
        <template #body="{ data }">
          {{ logonKindLabel(data.kind) }}
          <template v-if="data.targetUser">
            → <strong>{{ data.targetUser }}</strong></template
          >
          <span class="muted small"> {{ data.authPackage }}</span>
          <span v-if="data.elevated && !linux" class="muted small"> · elevated</span>
          <div v-if="data.command" class="mono small ellipsis command" :title="data.command">{{ data.command }}</div>
        </template>
      </Column>
      <Column v-if="linux" header="Key" style="width: 15rem">
        <template #body="{ data }">
          <span v-if="data.keyFingerprint" class="key">
            <Button
              v-tooltip.top="`Only logins with ${data.keyFingerprint}`"
              :label="keyLabel(data)"
              link
              size="small"
              class="fingerprint"
              :class="{ mono: !data.keyName }"
              @click="filterByKey(data.keyFingerprint)"
            />
            <Button
              icon="pi pi-copy"
              text
              size="small"
              severity="secondary"
              :aria-label="`Copy ${data.keyFingerprint}`"
              @click="copy(data.keyFingerprint, 'Key fingerprint')"
            />
          </span>
        </template>
      </Column>
      <Column header="From" style="width: 12rem">
        <template #body="{ data }">
          <span class="mono small">{{ data.sourceAddress ?? '' }}</span>
          <div class="muted small">{{ data.workstation }}</div>
        </template>
      </Column>
      <Column header="Logged off" style="width: 8rem">
        <template #body="{ data }"><RelativeTime v-if="data.loggedOffAt" :value="data.loggedOffAt" /></template>
      </Column>
      <Column header="Review" style="width: 9rem">
        <template #body="{ data }">
          <span class="review">
            <AccessReviewBadge v-if="data.review" :review="data.review" />
            <Button
              v-if="data.outcome === 'Success'"
              v-tooltip.top="notExpectedLabel"
              icon="pi pi-flag"
              text
              size="small"
              severity="secondary"
              :aria-label="`${notExpectedLabel}: ${who(data)}`"
              @click="notExpected = data"
            />
          </span>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <strong>{{ scanner(item) ? 'invalid user' : who(item) }}</strong> · {{ logonKindLabel(item.kind) }}
          <template v-if="item.targetUser"> → {{ item.targetUser }}</template>
          <Tag v-if="item.outcome === 'Failure'" value="failed" severity="danger" />
          <AccessReviewBadge v-if="item.review" :review="item.review" />
          <Button
            v-if="item.outcome === 'Success'"
            :label="notExpectedLabel"
            icon="pi pi-flag"
            text
            size="small"
            severity="secondary"
            @click="notExpected = item"
          />
        </div>
        <div class="small muted">
          <RelativeTime :value="item.lastAt ?? item.at" /> · {{ item.sourceAddress }}
          <span v-if="item.keyFingerprint"> · {{ keyLabel(item) }}</span>
        </div>
      </template>
    </ListTable>
    <NotExpectedDialog v-model:login="notExpected" :invalidate="listKey" />
  </section>
</template>

<style scoped>
.toolbar,
.line {
  display: flex;
  gap: var(--pnmt-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.check {
  display: flex;
  gap: var(--pnmt-space-1);
  align-items: center;
}

.key-filter {
  width: 16rem;
}

.key {
  display: inline-flex;
  align-items: center;
  white-space: nowrap;
}

.fingerprint {
  padding: 0;
  font-size: 0.857rem;
  white-space: nowrap;
  justify-content: flex-start;
}

.command {
  max-width: 14rem;
}

.small {
  font-size: 0.857rem;
}

.review {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.tag-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}
</style>
