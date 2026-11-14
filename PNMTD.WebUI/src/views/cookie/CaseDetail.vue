<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import CodeTag from '@/components/CodeTag.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import { importanceSeverity } from '@/views/notifications/importance'
import ZabbixSeverityTag from '@/views/zabbix/ZabbixSeverityTag.vue'
import { actorLabel, dutyName, dutyStateName, dutyStateSeverity } from './cookieFormat'

/**
 * A case (docs/cookie-plan.md §4.3, §7): what is bad in it (a host's bad sensors and its Zabbix problems), its timeline,
 * its duties with what came of them, and for a ticket the clock of §7.2. Refreshed with the list.
 */
const props = defineProps<{ id: string }>()
const session = useSession()
const detail = useQuery({
  queryKey: computed(() => [...keys.cookie, 'case', props.id]),
  queryFn: () => api.cookie.case(props.id),
  refetchInterval: 30_000,
})
const d = computed(() => detail.data.value)
const c = computed(() => d.value?.case)
const canMonitoring = computed(() => session.can('Monitoring', 'View'))
const canLocations = computed(() => session.can('Locations', 'View'))
const canRedmine = computed(() => session.can('Redmine', 'View'))

const zabbixSeverity = (m: S['CaseMember']) => m.severity as S['ZabbixSeverity']
</script>

