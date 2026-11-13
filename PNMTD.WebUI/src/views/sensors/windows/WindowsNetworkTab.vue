<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import { speed } from '@/views/admin/windows/windowsFormat'
import SectionBar from './SectionBar.vue'
import { useWindowsSection } from './useWindowsSection'

/**
 * A server's network in detail (docs/windows-sensor-plan.md §4.4): adapters with addresses, gateways and DNS, routes,
 * listening ports with their processes and services, the firewall and its inbound rules for those ports, the proxy.
 */
const props = defineProps<{ server: S['WindowsServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useWindowsSection(id, 'network')
const net = computed(() => section.data.value?.network ?? null)
const connections = computed(() =>
  Object.entries(net.value?.connectionsByState ?? {})
    .map(([state, count]) => `${count} ${state}`)
    .join(' · '),
)
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="network"
      :view="section.data.value"
      :loading="section.loading.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <template v-if="net">
      <section class="panel">
        <div class="panel-header">Adapters</div>
        <DataTable :value="net.adapters" size="small">
          <Column header="Adapter" style="width: 16rem">
            <template #body="{ data }">
              <strong>{{ data.name }}</strong>
              <Tag :value="data.status" :severity="data.status === 'Up' ? 'success' : 'secondary'" class="small" />
              <div class="muted small">{{ data.description }}</div>
              <div class="muted small">
                {{ data.kind }} · {{ speed(data.speedBps)
                }}<template v-if="data.vlanId"> · VLAN {{ data.vlanId }}</template
                ><template v-if="data.profile"> · {{ data.profile }}</template>
              </div>
            </template>
          </Column>
          <Column header="Addresses">
            <template #body="{ data }">
              <div v-for="a in data.addresses" :key="a" class="mono small">{{ a }}</div>
              <div class="muted small">{{ data.dhcp ? `DHCP ${data.dhcpServer ?? ''}` : 'static' }}</div>
            </template>
          </Column>
          <Column header="Gateways and DNS">
            <template #body="{ data }">
              <div v-for="g in data.gateways" :key="g" class="mono small">gw {{ g }}</div>
              <div v-for="d in data.dnsServers" :key="d" class="mono small muted">dns {{ d }}</div>
              <div v-if="data.dnsSuffix" class="small muted">{{ data.dnsSuffix }}</div>
            </template>
          </Column>
          <Column header="MAC" style="width: 10rem">
            <template #body="{ data }"
              ><span class="mono small">{{ data.mac }}</span></template
            >
          </Column>
        </DataTable>
      </section>
      <div class="two-columns">
        <section class="panel">
          <div class="panel-header">Listening ports</div>
          <DataTable :value="net.listeners" size="small" scrollable scroll-height="28rem">
            <Column header="Port" style="width: 7rem">
              <template #body="{ data }"
                ><span class="mono">{{ data.port }}</span>
                <span class="muted small">{{ data.protocol }}</span></template
              >
            </Column>
            <Column header="Address" style="width: 9rem">
              <template #body="{ data }"
                ><span class="mono small">{{ data.address }}</span></template
              >
            </Column>
            <Column header="Process">
              <template #body="{ data }">
                {{ data.process ?? '–' }}
                <span v-if="data.services.length" class="muted small">({{ data.services.join(', ') }})</span>
              </template>
            </Column>
          </DataTable>
          <div class="panel-body muted small">Connections: {{ connections || '–' }}</div>
        </section>
        <section class="panel">
          <div class="panel-header">Firewall</div>
          <div class="panel-body">
            <div class="profiles">
              <Tag
                v-for="p in net.firewall?.profiles ?? []"
                :key="p.profile"
                :value="`${p.profile}: ${p.enabled ? 'on' : 'OFF'}${p.active ? ' (active)' : ''}`"
                :severity="p.enabled ? 'success' : 'danger'"
              />
            </div>
            <p class="muted small">Enabled inbound rules for the ports that listen:</p>
          </div>
          <DataTable :value="net.firewall?.inboundRules ?? []" size="small" scrollable scroll-height="22rem">
            <Column field="name" header="Rule" />
            <Column header="Ports" style="width: 8rem">
              <template #body="{ data }"
                ><span class="mono small">{{ data.protocol }} {{ data.ports }}</span></template
              >
            </Column>
            <Column field="profiles" header="Profiles" style="width: 8rem" />
          </DataTable>
        </section>
      </div>
      <section class="panel">
        <div class="panel-header">Routes</div>
        <DataTable :value="net.routes" size="small">
          <Column header="Destination"
            ><template #body="{ data }"
              ><span class="mono">{{ data.destination }}</span></template
            ></Column
          >
          <Column header="Gateway"
            ><template #body="{ data }"
              ><span class="mono">{{ data.gateway }}</span></template
            ></Column
          >
          <Column field="interface" header="Interface" />
          <Column field="metric" header="Metric" style="width: 6rem" />
          <Column header="" style="width: 7rem">
            <template #body="{ data }"><span v-if="data.persistent" class="muted small">persistent</span></template>
          </Column>
        </DataTable>
      </section>
      <section
        v-if="net.teams.length || net.virtualSwitches.length || net.proxy || net.hostsEntries.length"
        class="panel"
      >
        <div class="panel-header">More</div>
        <dl class="panel-body facts">
          <template v-if="net.teams.length">
            <dt>NIC teams</dt>
            <dd>
              <div v-for="t in net.teams" :key="t.name">{{ t.name }} ({{ t.mode }}): {{ t.members.join(', ') }}</div>
            </dd>
          </template>
          <template v-if="net.virtualSwitches.length">
            <dt>Hyper-V switches</dt>
            <dd>{{ net.virtualSwitches.join(', ') }}</dd>
          </template>
          <dt>Proxy (WinHTTP)</dt>
          <dd class="mono small">{{ net.proxy ?? 'direct' }}</dd>
          <template v-if="net.hostsEntries.length">
            <dt>Hosts file</dt>
            <dd>
              <div v-for="h in net.hostsEntries" :key="h" class="mono small">{{ h }}</div>
            </dd>
          </template>
        </dl>
      </section>
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.profiles {
  display: flex;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.small {
  font-size: 0.857rem;
}
</style>
