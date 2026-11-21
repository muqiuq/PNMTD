<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { skewText, uptime } from '@/views/admin/servers/agentFormat'
import SectionBar from '../agent/SectionBar.vue'
import { useLinuxSection } from '../agent/useAgentSection'

/**
 * The system section on a Linux server's overview (docs/linux-sensor-plan.md §5.2, §11.2): the host, the distribution (with
 * the not a supported version badge), the kernel, uptime and the last shutdown, updates with the security ones and a pending
 * reboot (linking the OS updates tab, docs/linux-os-updates-plan.md §10.1), time, the firewall, AppArmor, Secure Boot, LUKS,
 * fail2ban; then the administrators with how each is one, the sudo rules giving ALL, and the accounts with whether a password
 * is set or locked (never the hash).
 */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useLinuxSection(id, 'system')
const sys = computed(() => section.data.value?.system ?? null)
const security = computed(() => sys.value?.security ?? null)

/** Each administrator once, with every way it is one: "deploy: group sudo, sudoers". */
const admins = computed(() => {
  const by = new Map<string, string[]>()
  for (const a of sys.value?.administrators ?? []) by.set(a.name, [...(by.get(a.name) ?? []), a.via])
  return [...by.entries()].map(([name, via]) => ({ name, via }))
})
/** People first, system accounts after them (muted). */
const accounts = computed(() =>
  [...(sys.value?.accounts ?? [])].sort((a, b) => Number(a.systemAccount) - Number(b.systemAccount) || a.uid - b.uid),
)
const password = (a: S['LinuxAccount']): { label: string; severity: 'warn' | 'secondary' | 'info' } | null =>
  a.locked
    ? { label: 'locked', severity: 'secondary' }
    : a.passwordSet
      ? { label: 'password set', severity: 'warn' }
      : a.passwordSet === false
        ? { label: 'no password', severity: 'info' }
        : null
const containers = computed(() =>
  (sys.value?.containers ?? [])
    .filter((c) => c.installed)
    .map((c) => `${c.name}${c.running ? ' (running)' : ''}`)
    .join(', '),
)
</script>

