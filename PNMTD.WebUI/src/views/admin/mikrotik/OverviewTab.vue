<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import ErrorState from '@/components/ErrorState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { fixLabel, fixSeverity } from '@/views/sensors/mikrotik/security'
import SshKeys from './SshKeys.vue'

/**
 * The overview (§11.2): whether the gateway runs, the endpoint in effect and where each part comes from,
 * peers up out of all, the routers by state and version, the latest RouterOS versions, PNMTD's SSH key, and the
 * advisories: releases with a security fix from MikroTik's feed and the routers behind them (docs/ai-operations-plan.md §10).
 */
const wg = useQuery({
  queryKey: [...keys.mikrotik, 'wireguard'],
  queryFn: api.wireGuard.status,
  refetchInterval: 15_000,
})
const mt = useQuery({ queryKey: [...keys.mikrotik, 'status'], queryFn: api.mikrotik.status, refetchInterval: 60_000 })
const releases = useQuery({
  queryKey: [...keys.mikrotik, 'releases'],
  queryFn: api.mikrotik.releases,
  refetchInterval: 300_000,
})
const assessed = (by: string) =>
  by === 'verified'
    ? 'rated critical by the AI, confirmed by the check model'
    : by === 'disputed'
      ? 'rated critical by the AI, not confirmed by the check model'
      : by === 'ai'
        ? 'rated by the AI'
        : 'assessed from the changelog'
const source = (s: string) => (s === 'Environment' ? '.env' : s === 'Ui' ? 'set below' : 'default')
const states = computed(() => Object.fromEntries((mt.data.value?.bySetupState ?? []).map((c) => [c.key, c.count])))
</script>

