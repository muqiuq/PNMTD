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
import { logonKindLabel, logonKinds } from '@/views/admin/servers/agentFormat'
import { shortFingerprint } from '../linux/linuxFormat'

/**
 * The logins and connections of a server (docs/windows-sensor-plan.md §4.7; docs/linux-sensor-plan.md §5.6), with their
 * filters: every admin login, failures aggregated, the kinds of its platform. On the Access tab of either platform. Linux adds
 * the key each SSH login used (its fingerprint, short, with copy and a filter; names come with the key manager), sudo's and su's
 * target user and command, and the names the internet's scanners tried. The User role: names and addresses.
 */
const props = defineProps<{ server: S['AgentServerDetail']; keyFingerprint?: string | null }>()
const linux = computed(() => props.server.server.platform === 'Linux')
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
const table = useServerTable<S['AgentLoginItem']>({
  key: [...keys.agentServer(props.server.server.sensorId), 'logins'],
  fetch: (q) => api.agents.logins(props.server.server.sensorId, q),
  pageSize: 50,
  filters,
  refetchInterval: 15_000,
})
const kinds = computed(() => logonKinds[props.server.server.platform])
const who = (l: S['AgentLoginItem']) => (l.domain ? `${l.domain}\\${l.user}` : l.user)
/** An aggregate of failures for accounts that don't exist: the scanners (§5.6). */
const scanner = (l: S['AgentLoginItem']) => l.failureReason === 'invalid user'
const copy = useCopy()
</script>

<template>
  <section class="panel">
    <div class="panel-header">Logins and connections</div>
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
    <ListTable :table="table" what="logins" empty-text="No login recorded yet.">
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
              :label="shortFingerprint(data.keyFingerprint)"
              link
              size="small"
              class="mono fingerprint"
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
      <template #mobile="{ item }">
        <div class="line">
          <strong>{{ scanner(item) ? 'invalid user' : who(item) }}</strong> · {{ logonKindLabel(item.kind) }}
          <template v-if="item.targetUser"> → {{ item.targetUser }}</template>
          <Tag v-if="item.outcome === 'Failure'" value="failed" severity="danger" />
        </div>
        <div class="small muted">
          <RelativeTime :value="item.lastAt ?? item.at" /> · {{ item.sourceAddress }}
          <span v-if="item.keyFingerprint" class="mono"> · {{ shortFingerprint(item.keyFingerprint) }}</span>
        </div>
      </template>
    </ListTable>
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

.tag-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}
</style>
