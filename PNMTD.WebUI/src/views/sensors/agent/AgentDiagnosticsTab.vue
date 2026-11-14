<script setup lang="ts">
import type { S } from '@/api/resources'
import AssignmentsList from '@/views/admin/diagnostics/AssignmentsList.vue'
import RunsTab from '@/views/admin/diagnostics/RunsTab.vue'

/**
 * A server's diagnostics (docs/windows-sensor-plan.md §10.2; docs/linux-sensor-plan.md §7, the same on both platforms): its
 * assignments with their last results, Run now, the history.
 */
defineProps<{ server: S['AgentServerDetail']; canWrite: boolean; isAdmin: boolean }>()
</script>

<template>
  <div class="tab">
    <section class="panel">
      <div class="panel-header">Assignments</div>
      <div class="panel-body">
        <AssignmentsList :target-sensor-id="server.server.sensorId" :can-write="canWrite" :is-admin="isAdmin" />
      </div>
    </section>
    <section class="panel">
      <div class="panel-header">History</div>
      <div class="panel-body"><RunsTab :target-sensor-id="server.server.sensorId" /></div>
    </section>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
</style>