<template>
  <ErrorState v-if="detail.isError.value && !d" :error="detail.error.value" what="the case" @retry="detail.refetch()" />
  <div v-else-if="!d || !c" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <template v-else>
    <div class="panel-header">
      <Tag :value="c.kind" severity="secondary" />
      <Tag :value="c.open ? 'open' : 'closed'" :severity="c.open ? 'info' : 'secondary'" />
      <Tag :value="c.importance" :severity="importanceSeverity(c.importance)" />
    </div>
    <div class="panel-body body">
      <h2 class="title">{{ c.title }}</h2>
      <p v-if="c.cookieNote" class="note">{{ c.cookieNote }}</p>
      <dl class="facts">
        <dt>Opened</dt>
        <dd>{{ formatAbsolute(c.openedAt) }}</dd>
        <template v-if="c.closedAt">
          <dt>Closed</dt>
          <dd>{{ formatAbsolute(c.closedAt) }}</dd>
        </template>
        <dt>Last change</dt>
        <dd><RelativeTime :value="c.changedAt" /></dd>
        <dt>Last progress</dt>
        <dd><RelativeTime :value="c.progressAt" /></dd>
        <template v-if="c.nextDueAt">
          <dt>Next deadline</dt>
          <dd>{{ dutyName(c.nextDue) }}, {{ formatAbsolute(c.nextDueAt) }}</dd>
        </template>
        <template v-if="c.hostId || c.locationId">
          <dt>Where</dt>
          <dd class="links">
            <RouterLink v-if="c.hostId && canMonitoring" :to="{ name: 'host', params: { id: c.hostId } }"
              >The host's page</RouterLink
            >
            <RouterLink v-if="c.locationId && canLocations" :to="{ name: 'location', params: { id: c.locationId } }"
              >The location's page</RouterLink
            >
          </dd>
        </template>
        <template v-if="d.ownIssueUrl">
          <dt>Cookie's ticket</dt>
          <dd>
            <a :href="d.ownIssueUrl" target="_blank" rel="noopener">{{ d.ownIssueUrl }}</a>
          </dd>
        </template>
      </dl>

      <template v-if="d.ticket">
        <h3 class="sub">The ticket's clock</h3>
        <dl class="facts">
          <dt>Ticket</dt>
          <dd class="links">
            <RouterLink v-if="canRedmine" :to="{ name: 'redmine-issue', params: { id: d.ticket.issueId } }"
              >#{{ d.ticket.issueId }}</RouterLink
            >
            <span v-else>#{{ d.ticket.issueId }}</span>
            <a v-if="d.ticket.issueUrl" :href="d.ticket.issueUrl" target="_blank" rel="noopener"
              >in Redmine <i class="pi pi-external-link" aria-hidden="true"
            /></a>
          </dd>
          <dt>Author</dt>
          <dd>
            {{ d.ticket.author ?? '–' }}
            <Tag v-if="d.ticket.authorIsStaff" value="staff" severity="secondary" />
          </dd>
          <dt>Assignee</dt>
          <dd>{{ d.ticket.assignee ?? 'nobody' }}</dd>
          <dt>Status</dt>
          <dd>{{ d.ticket.status ?? '–' }}</dd>
          <dt>Created</dt>
          <dd>{{ formatAbsolute(d.ticket.createdAt) }}</dd>
          <dt>First answer</dt>
          <dd>
            <template v-if="d.ticket.firstAnswerAt">{{ formatAbsolute(d.ticket.firstAnswerAt) }}</template>
            <span v-else-if="d.ticket.authorIsStaff" class="muted">not needed: written by staff</span>
            <span v-else class="muted">none yet</span>
          </dd>
          <dt>Last staff update</dt>
          <dd>{{ formatAbsolute(d.ticket.lastStaffUpdateAt) }}</dd>
        </dl>
      </template>

      <template v-if="d.members.length">
        <h3 class="sub">What is bad</h3>
        <DataTable :value="d.members" size="small" class="table">
          <Column header="What">
            <template #body="{ data }">
              <span class="what">
                <i
                  :class="data.type === 'zabbix' ? 'pi pi-chart-bar' : 'pi pi-bolt'"
                  :title="data.type === 'zabbix' ? 'Zabbix problem' : 'Sensor'"
                  aria-hidden="true"
                />
                <RouterLink
                  v-if="data.type === 'sensor' && canMonitoring"
                  :to="{ name: 'sensor', params: { id: data.id } }"
                  >{{ data.name }}</RouterLink
                >
                <span v-else>{{ data.name }}</span>
              </span>
              <div v-if="data.message" class="muted small wrap">{{ data.message }}</div>
            </template>
          </Column>
          <Column header="Code / severity" style="width: 9rem">
            <template #body="{ data }">
              <ZabbixSeverityTag v-if="data.type === 'zabbix' && data.severity" :severity="zabbixSeverity(data)" />
              <CodeTag v-else-if="data.code !== null && data.code !== undefined" :code="data.code" />
              <span v-else class="muted">–</span>
            </template>
          </Column>
          <Column header="Since" style="width: 6.5rem">
            <template #body="{ data }"><RelativeTime :value="data.since" /></template>
          </Column>
          <Column header="Acknowledged" style="width: 7.5rem">
            <template #body="{ data }">
              <template v-if="data.type === 'zabbix'">{{ data.acknowledged ? 'yes' : 'no' }}</template>
              <span v-else class="muted">–</span>
            </template>
          </Column>
        </DataTable>
      </template>

      <template v-if="d.duties.length">
        <h3 class="sub">Duties</h3>
        <DataTable :value="d.duties" size="small" data-key="id" class="table">
          <Column header="Duty">
            <template #body="{ data }">{{ dutyName(data.kind) }}</template>
          </Column>
          <Column header="Due" style="width: 9.5rem">
            <template #body="{ data }">{{ formatAbsolute(data.dueAt) }}</template>
          </Column>
          <Column header="State" style="width: 7rem">
            <template #body="{ data }">
              <Tag :value="dutyStateName(data.state)" :severity="dutyStateSeverity(data.state)" />
            </template>
          </Column>
          <Column header="Outcome">
            <template #body="{ data }">
              <span class="small wrap">{{ data.outcome ?? '' }}</span>
              <div v-if="data.doneAt" class="muted small"><RelativeTime :value="data.doneAt" /></div>
            </template>
          </Column>
        </DataTable>
      </template>

      <h3 class="sub">Timeline</h3>
      <p v-if="!d.entries.length" class="muted">Nothing yet.</p>
      <ol v-else class="timeline">
        <li v-for="(e, i) in d.entries" :key="i">
          <div class="entry-head small">
            <span class="nowrap">{{ formatAbsolute(e.at) }}</span>
            <Tag :value="e.kind" severity="secondary" />
            <span class="muted">{{ actorLabel(e) }}</span>
          </div>
          <div class="wrap">{{ e.text }}</div>
        </li>
      </ol>
    </div>
  </template>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.title {
  font-size: 1.1rem;
  margin: 0;
}

.note {
  margin: 0;
}

.sub {
  font-size: 0.93rem;
  margin: var(--pnmt-space-3) 0 0;
}

.links {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  align-items: center;
}

.table {
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  overflow: hidden;
}

.what {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.what i {
  color: var(--pnmt-muted);
  font-size: 0.85rem;
}

.wrap {
  white-space: pre-wrap;
  word-break: break-word;
}

.timeline {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.timeline li {
  border-left: 2px solid var(--pnmt-border);
  padding-left: var(--pnmt-space-2);
}

.entry-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  margin-bottom: 2px;
}

.small {
  font-size: 0.857rem;
}
</style>