<template>
  <section class="panel">
    <div class="panel-header">System</div>
    <div class="panel-body">
      <SectionBar
        :sensor-id="id"
        section="system"
        :view="section.data.value"
        :loading="section.loading.value"
        :error="section.error.value"
        :can-write="canWrite"
        :connected="server.server.connected"
      />
      <dl v-if="sys" class="facts">
        <dt>Hostname</dt>
        <dd>
          <span class="mono">{{ sys.hostname }}</span>
          <span v-if="sys.fqdn && sys.fqdn !== sys.hostname" class="muted mono"> · {{ sys.fqdn }}</span>
          <div v-if="sys.realm" class="muted small">realm {{ sys.realm }}</div>
        </dd>
        <dt>Distribution</dt>
        <dd>
          {{ sys.os.distribution ?? sys.os.prettyName }}
          <span v-if="sys.os.codename" class="muted">({{ sys.os.codename }})</span>
          <span v-if="!sys.os.supported" class="tags"><Tag value="not a supported version" severity="warn" /></span>
          <div v-if="sys.os.distribution && sys.os.distribution !== sys.os.prettyName" class="muted small">
            {{ sys.os.prettyName }}
          </div>
        </dd>
        <template v-if="sys.proxmox">
          <dt>Proxmox VE</dt>
          <dd>
            {{ sys.proxmox.version ?? '–' }}
            <span v-if="sys.proxmox.clusterName" class="muted">
              · cluster {{ sys.proxmox.clusterName }} ({{ sys.proxmox.nodes.join(', ') }})</span
            >
          </dd>
        </template>
        <dt>Kernel</dt>
        <dd>
          <span class="mono">{{ sys.os.kernelRelease }}</span> <span class="muted">{{ sys.os.architecture }}</span>
          <div v-if="sys.os.systemdVersion" class="muted small">systemd {{ sys.os.systemdVersion }}</div>
        </dd>
        <dt>Uptime</dt>
        <dd>
          {{ uptime(sys.bootTime) ?? '–' }} <span class="muted">since <RelativeTime :value="sys.bootTime" /></span>
          <div v-if="sys.lastShutdown?.at" class="small">
            <template v-if="sys.lastShutdown.clean === false"
              ><span class="warn">unexpected shutdown</span> before <RelativeTime :value="sys.lastShutdown.at"
            /></template>
            <template v-else-if="sys.lastShutdown.clean"
              ><span class="muted">clean shutdown <RelativeTime :value="sys.lastShutdown.at" /></span
            ></template>
            <template v-else
              ><span class="muted"
                >shut down <RelativeTime :value="sys.lastShutdown.at" />, clean or not unknown</span
              ></template
            >
          </div>
        </dd>
        <dt>Updates</dt>
        <dd>
          <template v-if="sys.updates">
            <div class="row">
              <span>{{ sys.updates.upgradable }} pending</span>
              <Tag
                v-if="sys.updates.security"
                :value="`${sys.updates.security} security`"
                severity="danger"
                :title="sys.updates.securityPackages.join(', ')"
              />
              <Tag
                v-if="sys.updates.rebootPending"
                value="reboot pending"
                severity="warn"
                :title="sys.updates.rebootReasons.join('; ')"
              />
              <RouterLink :to="{ name: 'sensor', params: { id, tab: 'os-updates' } }" class="small"
                >OS updates tab</RouterLink
              >
            </div>
            <div v-if="sys.updates.securityPackages.length" class="mono small muted">
              {{ sys.updates.securityPackages.join(', ')
              }}<template v-if="sys.updates.security > sys.updates.securityPackages.length">, …</template>
            </div>
            <div v-if="sys.updates.rebootReasons.length" class="small muted">
              {{ sys.updates.rebootReasons.join('; ') }}
            </div>
            <div v-if="server.securityUpdatesSince" class="small muted">
              security updates pending since <RelativeTime :value="server.securityUpdatesSince" />
            </div>
            <div class="small muted">
              <template v-if="sys.updates.listsUpdatedAt"
                >lists refreshed <RelativeTime :value="sys.updates.listsUpdatedAt" /> · </template
              >unattended-upgrades
              {{
                !sys.updates.unattendedInstalled
                  ? 'not installed'
                  : sys.updates.unattendedEnabled
                    ? 'on'
                    : 'installed, off'
              }}<template v-if="sys.updates.unattendedLastRun">
                (last run <RelativeTime :value="sys.updates.unattendedLastRun" />)</template
              ><template v-if="sys.updates.esmEnabled"> · Ubuntu Pro ESM on</template>
            </div>
          </template>
          <template v-else>–</template>
        </dd>
        <dt>Time</dt>
        <dd>
          <template v-if="sys.time">
            {{ sys.time.timeZone }} ·
            <span :class="{ warn: sys.time.ntpSynchronized === false }">{{
              sys.time.ntpSynchronized
                ? 'NTP synchronised'
                : sys.time.ntpSynchronized === false
                  ? 'NTP not synchronised'
                  : 'NTP unknown'
            }}</span>
            <span v-if="sys.time.service" class="muted"> ({{ sys.time.service }})</span>
          </template>
          <template v-else>–</template>
          <div class="small" :class="skewText(server.server.clockSkewSeconds) ? 'warn' : 'muted'">
            clock {{ skewText(server.server.clockSkewSeconds) ?? 'right' }} against PNMT
          </div>
        </dd>
        <dt>Virtualisation</dt>
        <dd>
          {{ sys.virtualization ?? 'none' }}
          <span v-if="containers" class="muted"> · containers: {{ containers }}</span>
        </dd>
        <dt>Firewall</dt>
        <dd>
          <template v-if="security?.firewall">
            <span class="line">
              <span>{{ security.firewall.kind }}</span>
              <Tag
                :value="security.firewall.active ? 'active' : 'inactive'"
                :severity="security.firewall.active ? 'success' : 'danger'"
              />
              <span v-if="security.firewall.inputPolicy" class="muted"
                >input policy {{ security.firewall.inputPolicy }}</span
              >
            </span>
            <div v-for="n in security.firewall.notes" :key="n" class="muted small">{{ n }}</div>
          </template>
          <Tag v-else value="none found" severity="warn" />
        </dd>
        <dt>AppArmor</dt>
        <dd>
          <template v-if="security?.appArmor">
            <Tag
              :value="security.appArmor.enabled ? 'on' : 'off'"
              :severity="security.appArmor.enabled ? 'success' : 'warn'"
            />
            <span v-if="security.appArmor.enabled" class="muted">
              {{ security.appArmor.enforcing }} profiles enforcing,
              {{ security.appArmor.complaining }} complaining</span
            >
          </template>
          <template v-else>–</template>
        </dd>
        <dt>Secure Boot</dt>
        <dd>{{ security?.secureBoot === null || !security ? '–' : security.secureBoot ? 'on' : 'off' }}</dd>
        <dt>Encryption</dt>
        <dd>
          <div v-for="v in security?.encrypted ?? []" :key="v.device">
            {{ v.kind }} <span class="mono small">{{ v.device }}</span>
            <span v-if="v.mount" class="muted"> on {{ v.mount }}</span>
          </div>
          <template v-if="!security?.encrypted.length">none</template>
        </dd>
        <dt>fail2ban</dt>
        <dd>
          <template v-if="security?.fail2ban">
            {{ security.fail2ban.running ? 'running' : 'not running' }}
            <div v-for="j in security.fail2ban.jails" :key="j.name" class="small muted">
              jail {{ j.name }}: {{ j.banned }} banned now, {{ j.totalBanned }} in all
            </div>
          </template>
          <template v-else>not installed</template>
        </dd>
      </dl>
    </div>
  </section>
  <section v-if="sys" class="panel">
    <div class="panel-header">Administrators and accounts</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Administrators</dt>
        <dd>
          <div v-for="a in admins" :key="a.name">
            <strong>{{ a.name }}</strong> <span class="muted small">via {{ a.via.join(', ') }}</span>
          </div>
          <template v-if="!admins.length">–</template>
        </dd>
        <dt>sudo rules giving ALL</dt>
        <dd>
          <div v-for="r in sys.sudoRules" :key="`${r.file}:${r.line}`" class="row">
            <span class="mono">{{ r.who }}</span>
            <span class="muted small mono">{{ r.file }}:{{ r.line }}</span>
            <Tag v-if="r.noPassword" value="NOPASSWD" severity="warn" class="small" />
          </div>
          <template v-if="!sys.sudoRules.length">none</template>
        </dd>
      </dl>
    </div>
    <DataTable :value="accounts" size="small" data-key="name" :row-class="(a) => (a.systemAccount ? 'row-muted' : '')">
      <Column header="Account">
        <template #body="{ data }">
          <strong>{{ data.name }}</strong> <span class="muted small">uid {{ data.uid }}</span>
          <div class="muted small mono">{{ data.home }}</div>
        </template>
      </Column>
      <Column header="Groups">
        <template #body="{ data }"
          ><span class="small">{{ data.groups.join(', ') }}</span></template
        >
      </Column>
      <Column header="Shell" style="width: 10rem">
        <template #body="{ data }"
          ><span class="mono small">{{ data.shell ?? '–' }}</span></template
        >
      </Column>
      <Column header="Password" style="width: 8rem">
        <template #body="{ data }">
          <Tag
            v-if="password(data)"
            :value="password(data)!.label"
            :severity="password(data)!.severity"
            class="small"
          />
          <span v-else class="muted small" title="/etc/shadow could not be read">unknown</span>
        </template>
      </Column>
    </DataTable>
  </section>
</template>

<style scoped>
.small {
  font-size: 0.857rem;
}

.warn {
  color: var(--pnmt-warn);
}

/* Tags after text: room between them, wrapping on a narrow screen. */
.tags {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  vertical-align: middle;
  margin-left: var(--pnmt-space-2);
}

.row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.line {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
