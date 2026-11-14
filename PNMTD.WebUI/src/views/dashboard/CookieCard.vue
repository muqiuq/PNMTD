<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'
import { cookieStateSeverity, overallLabel, overallSeverity, sourceLabel } from '@/views/cookie/cookieFormat'

/**
 * Cookie on the dashboard (docs/cookie-plan.md §9.1), for every signed-in user while Cookie is enabled: the overall state
 * with its reason and the cases behind it (Cookie's while it runs, PNMT's own otherwise), Cookie's own state, and the
 * reader's personal summary, what needs them first. The cases open in Cookie's window for those with Cookie's Use; the
 * server leaves out what the reader may not see. Without Cookie__Enabled, nothing shows.
 */
const session = useSession()
const status = useQuery({
  queryKey: [...keys.cookie, 'status'],
  queryFn: api.cookie.status,
  refetchInterval: 30_000,
})
const s = computed(() => (status.data.value?.cookie.enabled ? status.data.value : undefined))
// Written when asked and kept for 15 minutes on the server: asked again every 5.
const summary = useQuery({
  queryKey: [...keys.cookie, 'summary'],
  queryFn: api.cookie.summary,
  refetchInterval: 5 * 60_000,
  staleTime: 60_000,
  enabled: computed(() => !!s.value),
})
const lines = computed(() => summary.data.value?.lines ?? [])
const canUse = computed(() => session.can('Cookie', 'Use'))
const caseLink = (id: string) => ({ name: 'cookie', query: { case: id } })
</script>

<template>
  <section v-if="s" class="panel cookie-card" aria-labelledby="cookie-card-title">
    <header class="panel-header">
      <span id="cookie-card-title">Cookie</span>
      <Tag :value="s.cookie.state" :severity="cookieStateSeverity(s.cookie.state)" class="own" />
      <span v-if="s.cookie.reason" class="muted normal small ellipsis" :title="s.cookie.reason">{{
        s.cookie.reason
      }}</span>
      <span v-else-if="s.cookie.state === 'Sleeping' && s.cookie.nextRoundAt" class="muted normal small"
        >next round <RelativeTime :value="s.cookie.nextRoundAt"
      /></span>
      <RouterLink v-if="canUse" :to="{ name: 'cookie' }" class="all">Open <i class="pi pi-angle-right" /></RouterLink>
    </header>
    <div class="body">
      <div class="line">
        <Tag :value="overallLabel[s.overall.state]" :severity="overallSeverity(s.overall.state)" />
        <span v-if="s.overall.reason" class="reason">{{ s.overall.reason }}</span>
        <span class="muted small"
          >{{ sourceLabel(s.overall.source)
          }}<template v-if="s.overall.at"> · <RelativeTime :value="s.overall.at" /></template
        ></span>
      </div>
      <div v-if="s.overall.cases.length" class="cases small">
        <template v-for="(c, i) in s.overall.cases" :key="c.id"
          ><template v-if="i">, </template><RouterLink v-if="canUse" :to="caseLink(c.id)">{{ c.title }}</RouterLink
          ><span v-else>{{ c.title }}</span></template
        >
      </div>
      <div v-if="lines.length" class="summary" aria-label="Your summary">
        <div class="muted small">
          For you, {{ summary.data.value?.source === 'cookie' ? 'by Cookie' : 'by PNMT' }} ·
          <RelativeTime :value="summary.data.value?.at" />
        </div>
        <ul>
          <li v-for="(l, i) in lines" :key="i" :class="{ needs: l.needsYou }">
            <Tag v-if="l.needsYou" value="needs you" severity="warn" class="needs-tag" />
            <RouterLink v-if="l.caseId && canUse" :to="caseLink(l.caseId)">{{ l.text }}</RouterLink>
            <span v-else>{{ l.text }}</span>
          </li>
        </ul>
      </div>
    </div>
  </section>
</template>

<style scoped>
.all {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
  white-space: nowrap;
}

.normal {
  font-weight: 400;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.summary {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
}

.summary ul {
  margin: var(--pnmt-space-1) 0 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.needs {
  font-weight: 500;
}

.needs-tag {
  margin-right: var(--pnmt-space-1);
  font-size: 0.75rem;
}

.small {
  font-size: 0.857rem;
}
</style>
