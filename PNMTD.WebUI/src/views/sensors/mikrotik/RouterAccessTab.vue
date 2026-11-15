<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'
import AgentLoginsList from '../agent/AgentLoginsList.vue'

/**
 * A router's Access tab (docs/cookie-access-plan.md §4.2, §10.4), for Monitoring: Edit: the switch Cookie watches this
 * router's logins (Cookie: Manage changes it, the others see it), whether its log may be missing lines, who is logged in now,
 * its users, and its logins read from the log with the access watch's verdicts. With the switch off, the logins are still
 * read and shown, and nothing else happens.
 */
const props = defineProps<{ sensor: S['SensorDetail'] }>()
const viewKey = computed(() => [...keys.sensor(props.sensor.id), 'mikrotik-access'])
const view = useQuery({
  queryKey: viewKey,
  queryFn: () => api.mikrotik.logins(props.sensor.id, { pageSize: 1 }),
  refetchInterval: 30_000,
})
const v = computed(() => view.data.value)
const { done, ask, busy } = useActions()

async function setWatch(watch: boolean) {
  if (!watch) {
    const sure = await ask(
      `Stop watching ${props.sensor.host.name}?`,
      "Its logins are still read and shown here, but they open no case, send no notice and ask nobody. The change is in the audit log, and Admin → Cookie → Access lists the routers that aren't watched.",
      'Stop watching',
    )
    if (!sure) return
  }
  await done(() => api.mikrotik.setAccessWatch(props.sensor.id, watch), {
    success: watch ? 'Cookie watches its logins' : "Cookie doesn't watch its logins",
    failure: 'Not switched',
    invalidate: [viewKey.value, keys.cookie],
  })
}
</script>

<template>
  <div class="tab">
    <ErrorState v-if="view.isError.value && !v" :error="view.error.value" what="the logins" @retry="view.refetch()" />
    <div v-else-if="!v" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <section class="panel" aria-labelledby="router-watch-title">
        <div id="router-watch-title" class="panel-header">
          Cookie's watch
          <Tag :value="v.watch ? 'on' : 'off'" :severity="v.watch ? 'success' : 'warn'" />
        </div>
        <div class="panel-body body">
          <div class="switch">
            <ToggleSwitch
              :model-value="v.watch"
              input-id="router-watch"
              :disabled="!v.canSwitch || busy"
              @update:model-value="setWatch"
            />
            <div>
              <label for="router-watch" class="label">Cookie watches this router's logins</label>
              <div class="muted small">
                A new login is compared with the router's last days; what is new goes to Cookie, and the contacts under
                Admin → Cookie → Access are told as its verdict says. Off: its logins are read and shown, nothing more.
                <template v-if="!v.canSwitch"> Changing it needs Cookie: Manage.</template>
              </div>
            </div>
          </div>
          <Message v-if="v.gap" severity="warn" :closable="false" size="small">
            Lines may be missing: the oldest line of the last read is newer than the poll before it. A busy router can
            push a login out of its memory log between two polls.
          </Message>
          <Message v-if="v.unmatched > 0" severity="info" :closable="false" size="small">
            {{ v.unmatched }} {{ v.unmatched === 1 ? 'line' : 'lines' }} of the log's account topic matched no known
            pattern, and {{ v.unmatched === 1 ? 'was' : 'were' }} counted, not read as a login.
          </Message>
          <dl class="facts">
            <dt>First read</dt>
            <dd>
              <template v-if="v.firstReadAt">{{ formatAbsolute(v.firstReadAt) }}</template>
              <span v-else class="muted">not yet: the next poll reads the log</span>
              <div class="muted small">The learning period counts from here: only strong signals count in it.</div>
            </dd>
            <template v-if="v.pnmtAddress">
              <dt>PNMT's address</dt>
              <dd>
                <span class="mono">{{ v.pnmtAddress }}</span>
                <div class="muted small">PNMT's own poll and jobs log in from it, and are never reviewed.</div>
              </dd>
            </template>
          </dl>
        </div>
      </section>

      <div class="two-columns">
        <section class="panel" aria-labelledby="router-active-title">
          <div id="router-active-title" class="panel-header">Logged in now</div>
          <div class="panel-body">
            <p v-if="!v.active.length" class="muted none">Nobody, as of the last poll.</p>
            <ul v-else class="list">
              <li v-for="(a, i) in v.active" :key="i">
                <strong>{{ a.name }}</strong>
                <span v-if="a.via" class="muted"> by {{ a.via }}</span>
                <span v-if="a.address" class="mono small"> from {{ a.address }}</span>
                <span v-if="a.when" class="muted small"> · since {{ a.when }}</span>
              </li>
            </ul>
          </div>
        </section>
        <section class="panel" aria-labelledby="router-users-title">
          <div id="router-users-title" class="panel-header">Users</div>
          <div class="panel-body">
            <p v-if="!v.users.length" class="muted none">None read yet.</p>
            <ul v-else class="list">
              <li v-for="u in v.users" :key="u.name" :class="{ 'row-muted': u.disabled }">
                <strong>{{ u.name }}</strong>
                <span v-if="u.group" class="muted"> · {{ u.group }}</span>
                <Tag v-if="u.admin" value="admin" severity="warn" class="tag" />
                <Tag v-if="u.disabled" value="disabled" severity="secondary" class="tag" />
              </li>
            </ul>
            <p class="muted small hint">An admin is a user whose group has the write or policy policy.</p>
          </div>
        </section>
      </div>
    </template>
    <AgentLoginsList :router-id="sensor.id" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6);
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.switch {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-3);
}

.switch > :first-child {
  flex: none;
}

.label {
  font-weight: 500;
}

.none,
.hint {
  margin: 0;
}

.hint {
  margin-top: var(--pnmt-space-2);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.tag {
  margin-left: var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
