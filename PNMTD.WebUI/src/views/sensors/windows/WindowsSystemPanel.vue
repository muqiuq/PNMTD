<script setup lang="ts">
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import SectionBar from './SectionBar.vue'
import { useWindowsSection } from './useWindowsSection'

/**
 * The <c>system</c> section on the overview (docs/windows-sensor-plan.md §4.2): Windows, the domain, the last shutdown,
 * updates and a pending reboot, time, Defender, the firewall, BitLocker, Secure Boot, the local administrators.
 */
const props = defineProps<{ server: S['WindowsServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useWindowsSection(id, 'system')
const sys = computed(() => section.data.value?.system ?? null)
const activation = computed(() => sys.value?.os.activation ?? null)

const activationLabel = (status: string) => (status === 'Licensed' ? 'activated' : status.toLowerCase())
const activationSeverity = (status: string) =>
  status === 'Licensed' ? 'success' : status === 'Unlicensed' || status === 'Notification' ? 'danger' : 'warn'
/** The key's channel (ProductKeyChannel): how Windows was licensed. */
const channelLabel = (channel: string | null | undefined) => {
  if (!channel) return 'Product'
  if (channel === 'Retail') return 'Retail'
  if (channel === 'Volume:GVLK') return 'KMS client (GVLK)'
  if (channel === 'Volume:MAK') return 'Volume (MAK)'
  if (channel.startsWith('OEM')) return `OEM (${channel.slice(4) || 'preinstalled'})`
  return channel
}
const graceText = (a: S['ActivationInfo']) => {
  const minutes = a.graceMinutes ?? 0
  const left = minutes >= 2 * 1440 ? `${Math.round(minutes / 1440)} days` : `${Math.round(minutes / 60)} hours`
  // Licensed with time left: a KMS (or MAK-less volume) activation, valid that long unless renewed from the KMS host.
  return a.status === 'Licensed' ? `Valid for ${left} more; the KMS host renews it` : `${left} left in the grace period`
}
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
        :can-write="canWrite"
        :connected="server.server.connected"
      />
      <dl v-if="sys" class="facts">
        <dt>Windows</dt>
        <dd>
          {{ sys.os.caption }} {{ sys.os.displayVersion }} ({{ sys.os.build
          }}<template v-if="sys.os.ubr">.{{ sys.os.ubr }}</template
          >)
          <span v-if="!sys.os.supported" class="tags"><Tag value="not a supported version" severity="warn" /></span>
        </dd>
        <dt>Activation</dt>
        <dd>
          <template v-if="activation">
            <div class="line">
              <Tag :value="activationLabel(activation.status)" :severity="activationSeverity(activation.status)" />
              <span v-if="activation.partialKey">
                {{ channelLabel(activation.channel) }} key <span class="mono">…{{ activation.partialKey }}</span>
              </span>
            </div>
            <div class="muted small">
              <div v-if="activation.description">{{ activation.description }}</div>
              <div v-if="activation.kmsHost">
                KMS host <span class="mono">{{ activation.kmsHost }}</span>
              </div>
              <div v-if="activation.graceMinutes">{{ graceText(activation) }}</div>
              <div v-if="activation.evaluationEnds">
                Evaluation ends <RelativeTime :value="activation.evaluationEnds" />
              </div>
              <div v-if="activation.firmwareKey">
                <template v-if="activation.firmwareKey === activation.partialKey"
                  >The installed key is the one in the firmware.</template
                >
                <template v-else
                  >The firmware holds an OEM key <span class="mono">…{{ activation.firmwareKey }}</span
                  >; another key is installed.</template
                >
              </div>
              <div v-if="activation.reason">
                Licensing error <span class="mono">{{ activation.reason }}</span>
              </div>
            </div>
          </template>
          <Tag v-else-if="sys.os.activated === false" value="not activated" severity="warn" />
          <template v-else-if="sys.os.activated">activated</template>
          <template v-else>–</template>
        </dd>
        <dt>Domain</dt>
        <dd>{{ sys.partOfDomain ? sys.domain : `workgroup ${sys.domain ?? ''}` }} · {{ sys.domainRole ?? '' }}</dd>
        <dt>Last shutdown</dt>
        <dd>
          <template v-if="sys.lastShutdown">
            <RelativeTime :value="sys.lastShutdown.at" />:
            {{ sys.lastShutdown.unexpected ? 'unexpected' : sys.lastShutdown.reason }}
            <span v-if="sys.lastShutdown.user" class="muted">by {{ sys.lastShutdown.user }}</span>
          </template>
          <template v-else>–</template>
        </dd>
        <dt>Updates</dt>
        <dd>
          {{ sys.updates?.lastHotfix ?? '–' }}
          <span v-if="sys.updates?.lastHotfixAt" class="muted"><RelativeTime :value="sys.updates.lastHotfixAt" /></span>
          <span v-if="sys.updates?.rebootPending" class="tags">
            <Tag :value="`reboot pending: ${sys.updates.rebootReasons.join(', ')}`" severity="warn" />
          </span>
        </dd>
        <dt>Time</dt>
        <dd>{{ sys.time?.timeZone }} · source {{ sys.time?.source ?? '–' }}</dd>
        <dt>Defender</dt>
        <dd>
          <template v-if="sys.security?.defender">
            real-time {{ sys.security.defender.realTimeProtection ? 'on' : 'OFF' }} · signatures
            {{ sys.security.defender.signatureAgeDays ?? '–' }} days old
          </template>
          <template v-else>–</template>
        </dd>
        <dt>Firewall</dt>
        <dd>
          <span v-if="sys.security?.firewall.length" class="tags">
            <Tag
              v-for="p in sys.security.firewall"
              :key="p.profile"
              :value="`${p.profile} ${p.enabled ? 'on' : 'OFF'}`"
              :severity="p.enabled ? 'success' : 'danger'"
              :title="`${p.profile} profile${p.active ? ' (active)' : ''}: inbound ${p.defaultInbound ?? '?'} by default`"
            />
          </span>
          <template v-else>–</template>
        </dd>
        <dt>BitLocker</dt>
        <dd>
          <span v-for="b in sys.security?.bitLocker ?? []" :key="b.volume">{{ b.volume }} {{ b.protection }} </span>
          <template v-if="!sys.security?.bitLocker.length">–</template>
        </dd>
        <dt>Secure Boot</dt>
        <dd>{{ sys.security?.secureBoot === null ? '–' : sys.security?.secureBoot ? 'on' : 'off' }}</dd>
        <dt>Virtualisation</dt>
        <dd>{{ sys.hypervisor?.vendor ?? (sys.hypervisor?.present ? 'a hypervisor' : 'none') }}</dd>
        <dt>Local administrators</dt>
        <dd>
          <div v-for="a in sys.localAdministrators" :key="a.name">
            {{ a.name }} <span class="muted small">{{ a.kind.toLowerCase() }}</span>
          </div>
        </dd>
      </dl>
    </div>
  </section>
</template>

<style scoped>
.small {
  font-size: 0.857rem;
}

/* Tags beside each other or after text: room between them, wrapping on a narrow screen. */
.tags {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1);
  vertical-align: middle;
}

.tags:not(:first-child) {
  margin-left: var(--pnmt-space-2);
}

.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