<template>
  <div class="overview">
    <ErrorState
      v-if="wg.isError.value && !wg.data.value"
      :error="wg.error.value"
      what="the network"
      @retry="wg.refetch()"
    />
    <section v-if="wg.data.value" class="panel" aria-labelledby="gateway-title">
      <div id="gateway-title" class="panel-header">
        WireGuard
        <Tag v-if="!wg.data.value.enabled" value="off" severity="secondary" />
        <Tag v-else-if="wg.data.value.gatewayUp" value="gateway up" severity="success" />
        <Tag v-else value="gateway down" severity="danger" />
      </div>
      <div class="panel-body">
        <dl class="facts">
          <dt>Endpoint</dt>
          <dd>
            <span class="mono">{{ wg.data.value.endpoint ?? 'unknown' }}</span>
            <span class="muted small">
              host: {{ source(wg.data.value.externalHostSource) }}, port:
              {{ source(wg.data.value.externalPortSource) }}; published on {{ wg.data.value.publishedPort }}/udp</span
            >
          </dd>
          <dt>Gateway</dt>
          <dd>
            <template v-if="wg.data.value.gatewaySeenAt">
              seen <RelativeTime :value="wg.data.value.gatewaySeenAt" /> · {{ wg.data.value.gatewayVersion }}
            </template>
            <span v-else class="muted">never seen</span>
            <div v-if="wg.data.value.gatewayError" class="bad small">{{ wg.data.value.gatewayError }}</div>
          </dd>
          <dt>Network</dt>
          <dd class="mono">
            {{ wg.data.value.network }} · server {{ wg.data.value.serverAddress }} · access
            {{ wg.data.value.accessAddress }}
          </dd>
          <dt>Tunnels</dt>
          <dd>{{ wg.data.value.peersUp }} of {{ wg.data.value.peers }} up</dd>
          <dt>Public key</dt>
          <dd><CopyField :value="wg.data.value.gatewayPublicKey" label="Gateway's public key" /></dd>
        </dl>
        <Message v-for="w in wg.data.value.warnings" :key="w" severity="warn" :closable="false" size="small">{{
          w
        }}</Message>
      </div>
    </section>

    <section v-if="mt.data.value" class="panel" aria-labelledby="routers-title">
      <div id="routers-title" class="panel-header">MikroTik routers</div>
      <div class="panel-body">
        <dl class="facts">
          <dt>Routers</dt>
          <dd>
            {{ mt.data.value.routers }} · {{ states.Ready ?? 0 }} ready · {{ states.AwaitingRouter ?? 0 }} waiting for
            their script · {{ states.NeedsAttention ?? 0 }} need attention
          </dd>
          <dt>Updates</dt>
          <dd>
            {{ mt.data.value.updatesAvailable }} behind on their channel · {{ mt.data.value.scriptsOutdated }} with an
            outdated script
          </dd>
          <dt>Versions</dt>
          <dd>
            <span v-for="v in mt.data.value.byVersion" :key="v.key" class="chip mono">{{ v.key }} × {{ v.count }}</span>
            <span v-if="!mt.data.value.byVersion.length" class="muted">none read yet</span>
          </dd>
          <dt>Latest RouterOS</dt>
          <dd>
            <span v-for="c in mt.data.value.latestVersions" :key="c.channel" class="chip"
              >{{ c.channel }} <span class="mono">{{ c.version }}</span></span
            >
            <div class="muted small">
              <template v-if="mt.data.value.versionsCheckedAt">
                checked <RelativeTime :value="mt.data.value.versionsCheckedAt" /> at upgrade.mikrotik.com
              </template>
              <template v-else-if="mt.data.value.versionCheckEnabled">not checked yet</template>
              <template v-else>the check is off (Mikrotik__VersionCheck__Enabled)</template>
            </div>
            <div v-if="mt.data.value.versionCheckError" class="bad small">{{ mt.data.value.versionCheckError }}</div>
          </dd>
          <dt>PNMTD's SSH key</dt>
          <dd><SshKeys /></dd>
          <dt>Tag</dt>
          <dd class="mono">
            pnmt:{{ mt.data.value.tag }} <span class="muted small">(the comment on what join scripts create)</span>
          </dd>
        </dl>
      </div>
    </section>
    <section class="panel" aria-labelledby="advisories-title">
      <div id="advisories-title" class="panel-header">Advisories</div>
      <div class="panel-body">
        <ErrorState
          v-if="releases.isError.value"
          :error="releases.error.value"
          what="the advisories"
          @retry="releases.refetch()"
        />
        <p v-else-if="!releases.data.value?.length" class="muted none">
          No RouterOS release with a security fix seen in MikroTik's feed.
        </p>
        <ul v-else class="releases">
          <li v-for="r in releases.data.value" :key="`${r.version}-${r.channel}`">
            <div class="line">
              <Tag :value="fixLabel(r.severity)" :severity="fixSeverity(r.severity)" icon="pi pi-shield" />
              <span class="mono">{{ r.version }}</span> <span class="muted">{{ r.channel }}</span>
              <span v-if="r.publishedAt" class="muted small"><RelativeTime :value="r.publishedAt" /></span>
              <Tag v-if="r.zeroDay" value="zero-day" severity="danger" />
              <Tag v-if="r.exploited" value="exploited" severity="danger" />
              <Tag v-if="r.remoteWithoutCredentials" value="remote, no login" severity="danger" />
            </div>
            <div v-if="r.cves.length" class="small mono">{{ r.cves.join(', ') }}</div>
            <div v-if="r.summary" class="small">{{ r.summary }}</div>
            <ul v-if="r.securityLines.length" class="small lines">
              <li v-for="l in r.securityLines" :key="l" class="mono">{{ l }}</li>
            </ul>
            <div class="small">
              <template v-if="r.routersBehind.length">
                <span :class="r.severity === 'Critical' ? 'bad' : ''">{{ r.routersBehind.length }} behind:</span>
                <template v-for="(b, i) in r.routersBehind" :key="b.id">
                  <RouterLink :to="{ name: 'sensor', params: { id: b.id } }">{{ b.name }}</RouterLink>{{
                    i < r.routersBehind.length - 1 ? ', ' : ''
                  }}
                </template>
              </template>
              <span v-else class="muted">every router on {{ r.channel }} has it</span>
              <span class="muted"> · {{ assessed(r.assessedBy) }}</span>
            </div>
          </li>
        </ul>
      </div>
    </section>
  </div>
</template>

<style scoped>
.overview {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.panel-header {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.panel-body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.mono {
  font-family: var(--pnmt-mono);
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}

.bad {
  color: var(--pnmt-fail);
}

.releases {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.lines {
  margin: var(--pnmt-space-1) 0;
  padding-left: var(--pnmt-space-4);
  color: var(--pnmt-fail);
}

.none {
  margin: 0;
}

.chip {
  display: inline-block;
  margin: 0 var(--pnmt-space-2) var(--pnmt-space-1) 0;
  padding: 0 var(--pnmt-space-2);
  border-radius: var(--p-border-radius-sm);
  background: var(--pnmt-none-soft);
}
</style>
