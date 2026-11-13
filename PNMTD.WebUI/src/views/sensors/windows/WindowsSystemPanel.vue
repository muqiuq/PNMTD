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
          <Tag v-if="!sys.os.supported" value="not a supported version" severity="warn" />
          <Tag v-if="sys.os.activated === false" value="not activated" severity="warn" />
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
          <Tag
            v-if="sys.updates?.rebootPending"
            :value="`reboot pending: ${sys.updates.rebootReasons.join(', ')}`"
            severity="warn"
          />
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
          <Tag
            v-for="p in sys.security?.firewall ?? []"
            :key="p.profile"
            :value="`${p.profile} ${p.enabled ? 'on' : 'OFF'}`"
            :severity="p.enabled ? 'success' : 'danger'"
          />
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
</style>
