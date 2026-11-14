<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useSession } from '@/stores/session'
import { cookieStateSeverity, overallLabel, overallSeverity, sourceLabel } from '@/views/cookie/cookieFormat'

/**
 * Cookie on the dashboard (docs/cookie-plan.md §9.1), for every signed-in user: the overall state with its reason and
 * the cases behind it (Cookie's while it runs, PNMT's own otherwise), and Cookie's own state. The cases open in Cookie's
 * window for those with Cookie's Use; the server leaves out what the reader may not see.
 */
const session = useSession()
const status = useQuery({
  queryKey: [...keys.cookie, 'status'],
  queryFn: api.cookie.status,
  refetchInterval: 30_000,
})
const s = computed(() => status.data.value)
const canUse = computed(() => session.can('Cookie', 'Use'))
</script>

<template>
  <section v-if="s" class="panel cookie-card" aria-labelledby="cookie-card-title">
    <header class="panel-header">
      <span id="cookie-card-title">Cookie</span>
      <RouterLink v-if="canUse && s.cookie.enabled" :to="{ name: 'cookie' }" class="all"
        >Cases <i class="pi pi-angle-right"
      /></RouterLink>
    </header>
    <div class="body">
      <p v-if="!s.cookie.enabled" class="muted">Cookie is not enabled on this installation.</p>
      <template v-else>
        <div class="line">
          <Tag :value="overallLabel[s.overall.state]" :severity="overallSeverity(s.overall.state)" />
          <span class="muted small"
            >{{ sourceLabel(s.overall.source)
            }}<template v-if="s.overall.at"> · <RelativeTime :value="s.overall.at" /></template
          ></span>
        </div>
        <p v-if="s.overall.reason" class="reason">{{ s.overall.reason }}</p>
        <ul v-if="s.overall.cases.length" class="cases">
          <li v-for="c in s.overall.cases" :key="c.id">
            <RouterLink v-if="canUse" :to="{ name: 'cookie', query: { case: c.id } }">{{ c.title }}</RouterLink>
            <span v-else>{{ c.title }}</span>
          </li>
        </ul>
        <!-- Step B: the personal summary (GET /api/v1/cookie/summary) goes here, with its links to the cases. -->
        <div class="line own small">
          <span class="muted">Cookie itself:</span>
          <Tag :value="s.cookie.state" :severity="cookieStateSeverity(s.cookie.state)" />
          <span v-if="s.cookie.reason" class="muted">{{ s.cookie.reason }}</span>
          <span v-if="s.cookie.state === 'Sleeping' && s.cookie.nextRoundAt" class="muted"
            >· next round <RelativeTime :value="s.cookie.nextRoundAt"
          /></span>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.all {
  margin-left: auto;
  font-weight: 400;
  font-size: 0.929rem;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-2) var(--pnmt-space-3) var(--pnmt-space-3);
}

.body p {
  margin: 0;
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.cases {
  margin: 0;
  padding-left: 1.2rem;
}

.own {
  border-top: 1px solid var(--pnmt-border);
  padding-top: var(--pnmt-space-2);
}

.small {
  font-size: 0.857rem;
}
</style>
