<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import ProtocolPage from '@/views/sensors/udp/protocol/ProtocolPage.vue'
import { useSession } from '@/stores/session'

/**
 * The PNMT-UDP reference without a node (docs/udp-iot-plan.md §6.5): the reference key and the
 * specification's test vectors. Everyone who sees monitoring may read it; the Documentation page links it.
 */
const session = useSession()
</script>

<template>
  <div class="page">
    <PageHeader
      title="UDP protocol"
      :crumbs="
        session.can('UdpNodes', 'Manage')
          ? [{ label: 'UDP nodes', to: { name: 'udp-nodes' } }]
          : [{ label: 'Documentation', to: { name: 'docs' } }]
      "
    />
    <ProtocolPage />
  </div>
</template>
