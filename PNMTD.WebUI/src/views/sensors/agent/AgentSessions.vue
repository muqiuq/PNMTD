<script setup lang="ts">
import Tag from 'primevue/tag'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'

/**
 * Who is logged on to a server now (docs/windows-sensor-plan.md §4.7; docs/linux-sensor-plan.md §5.2), on the overview and the
 * Access tab of either platform: one row per session with the account (a Windows domain or computer muted), how (console,
 * Remote Desktop, SSH, graphical) and its state, since when and from where; then the last 24 hours' logins. Linux adds the
 * elevations (sudo, su) and the internet's scanners, counted apart.
 */
defineProps<{
  sessions: S['SessionInfo'][]
  last24Hours?: S['AccessCounts'] | null
  elevations?: number | null
  scanners?: number | null
}>()

const split = (user: string) => {
  const slash = user.lastIndexOf('\\')
  return slash < 0 ? { domain: '', name: user } : { domain: user.slice(0, slash + 1), name: user.slice(slash + 1) }
}
const kindLabel = (kind: string) =>
  kind === 'Rdp' ? 'Remote Desktop' : kind.toLowerCase() === 'ssh' ? 'SSH' : kind.toLowerCase()
const kindIcon = (kind: string) =>
  kind === 'Rdp' || kind === 'Remote' ? 'pi pi-globe' : kind.toLowerCase() === 'ssh' ? 'pi pi-code' : 'pi pi-desktop'
const stateSeverity = (state: string) =>
  state.toLowerCase() === 'active' ? 'success' : state === 'Disconnected' || state === 'closing' ? 'warn' : 'secondary'
</script>

<template>
  <ul class="sessions">
    <li v-for="(s, i) in sessions" :key="i" class="session">
      <span class="icon" :title="kindLabel(s.kind)"><i :class="kindIcon(s.kind)" /></span>
      <div class="who">
        <div class="name">
          <span class="muted">{{ split(s.user).domain }}</span><strong>{{ split(s.user).name }}</strong>
        </div>
        <div class="muted small">
          {{ kindLabel(s.kind) }}
          <template v-if="s.logonAt"> · since <RelativeTime :value="s.logonAt" /></template>
          <template v-if="s.clientAddress">
            · from <span class="mono">{{ s.clientAddress }}</span></template
          >
        </div>
      </div>
      <Tag :value="s.state.toLowerCase()" :severity="stateSeverity(s.state)" />
    </li>
    <li v-if="!sessions.length" class="muted nobody">Nobody.</li>
  </ul>
  <div v-if="last24Hours" class="day">
    <div class="day-title muted small">Last 24 hours</div>
    <div class="stats">
      <div class="stat">
        <span class="value">{{ last24Hours.successes }}</span>
        <span class="muted small">logins</span>
      </div>
      <div class="stat">
        <span class="value">{{ last24Hours.adminLogins }}</span>
        <span class="muted small">by admins</span>
      </div>
      <div class="stat" :class="{ warn: last24Hours.failures > 0 }">
        <span class="value">{{ last24Hours.failures }}</span>
        <span class="muted small">failed</span>
      </div>
      <div class="stat" :class="{ fail: last24Hours.lockouts > 0 }">
        <span class="value">{{ last24Hours.lockouts }}</span>
        <span class="muted small">lockouts</span>
      </div>
      <div v-if="elevations !== null && elevations !== undefined" class="stat">
        <span class="value">{{ elevations }}</span>
        <span class="muted small">sudo and su</span>
      </div>
      <div
        v-if="scanners !== null && scanners !== undefined"
        class="stat"
        title="Failed logins for accounts that don't exist"
      >
        <span class="value">{{ scanners }}</span>
        <span class="muted small">scanners</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.sessions {
  list-style: none;
  margin: 0;
  padding: 0;
}

.session {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-2) 0;
}

.session + .session {
  border-top: 1px solid var(--pnmt-border);
}

.icon {
  flex: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2rem;
  height: 2rem;
  border-radius: 50%;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  color: var(--pnmt-muted);
}

.who {
  flex: 1;
  min-width: 0;
}

.name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.nobody {
  padding: var(--pnmt-space-1) 0;
}

.day {
  margin-top: var(--pnmt-space-3);
  padding-top: var(--pnmt-space-3);
  border-top: 1px solid var(--pnmt-border);
}

.day-title {
  margin-bottom: var(--pnmt-space-2);
}

.stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(5.5rem, 1fr));
  gap: var(--pnmt-space-2);
}

.stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-bg);
}

.stat .value {
  font-size: 1.25rem;
  font-weight: 600;
  line-height: 1.2;
}

.stat.warn {
  border-color: var(--pnmt-warn);
  background: var(--pnmt-warn-soft);
}

.stat.fail {
  border-color: var(--pnmt-fail);
  background: var(--pnmt-fail-soft);
}

.small {
  font-size: 0.857rem;
}
</style>
