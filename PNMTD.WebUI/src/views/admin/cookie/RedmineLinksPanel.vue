<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Message from 'primevue/message'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { useNotify } from '@/composables/useNotify'
import { isLinkProblem, linkStateLabel, linkStateMeaning, linkStateSeverity, pickedFor } from './cookieAdmin'

/**
 * People and their Redmine users (docs/cookie-redmine-tab-plan.md §4.3): each active PNMT user's Redmine user and how it
 * stands, linked by id from Redmine's users (the members of the projects PNMT shows, and the people on watched tickets),
 * never typed; every exact match at once; and the Redmine users with work in PNMT but no PNMT user, whose reminders go
 * to the escalation group.
 */
const linksKey = [...keys.cookie, 'admin', 'redmine', 'links'] as const
const links = useQuery({ queryKey: linksKey, queryFn: api.cookie.redmineAdmin.links })
const view = computed(() => links.data.value)
const { run, busy } = useActions()
const notify = useNotify()

const problemsOnly = ref(false)
const rows = computed(() => (view.value?.rows ?? []).filter((r) => !problemsOnly.value || isLinkProblem(r)))
const exact = computed(() => (view.value?.rows ?? []).filter((r) => r.state === 'Matches' && !r.shared).length)
const options = computed(() =>
  (view.value?.redmineUsers ?? []).map((u) => ({
    label: `${u.name} (#${u.id})${u.member ? '' : ', on tickets only'}`,
    value: u.id,
  })),
)

/** Each row's picked Redmine user, starting at the linked one or the one match. */
const picked = ref<Record<string, number | null>>({})
watch(
  () => view.value?.rows,
  (list) => {
    picked.value = Object.fromEntries((list ?? []).map((r) => [r.userId, pickedFor(r)]))
  },
  { immediate: true },
)

async function link(row: S['RedmineLinkRow'], redmineUserId: number | null) {
  await run(() => api.cookie.redmineAdmin.link(row.userId, redmineUserId), {
    success: redmineUserId === null ? `${row.name}: unlinked` : `${row.name}: linked`,
    failure: 'Not linked',
    invalidate: [linksKey],
  })
}

async function linkExact() {
  const done = await run(() => api.cookie.redmineAdmin.linkExact(), { failure: 'Not linked', invalidate: [linksKey] })
  if (done) notify.success(done.linked === 1 ? '1 person linked' : `${done.linked} people linked`)
}
</script>

