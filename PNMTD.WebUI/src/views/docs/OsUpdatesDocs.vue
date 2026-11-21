<script setup lang="ts">
import { computed } from 'vue'
import CodeTag from '@/components/CodeTag.vue'

/** The documentation of OS updates on Linux servers and the high-risk check (docs/linux-os-updates-plan.md §10.4). */
const props = defineProps<{ search: string }>()
const text =
  'os updates linux apt apt-get update upgrade full-upgrade install updates refresh lists pending packages security ' +
  'daily window config.yaml os_updates changelogs needrestart reboot dpkg unattended-upgrades logs retention ' +
  'high risk check advisories cve kev epss debian security tracker ubuntu notices rss ai prompt injection acknowledge ' +
  'acknowledgement 265 466 467 434 servers run manage proxmox'
const show = computed(() => {
  const q = props.search.trim().toLowerCase()
  return !q || text.includes(q)
})
const yaml = `os_updates:
  update: true        # PNMT may run apt-get update: daily, and when asked
  upgrade: true       # apt-get upgrade --with-new-pkgs, when a person asks
  full_upgrade: true  # apt-get full-upgrade, which may remove packages (Proxmox VE needs it)
  changelogs: true    # fetch the pending packages' changelogs, to find the CVEs they fix`
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="os-updates-docs-title">
    <div id="os-updates-docs-title" class="panel-header">OS updates on Linux servers</div>
    <div class="panel-body prose">
      <p>
        <strong>The Linux agent refreshes apt's lists once a day and installs updates when a person asks.</strong> A
        server's <em>OS updates</em> tab shows the pending packages, the log of runs with their full logs, and the
        buttons <em>Refresh lists</em> and <em>Install updates…</em>; the dashboard's <em>OS updates</em> card sums up
        every server.
      </p>
      <ul>
        <li>
          <strong>What runs where.</strong> The agent runs <code>apt-get update</code> once a day at a fixed time per
          server inside a window (04:00–06:00 server time by default, <code>OsUpdates__WindowStart</code> and
          <code>OsUpdates__WindowMinutes</code>), also while PNMT is unreachable; a missed slot runs at the next start.
          <code>apt-get upgrade --with-new-pkgs</code> or <code>apt-get full-upgrade</code> run only when a person
          starts them. Each run is a systemd unit of its own, outside the agent's process: an agent restart or a lost
          connection doesn't stop it, and an upgrade is never stopped in the middle of dpkg. PNMT sends only the kind
          and the mode, never a package name, an option or a command line. Local configuration files are kept.
          needrestart only reports the services that still run old libraries: PNMT restarts nothing and never reboots.
        </li>
        <li>
          <strong>On the server</strong>, <code>/etc/pnmt-agent/config.yaml</code>'s block decides what PNMT may do. A
          missing key means allowed; a <code>false</code> is final, and only root changes it. A refused file grants
          nothing.
          <pre class="yaml">{{ yaml }}</pre>
          <code>pnmt-agent os-updates</code> shows the rights, the next daily update and the last runs;
          <code>pnmt-agent os-updates log [&lt;id&gt;|last] [-f]</code> a run's log;
          <code>pnmt-agent os-updates update</code>
          refreshes the lists now.
        </li>
        <li>
          <strong>Logs</strong> are kept in full: the newest 50 update logs and the newest 200 upgrade logs per server
          (<code>OsUpdates__KeepUpdateLogs</code>, <code>OsUpdates__KeepUpgradeLogs</code>). A log longer than 8 MiB
          keeps its start and its end, with a marker. Upgrades someone ran outside PNMT (unattended-upgrades, apt on the
          shell) appear from history.log, with their packages but no full log.
        </li>
        <li>
          <strong>Events.</strong> <CodeTag :code="265" /> an upgrade PNMT ran succeeded (once per run) ·
          <CodeTag :code="466" /> a pending update fixes a high-risk vulnerability that nobody acknowledged ·
          <CodeTag :code="467" /> the last upgrade failed (until a later one succeeds or a person marks it handled),
          dpkg was interrupted, or the lists weren't refreshed for 3 days. 466 and 467 can be turned off per server in
          its settings. The system sensor <em>OS advisories</em> is <CodeTag :code="434" /> while a source of the
          high-risk check hasn't been fetched for 36 hours, or a role has no working source: a blind check is an alert
          itself.
        </li>
        <li>
          <strong>The high-risk check</strong> (off by default, <code>OsUpdates__Advisories__Enabled</code>) downloads
          public data in bulk: what an update fixes (the Debian Security Tracker, Ubuntu's security notices, and the
          changelogs the servers fetch) and how dangerous a CVE is (CISA's Known Exploited Vulnerabilities and FIRST's
          EPSS). Code matches each pending update to the CVEs it fixes and rates it: <em>high risk</em> when the CVE is
          exploited in the wild (KEV), in the top 1 % of EPSS, rated critical by the distribution, or named by a
          verified AI claim; <em>important</em> below that. Sources only add evidence: one failing can't hide what
          another shows. No package list leaves PNMT for this. The sources are under Admin → Servers → Advisories.
        </li>
        <li>
          <strong>Acknowledging.</strong> A high-risk finding that can't be installed yet can be acknowledged from the
          tab's <em>High risk</em> panel (Servers: Run), with a reason, for 1, 7 or 30 days (at most
          <code>OsUpdates__Advisories__AcknowledgeMaxDays</code>), never forever. It stays visible, greyed out, and
          leaves 466. It ends when its time runs out, when a person removes it, or earlier when the evidence gets
          stronger, for example when the CVE enters KEV. Every acknowledgement and its end are in the audit log.
        </li>
        <li>
          <strong>The AI</strong> (optional, <code>OsUpdates__Advisories__Ai</code>) reads only public advisory texts
          from RSS or Atom sources, which may say <em>exploited</em> before KEV does. It never sees server names,
          package lists, versions, the servers' changelogs or PNMT's findings, and it has no tools. It can only raise a
          rating, never lower or clear one, and it can't acknowledge anything. A claim counts only when its CVE is one
          code found in the text, its quote appears there word for word, the CVE is fixed by an update that is actually
          pending, and a second model of another vendor agrees. A text the guard flags reaches no model and is marked
          <em>suspicious</em>. Apt's logs and the changelogs never reach an AI; the UI shows every outside text as text.
        </li>
        <li>
          <strong>Rights.</strong> Monitoring: View sees the tab, the runs and their logs, the findings and the card
          (the names of people with Details). Servers: Run refreshes the lists, installs updates, marks a failure
          handled and acknowledges findings. Servers: Manage changes a server's OS update settings and the advisory
          sources. <code>OsUpdates__Enabled=false</code> turns the feature off everywhere.
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
/* On a phone: the long setting names break anywhere, and the YAML scrolls inside its box, not the page. */
.panel {
  min-width: 0;
}

.prose :deep(code) {
  overflow-wrap: anywhere;
}

.prose p {
  margin: 0 0 var(--pnmt-space-3);
  max-width: 75ch;
}

.prose ul {
  margin: 0;
  padding-left: 1.2rem;
  max-width: 75ch;
}

.prose li + li {
  margin-top: var(--pnmt-space-2);
}

.yaml {
  margin: var(--pnmt-space-2) 0;
  padding: var(--pnmt-space-2);
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  background: var(--pnmt-bg);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  white-space: pre;
  overflow-x: auto;
  max-width: 100%;
}
</style>
