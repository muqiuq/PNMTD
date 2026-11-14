<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import SectionBar from '../agent/SectionBar.vue'
import { useLinuxSection } from '../agent/useAgentSection'
import { rootLogin, sshdNotes, verdictSeverity } from './linuxFormat'

/**
 * Remote access on a Linux server (docs/linux-sensor-plan.md §5.5): sshd's effective settings (`sshd -T`) with what to look at
 * (root with a password, password authentication, forwarding, keys from elsewhere), then every way in: whether it is active and
 * listening, its ports, the firewall's verdict (open, filtered, or unknown where the rules are too complex to judge), its notes;
 * highlighted where it always deserves a look (telnet, Docker's API, third-party remote tools).
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useLinuxSection(id, 'remote-access')
const remote = computed(() => section.data.value?.remoteAccess ?? null)
const sshd = computed(() => remote.value?.sshd ?? null)
const notes = computed(() => (sshd.value ? sshdNotes(sshd.value) : []))
const features = computed(() =>
  [...(remote.value?.features ?? [])].sort(
    (a, b) =>
      Number(b.highlight) - Number(a.highlight) || Number(b.active) - Number(a.active) || a.name.localeCompare(b.name),
  ),
)
const on = (value: boolean) => (value ? 'on' : 'off')
const noteSeverity = (n: string) =>
  /password allowed|password authentication on|no password|without TLS|root-equivalent|SMB1|to \*|not required|v1|v2c/i.test(
    n,
  )
    ? 'warn'
    : 'secondary'
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="remote-access"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <section v-if="remote" class="panel">
      <div class="panel-header">
        OpenSSH server
        <span v-if="sshd?.version" class="muted small mono">{{ sshd.version }}</span>
      </div>
      <div v-if="!sshd" class="panel-body muted">Not installed, or its settings could not be read.</div>
      <div v-else class="panel-body content">
        <div class="tags">
          <Tag v-for="n in notes" :key="n.text" :value="n.text" :severity="n.severity" />
        </div>
        <dl class="facts">
          <dt>Listens on</dt>
          <dd>
            <span class="mono">port {{ sshd.ports.join(', ') }}</span>
            <span class="muted small"> · {{ sshd.listenAddresses.join(', ') || 'every address' }}</span>
            <span v-if="sshd.socketActivated" class="muted small"> · started by ssh.socket</span>
          </dd>
          <dt>PermitRootLogin</dt>
          <dd>
            <span class="mono">{{ sshd.permitRootLogin ?? '–' }}</span>
            <span class="muted small"> · {{ rootLogin(sshd.permitRootLogin).text }}</span>
          </dd>
          <dt>Authentication</dt>
          <dd>
            <span :class="{ warn: sshd.passwordAuthentication }">password {{ on(sshd.passwordAuthentication) }}</span>
            ·
            <span :class="{ warn: sshd.kbdInteractiveAuthentication }"
              >keyboard-interactive {{ on(sshd.kbdInteractiveAuthentication) }}</span
            >
            · public key {{ on(sshd.pubkeyAuthentication) }}
            <span v-if="sshd.maxAuthTries" class="muted small"> · MaxAuthTries {{ sshd.maxAuthTries }}</span>
          </dd>
          <dt>Who may log in</dt>
          <dd>
            <template
              v-if="
                sshd.allowUsers.length || sshd.allowGroups.length || sshd.denyUsers.length || sshd.denyGroups.length
              "
            >
              <div v-if="sshd.allowUsers.length">
                AllowUsers <span class="mono">{{ sshd.allowUsers.join(' ') }}</span>
              </div>
              <div v-if="sshd.allowGroups.length">
                AllowGroups <span class="mono">{{ sshd.allowGroups.join(' ') }}</span>
              </div>
              <div v-if="sshd.denyUsers.length">
                DenyUsers <span class="mono">{{ sshd.denyUsers.join(' ') }}</span>
              </div>
              <div v-if="sshd.denyGroups.length">
                DenyGroups <span class="mono">{{ sshd.denyGroups.join(' ') }}</span>
              </div>
            </template>
            <span v-else class="muted">every account (no AllowUsers or AllowGroups)</span>
          </dd>
          <dt>Forwarding</dt>
          <dd>
            agent {{ on(sshd.allowAgentForwarding) }} · TCP {{ sshd.allowTcpForwarding ?? '–' }} ·
            <span :class="{ warn: sshd.x11Forwarding }">X11 {{ on(sshd.x11Forwarding) }}</span>
          </dd>
          <dt>Keys from elsewhere</dt>
          <dd>
            <div v-if="sshd.authorizedKeysCommand">
              AuthorizedKeysCommand <span class="mono small">{{ sshd.authorizedKeysCommand }}</span>
            </div>
            <div v-if="sshd.trustedUserCaKeys">TrustedUserCAKeys set: certificates of a CA get in</div>
            <span v-if="!sshd.authorizedKeysCommand && !sshd.trustedUserCaKeys" class="muted"
              >none: only the key files</span
            >
          </dd>
          <dt>LogLevel</dt>
          <dd>
            <span class="mono">{{ sshd.logLevel ?? '–' }}</span>
            <span v-if="sshd.matchBlocks" class="muted small"> · Match blocks change settings per user or address</span>
          </dd>
        </dl>
      </div>
    </section>
    <section v-if="remote" class="panel">
      <div class="panel-header">
        {{ remote.summary }}
        <span v-if="remote.firewallKind" class="muted small">· firewall {{ remote.firewallKind }}</span>
      </div>
      <DataTable
        :value="features"
        size="small"
        data-key="key"
        :row-class="(f) => (f.highlight ? 'highlight' : !f.active ? 'row-muted' : '')"
      >
        <Column header="Feature">
          <template #body="{ data }">
            <strong>{{ data.name }}</strong>
            <span v-if="!data.installed" class="muted small"> · not installed</span>
          </template>
        </Column>
        <Column header="Active" style="width: 7rem">
          <template #body="{ data }"
            ><Tag :value="data.active ? 'active' : 'off'" :severity="data.active ? 'warn' : 'secondary'"
          /></template>
        </Column>
        <Column header="Ports" style="width: 10rem">
          <template #body="{ data }">
            <span class="mono small">{{ data.ports.join(', ') || '–' }}</span>
            <span v-if="data.listening" class="small"> · listening</span>
          </template>
        </Column>
        <Column header="Firewall" style="width: 8rem">
          <template #body="{ data }">
            <Tag
              v-if="data.ports.length"
              :value="data.firewall"
              :severity="verdictSeverity(data.firewall)"
              :title="
                data.firewall === 'unknown'
                  ? 'The rules are too complex to judge: PNMT says unknown rather than guess'
                  : undefined
              "
            />
          </template>
        </Column>
        <Column header="Notes">
          <template #body="{ data }">
            <div class="tags">
              <Tag v-if="data.highlight" value="look at it" severity="danger" class="small" />
              <Tag v-for="n in data.notes" :key="n" :value="n" :severity="noteSeverity(n)" class="small" />
            </div>
          </template>
        </Column>
      </DataTable>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.content {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
  font-weight: 600;
}

.tags {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}

:deep(tr.highlight) > td:first-child {
  box-shadow: inset 3px 0 0 var(--pnmt-fail);
}
</style>