<template>
  <section class="panel" aria-labelledby="redmine-links-title">
    <div id="redmine-links-title" class="panel-header">
      People and their Redmine users
      <span class="end header-actions">
        <label class="switch small"
          ><ToggleSwitch v-model="problemsOnly" input-id="links-problems" aria-label="Problems only" /> Problems
          only</label
        >
        <Button
          :label="exact ? `Link all exact matches (${exact})` : 'Link all exact matches'"
          icon="pi pi-link"
          size="small"
          severity="secondary"
          :disabled="!exact || busy"
          @click="linkExact()"
        />
      </span>
    </div>
    <div class="panel-body body">
      <p class="muted small intro">
        A ticket's reminders and escalations reach its assignee's PNMT user, found by the Redmine user's id once linked,
        otherwise by the name. Redmine's users are the members of the projects PNMT shows and the people on watched
        tickets; Redmine's own user list needs an admin key PNMT doesn't have.
      </p>
      <ErrorState
        v-if="links.isError.value"
        :error="links.error.value"
        what="the Redmine users"
        @retry="links.refetch()"
      />
      <template v-else-if="view">
        <Message v-if="!view.configured" severity="warn" :closable="false" size="small">
          Redmine is not configured: only the names can be compared, with the people on tickets PNMT saw.
        </Message>
        <Message v-if="view.error" severity="warn" :closable="false" size="small">
          Redmine's members couldn't be read: {{ view.error }}
        </Message>
        <Message v-if="view.forbidden.length" severity="warn" :closable="false" size="small">
          Redmine's key may not see the members of {{ view.forbidden.join(', ') }}.
        </Message>
        <p v-if="view.readAt" class="muted small intro">
          Redmine's members read <RelativeTime :value="view.readAt" /><template v-if="view.reading"
            >, reading again…</template
          ><template v-if="view.groups">; {{ view.groups }} groups among them can't be linked</template>.
        </p>

        <DataTable :value="rows" size="small" class="table" data-key="userId">
          <template #empty>{{ problemsOnly ? 'No problems.' : 'No active users.' }}</template>
          <Column header="Person">
            <template #body="{ data }">
              {{ data.name }}
              <div v-if="!data.inPlan" class="muted small">Not in the availability plan</div>
            </template>
          </Column>
          <Column header="Redmine user">
            <template #body="{ data }">
              <template v-if="data.redmineUserId != null">
                {{ data.redmineNow ?? data.redmineName ?? '?' }} (#{{ data.redmineUserId }})
              </template>
              <template v-else-if="data.redmineName">
                “{{ data.redmineName }}”
                <span v-if="data.candidates.length > 1" class="muted small">
                  : {{ data.candidates.map((c: S['RedmineNamed']) => `#${c.id}`).join(', ') }}</span
                >
              </template>
              <span v-else class="muted">–</span>
            </template>
          </Column>
          <Column header="State">
            <template #body="{ data }">
              <Tag
                :value="linkStateLabel[data.state as S['RedmineLinkState']]"
                :severity="linkStateSeverity(data.state)"
              />
              <Tag v-if="data.shared" value="shared" severity="danger" class="shared" />
              <div class="muted small">
                {{
                  data.shared
                    ? 'Another person has the same Redmine user: which one counts is chance.'
                    : linkStateMeaning[data.state as S['RedmineLinkState']]
                }}
              </div>
            </template>
          </Column>
          <Column header="Open tickets" style="width: 7rem">
            <template #body="{ data }">{{ data.openTickets || '' }}</template>
          </Column>
          <Column header="Link" style="width: 22rem">
            <template #body="{ data }">
              <div class="link-cell">
                <Select
                  v-model="picked[data.userId]"
                  :options="options"
                  option-label="label"
                  option-value="value"
                  filter
                  size="small"
                  placeholder="Pick a Redmine user"
                  class="picker"
                  :aria-label="`Redmine user of ${data.name}`"
                />
                <Button
                  :label="data.redmineUserId != null ? 'Change' : 'Link'"
                  size="small"
                  :disabled="picked[data.userId] == null || picked[data.userId] === data.redmineUserId || busy"
                  @click="link(data, picked[data.userId] ?? null)"
                />
                <Button
                  v-if="data.redmineUserId != null || data.redmineName"
                  icon="pi pi-times"
                  size="small"
                  severity="secondary"
                  text
                  :aria-label="`Unlink ${data.name}`"
                  title="Unlink: no Redmine user"
                  :disabled="busy"
                  @click="link(data, null)"
                />
              </div>
            </template>
          </Column>
        </DataTable>

        <div v-if="view.unlinked.length" class="unlinked">
          <h3>Redmine users without a PNMT user</h3>
          <p class="muted small intro">
            They have open watched tickets or a staff role: their reminders go to the escalation group, or whoever is
            responsible.
          </p>
          <ul>
            <li v-for="u in view.unlinked" :key="u.id">
              {{ u.name }} (#{{ u.id }})<span v-if="u.openTickets" class="muted"
                >, {{ u.openTickets }} open {{ u.openTickets === 1 ? 'ticket' : 'tickets' }}</span
              ><span v-if="u.staff" class="muted">, staff</span>
            </li>
          </ul>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.intro {
  margin: 0;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
}

.end {
  margin-left: auto;
}

.switch {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  font-weight: normal;
}

.link-cell {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.picker {
  flex: 1;
  min-width: 0;
}

.shared {
  margin-left: var(--pnmt-space-1);
}

.unlinked h3 {
  font-size: 1rem;
}

.unlinked ul {
  margin: var(--pnmt-space-1) 0 0;
  padding-left: 1.25rem;
}

.small {
  font-size: 0.857rem;
}
</style>
