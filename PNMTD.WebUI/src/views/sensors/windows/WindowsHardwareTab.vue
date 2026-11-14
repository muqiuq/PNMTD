<script setup lang="ts">
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import type { S } from '@/api/resources'
import { bytes } from '@/views/admin/windows/windowsFormat'
import SectionBar from './SectionBar.vue'
import { useWindowsSection } from './useWindowsSection'

/** A server's hardware (docs/windows-sensor-plan.md §4.3): the machine, CPUs, memory, disks with their health, volumes, GPUs. */
const props = defineProps<{ server: S['WindowsServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const section = useWindowsSection(id, 'hardware')
const hw = computed(() => section.data.value?.hardware ?? null)
const healthSeverity = (h: string | null | undefined) =>
  h === 'Healthy' ? 'success' : h === 'Warning' ? 'warn' : 'danger'
</script>

<template>
  <div class="tab">
    <SectionBar
      :sensor-id="id"
      section="hardware"
      :view="section.data.value"
      :loading="section.loading.value"
      :error="section.error.value"
      :can-write="canWrite"
      :connected="server.server.connected"
    />
    <template v-if="hw">
      <div class="two-columns">
        <section class="panel">
          <div class="panel-header">Machine</div>
          <dl class="panel-body facts">
            <dt>Manufacturer</dt>
            <dd>{{ hw.machine.manufacturer ?? '–' }}</dd>
            <dt>Model</dt>
            <dd>{{ hw.machine.model ?? '–' }}</dd>
            <dt>Serial number</dt>
            <dd class="mono">{{ hw.machine.serialNumber ?? '–' }}</dd>
            <dt>Firmware</dt>
            <dd>
              {{ hw.machine.firmware ?? '–' }} {{ hw.machine.biosVersion }}
              <span v-if="hw.machine.biosDate" class="muted">({{ hw.machine.biosDate.slice(0, 10) }})</span>
            </dd>
            <dt>TPM</dt>
            <dd>
              <template v-if="hw.tpm?.present"
                >{{ hw.tpm.version }} · {{ hw.tpm.ready ? 'ready' : 'not ready' }}</template
              >
              <template v-else>none</template>
            </dd>
            <dt>Memory</dt>
            <dd>{{ bytes(hw.memory.totalBytes) }} in {{ hw.memory.modules.length }} modules</dd>
          </dl>
        </section>
        <section class="panel">
          <div class="panel-header">Processors</div>
          <DataTable :value="hw.processors" size="small">
            <Column field="name" header="CPU" />
            <Column header="Cores / threads" style="width: 9rem">
              <template #body="{ data }">{{ data.cores ?? '–' }} / {{ data.threads ?? '–' }}</template>
            </Column>
            <Column header="Clock" style="width: 7rem">
              <template #body="{ data }">{{ data.maxClockMhz ? `${data.maxClockMhz} MHz` : '–' }}</template>
            </Column>
          </DataTable>
        </section>
      </div>
      <section class="panel">
        <div class="panel-header">Disks</div>
        <DataTable :value="hw.disks" size="small">
          <Column field="model" header="Disk" />
          <Column header="Size" style="width: 7rem"
            ><template #body="{ data }">{{ bytes(data.sizeBytes) }}</template></Column
          >
          <Column header="Media" style="width: 8rem">
            <template #body="{ data }">{{ data.media ?? '–' }} · {{ data.bus ?? '–' }}</template>
          </Column>
          <Column header="Health" style="width: 12rem">
            <template #body="{ data }">
              <div class="tag-line">
                <Tag :value="data.health ?? 'Unknown'" :severity="healthSeverity(data.health)" />
                <span class="muted small">{{ data.operationalStatus }}</span>
                <Tag v-if="data.predictFailure" value="SMART predicts failure" severity="danger" />
                <span
                  v-else-if="data.predictFailure === null"
                  class="muted small"
                  title="The disk's driver doesn't report SMART (usual in a VM)"
                  >SMART unknown</span
                >
              </div>
            </template>
          </Column>
        </DataTable>
      </section>
      <section class="panel">
        <div class="panel-header">Volumes</div>
        <DataTable :value="hw.volumes" size="small">
          <Column header="Volume" style="width: 10rem">
            <template #body="{ data }"
              >{{ data.letter }} <span class="muted">{{ data.label }}</span></template
            >
          </Column>
          <Column field="fileSystem" header="File system" style="width: 8rem" />
          <Column header="Free">
            <template #body="{ data }">
              {{ bytes(data.freeBytes) }} of {{ bytes(data.sizeBytes) }}
              <span class="muted"
                >({{ data.sizeBytes ? Math.round((100 * (data.freeBytes ?? 0)) / data.sizeBytes) : '–' }} %)</span
              >
            </template>
          </Column>
          <Column header="BitLocker" style="width: 8rem">
            <template #body="{ data }">{{ data.bitLocker ?? '–' }}</template>
          </Column>
        </DataTable>
      </section>
      <section v-if="hw.gpus.length || hw.batteries.length" class="panel">
        <div class="panel-header">Graphics and batteries</div>
        <ul class="panel-body plain">
          <li v-for="(g, i) in hw.gpus" :key="'g' + i">
            {{ g.name }} <span class="muted">{{ g.driverVersion }}</span>
          </li>
          <li v-for="(b, i) in hw.batteries" :key="'b' + i">
            {{ b.name }}: {{ b.chargePercent ?? '–' }} % · {{ b.status ?? '' }}
          </li>
        </ul>
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

.plain {
  list-style: none;
  margin: 0;
}

.small {
  font-size: 0.857rem;
}

.tag-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-2);
}
</style>
